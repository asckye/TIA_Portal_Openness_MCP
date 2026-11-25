using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using TiaOpenness.Shared;

namespace TiaMcp.WriteGuard;

internal static class Program
{
    private static int Main()
    {
        Console.InputEncoding = new UTF8Encoding(false);
        Console.OutputEncoding = new UTF8Encoding(false);
        var environment = Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
            .Where(item => item.Key is string && item.Value is string)
            .ToDictionary(item => (string)item.Key, item => (string?)item.Value, StringComparer.OrdinalIgnoreCase);
        string raw = Console.In.ReadToEnd();
        GuardHost.Run(raw, environment, AppContext.BaseDirectory, Console.Out, Console.Error);
        return 0;
    }
}

public static class GuardHost
{
    private const string UnknownMessage =
        "TIA write guard: '{0}' is unknown; the tool list is out of date. Regenerate manifest/tools-list.json from the current engines (or set " +
        "TIA_MCP_WRITE_GUARD=0 to disable the guard).";
    private static readonly HashSet<string> SafeOperations = new(StringComparer.OrdinalIgnoreCase) { "READ", "SESSION", "OFFLINE" };
    private static readonly HashSet<string> KnownOperations = new(StringComparer.OrdinalIgnoreCase)
        { "READ", "SESSION", "OFFLINE", "WRITE", "FILE", "EXECUTE", "ONLINE", "ONLINE-WRITE" };
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    public static string ProcessInput(string raw, IReadOnlyDictionary<string, string?> environment, string baseDirectory, TextWriter? error = null)
    {
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        Run(raw, environment, baseDirectory, output, error);
        return output.ToString();
    }

    public static void Run(string raw, IReadOnlyDictionary<string, string?> environment, string baseDirectory, TextWriter output, TextWriter? error = null)
    {
        var audit = new List<AuditEntry>();
        string decision = Classify(raw, environment, baseDirectory, error, audit, out var payload);
        if (decision.Length > 0)
        {
            output.Write(decision);
            output.Flush();
        }
        if (audit.Count == 0) return;
        string? auditPath = GetEnvironment(environment, "TIA_MCP_AUDIT_LOG");
        string? localAppData = GetEnvironment(environment, "LOCALAPPDATA");
        foreach (var entry in audit) Audit(entry, payload, auditPath, localAppData);
    }

    private static string Classify(string raw, IReadOnlyDictionary<string, string?> environment, string baseDirectory, TextWriter? error,
        List<AuditEntry> audit, out JsonObject? payload)
    {
        payload = null;
        if (IsMatch(GetEnvironment(environment, "TIA_MCP_WRITE_GUARD"), "^(0|off|false)$")) return "";
        if (string.IsNullOrWhiteSpace(raw)) return "";

        string name = "";
        JsonNode? input = null;
        try
        {
            payload = JsonNode.Parse(raw) as JsonObject;
            if (payload == null) return "";
            string toolName = NodeString(GetProperty(payload, "tool_name"));
            Match hookName = Regex.Match(toolName, ToolNames.HookMatcher, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (!hookName.Success) return "";
            name = hookName.Groups[1].Value;
            input = GetProperty(payload, "tool_input");

            string root = GetEnvironment(environment, "CLAUDE_PLUGIN_ROOT") ?? "";
            if (string.IsNullOrWhiteSpace(root)) root = Path.GetFullPath(Path.Combine(baseDirectory, "..", ".."));
            if (!toolName.StartsWith("mcp__tia-portal__", StringComparison.OrdinalIgnoreCase))
            {
                var plugin = JsonNode.Parse(File.ReadAllText(Path.Combine(root, ".claude-plugin", "plugin.json")));
                string pattern = ToolNames.Pattern(NodeString(plugin?["name"]));
                if (!Regex.IsMatch(toolName, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) return "";
            }
            var entries = ReadEntries(Path.Combine(root, "manifest", "tools-list.json"));
            var deny = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "ONLINE-WRITE" };
            foreach (string extra in (GetEnvironment(environment, "TIA_MCP_GUARD_DENY_OPERATIONS") ?? "").Split(','))
                if (!string.IsNullOrWhiteSpace(extra)) deny.Add(extra.Trim());
            bool allowed = IsMatch(GetEnvironment(environment, "TIA_MCP_ALLOW_ONLINE_WRITE"), "^(1|true|yes)$");
            var reasons = Inspect(name, input, false, false, 0, entries, deny, allowed, audit);
            return reasons.Count == 0 ? "" : Deny(string.Join(" ", reasons));
        }
        catch (Exception ex)
        {
            if (IsTruthy(GetEnvironment(environment, "TIA_MCP_GUARD_DEBUG")))
                error?.WriteLine("tia-write-guard: " + ex.Message);
            if (name.Length == 0) return "";
            audit.Add(new AuditEntry(name, input, "UNKNOWN", "", false));
            return Deny($"TIA write guard: '{name}' cannot be classified; the tool list is out of date or unreadable. Regenerate manifest/tools-list.json (or set TIA_MCP_WRITE_GUARD=0 to disable the guard).");
        }
    }

    private static Dictionary<string, ToolEntry> ReadEntries(string path)
    {
        var entries = new Dictionary<string, ToolEntry>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path)) return entries;
        var document = JsonNode.Parse(File.ReadAllText(path, Encoding.UTF8));
        if (GetProperty(document as JsonObject, "tools") is not JsonArray tools) return entries;
        foreach (var node in tools)
        {
            if (node is not JsonObject item) continue;
            string name = NodeString(GetProperty(item, "name"));
            if (entries.ContainsKey(name)) throw new InvalidDataException("Duplicate tool list entry");
            var parameters = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (GetProperty(item, "parameters") is JsonArray values)
                foreach (var value in values) parameters.Add(NodeString(value));
            entries.Add(name, new ToolEntry(NodeString(GetProperty(item, "operation")),
                NodeString(GetProperty(item, "category")), parameters));
        }
        return entries;
    }

    private static List<string> Inspect(string target, JsonNode? arguments, bool preview, bool apply, int depth,
        IReadOnlyDictionary<string, ToolEntry> entries, HashSet<string> deny, bool allowed, List<AuditEntry> audit)
    {
        if (!entries.TryGetValue(target, out var entry) || !KnownOperations.Contains(entry.Operation))
        {
            audit.Add(new AuditEntry(target, arguments, "UNKNOWN", "", preview));
            return [string.Format(CultureInfo.InvariantCulture, UnknownMessage, target)];
        }
        if (depth >= 20) return [$"TIA write guard: '{target}' exceeds the dispatch nesting limit."];
        if (arguments == null) arguments = new JsonObject();
        if (arguments is not JsonObject argumentObject) return [$"TIA write guard: '{target}' arguments must be a V4 object."];

        bool hasDryRun = entry.Parameters.Contains("dryRun");
        JsonNode? dryRunValue = GetProperty(argumentObject, "dryRun");
        bool isFalseBoolean = dryRunValue is JsonValue dryRunJson && dryRunJson.TryGetValue<bool>(out bool booleanValue) && !booleanValue;
        string dryRunText = NodeString(dryRunValue);
        bool isPreview = preview || (!apply && hasDryRun && !isFalseBoolean && !IsMatch(dryRunText, "^(false|0)$"));
        if (!SafeOperations.Contains(entry.Operation)) audit.Add(new AuditEntry(target, argumentObject, entry.Operation, entry.Category, isPreview));

        var reasons = new List<string>();
        if (target.Equals("CallTool", StringComparison.OrdinalIgnoreCase) || target.Equals("PreviewToolCall", StringComparison.OrdinalIgnoreCase))
        {
            string child = NodeString(GetProperty(argumentObject, "name"));
            reasons.AddRange(Inspect(child, GetProperty(argumentObject, "arguments"),
                preview || target.Equals("PreviewToolCall", StringComparison.OrdinalIgnoreCase), apply, depth + 1,
                entries, deny, allowed, audit));
        }
        else if (target.Equals("RunReadOnlyToolBatch", StringComparison.OrdinalIgnoreCase) ||
                 target.Equals("PreviewToolBatch", StringComparison.OrdinalIgnoreCase) ||
                 target.Equals("ApplyToolBatch", StringComparison.OrdinalIgnoreCase))
        {
            foreach (JsonNode? call in Enumerate(GetProperty(argumentObject, "operations")))
            {
                if (call == null) continue;
                var callObject = call as JsonObject;
                string child = NodeString(GetProperty(callObject, "name"));
                reasons.AddRange(Inspect(child, GetProperty(callObject, "arguments"),
                    preview || target.Equals("PreviewToolBatch", StringComparison.OrdinalIgnoreCase),
                    apply || target.Equals("ApplyToolBatch", StringComparison.OrdinalIgnoreCase), depth + 1,
                    entries, deny, allowed, audit));
            }
            if (target.Equals("ApplyToolBatch", StringComparison.OrdinalIgnoreCase) && !preview && !allowed)
                reasons
                    .Add("TIA write guard: 'ApplyToolBatch' has an opaque stored plan; its targets cannot be classified. Set TIA_MCP_ALLOW_ONLINE_WRITE=1 to " +
                    "execute the batch.");
        }
        if (deny.Contains(entry.Operation) && !isPreview && !allowed)
            reasons.Add($"TIA write guard: '{target}' is a {entry.Operation} operation on a real controller/runtime/simulation. " +
                (hasDryRun ? "Run it with dryRun=true first; to execute, " : "To execute, ") +
                "set TIA_MCP_ALLOW_ONLINE_WRITE=1 in the environment of this Claude Code session (or TIA_MCP_WRITE_GUARD=0 to disable the guard). Every " +
                    "write is recorded in the audit log.");
        return reasons;
    }

    private static IEnumerable<JsonNode?> Enumerate(JsonNode? value)
    {
        if (value is JsonArray array) return array;
        return value == null ? Array.Empty<JsonNode?>() : new JsonNode?[] { value };
    }

    private sealed record AuditEntry(string Tool, JsonNode? Arguments, string Operation, string Category, bool Preview);

    private static void Audit(AuditEntry entry, JsonObject? payload, string? auditPath, string? localAppData)
    {
        try
        {
            string path = string.IsNullOrWhiteSpace(auditPath)
                ? Path.Combine(localAppData ?? throw new InvalidOperationException("LOCALAPPDATA is unavailable"), "TiaMcpServer", "audit", "tool-calls.jsonl")
                : auditPath;
            string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            var line = new JsonObject
            {
                ["timestamp"] = DateTimeOffset.Now.ToString("o", CultureInfo.InvariantCulture),
                ["session"] = NodeString(GetProperty(payload, "session_id")),
                ["cwd"] = NodeString(GetProperty(payload, "cwd")),
                ["tool"] = entry.Tool,
                ["category"] = entry.Category,
                ["operation"] = entry.Operation,
                ["preview"] = entry.Preview,
                ["arguments"] = AuditArguments(entry.Arguments),
            };
            File.AppendAllText(path, line.ToJsonString(JsonOptions) + Environment.NewLine, new UTF8Encoding(false));
        }
        catch /* swallow(fail-open-guard): audit failures must not bypass the deny policy */ { }
    }

    private static JsonNode? AuditArguments(JsonNode? arguments)
    {
        try { return LimitArguments(CallJournalPayload.RedactGuardArguments(arguments), 0); }
        catch /* swallow(privacy): retain the audit event even when its arguments cannot be masked */ { return JsonValue.Create("<redacted>"); }
    }

    private static JsonNode? LimitArguments(JsonNode? value, int depth)
    {
        if (value == null) return null;
        if (depth >= 20) return JsonValue.Create("<truncated>");
        if (value is JsonObject obj)
        {
            var result = new JsonObject();
            foreach (var pair in obj)
                result[pair.Key] = LimitArguments(pair.Value, depth + 1);
            return result;
        }
        if (value is JsonArray array)
        {
            var result = new JsonArray();
            foreach (JsonNode? item in array) result.Add(LimitArguments(item, depth + 1));
            return result;
        }
        string text = NodeString(value);
        return JsonValue.Create(text.Length > 200 ? text.Substring(0, 200) + "..." : text);
    }

    private static string Deny(string reason) => new JsonObject
    {
        ["hookSpecificOutput"] = new JsonObject
        {
            ["hookEventName"] = "PreToolUse",
            ["permissionDecision"] = "deny",
            ["permissionDecisionReason"] = reason,
        }
    }.ToJsonString(JsonOptions);

    private static JsonNode? GetProperty(JsonObject? value, string name)
        => value?.FirstOrDefault(pair => pair.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;

    private static string NodeString(JsonNode? value)
    {
        if (value == null) return "";
        if (value is JsonValue scalar)
        {
            if (scalar.TryGetValue<bool>(out bool boolean)) return boolean ? "True" : "False";
            if (scalar.TryGetValue<string>(out string? text)) return text ?? "";
            if (scalar.TryGetValue<JsonElement>(out var element))
                return element.ValueKind switch
                {
                    JsonValueKind.String => element.GetString() ?? "",
                    JsonValueKind.True => "True",
                    JsonValueKind.False => "False",
                    JsonValueKind.Null => "",
                    _ => element.ToString(),
                };
        }
        return value.ToJsonString();
    }

    private static string? GetEnvironment(IReadOnlyDictionary<string, string?> environment, string name)
        => environment.FirstOrDefault(pair => pair.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;

    private static bool IsTruthy(string? value) => !string.IsNullOrEmpty(value);
    private static bool IsMatch(string? value, string pattern)
        => Regex.IsMatch(value ?? "", pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private sealed record ToolEntry(string Operation, string Category, HashSet<string> Parameters);
}
