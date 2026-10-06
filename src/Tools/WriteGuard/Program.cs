using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

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
        string output = GuardHost.ProcessInput(raw, environment, AppContext.BaseDirectory, Console.Error);
        if (output.Length > 0) Console.Out.Write(output);
        return 0;
    }
}

public static class GuardHost
{
    private const string UnknownMessage = "TIA write guard: '{0}' is unknown; the tool list is out of date. Regenerate manifest/tools-list.json from the current engines (or set TIA_MCP_WRITE_GUARD=0 to disable the guard).";
    private static readonly HashSet<string> SafeOperations = new(StringComparer.OrdinalIgnoreCase) { "READ", "SESSION", "OFFLINE" };
    private static readonly HashSet<string> KnownOperations = new(StringComparer.OrdinalIgnoreCase)
        { "READ", "SESSION", "OFFLINE", "WRITE", "FILE", "EXECUTE", "ONLINE", "ONLINE-WRITE" };
    private static readonly HashSet<string> SecretNames = new(StringComparer.OrdinalIgnoreCase)
        { "password", "secret", "token", "credential" };
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    public static string ProcessInput(string raw, IReadOnlyDictionary<string, string?> environment, string baseDirectory, TextWriter? error = null)
    {
        if (IsMatch(GetEnvironment(environment, "TIA_MCP_WRITE_GUARD"), "^(0|off|false)$")) return "";
        if (string.IsNullOrWhiteSpace(raw)) return "";

        string name = "";
        JsonNode? payload = null;
        try
        {
            payload = JsonNode.Parse(raw);
            if (payload is not JsonObject payloadObject) return "";
            string toolName = NodeString(GetProperty(payloadObject, "tool_name"));
            Match hookName = Regex.Match(toolName, "^mcp__tia-portal__(.+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (!hookName.Success) return "";
            name = hookName.Groups[1].Value;

            string root = GetEnvironment(environment, "CLAUDE_PLUGIN_ROOT") ?? "";
            if (string.IsNullOrWhiteSpace(root)) root = Path.GetFullPath(Path.Combine(baseDirectory, "..", ".."));
            var entries = ReadEntries(Path.Combine(root, "manifest", "tools-list.json"));
            var deny = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "ONLINE-WRITE" };
            foreach (string extra in (GetEnvironment(environment, "TIA_MCP_GUARD_DENY_OPERATIONS") ?? "").Split(','))
                if (!string.IsNullOrWhiteSpace(extra)) deny.Add(extra.Trim());
            bool allowed = IsMatch(GetEnvironment(environment, "TIA_MCP_ALLOW_ONLINE_WRITE"), "^(1|true|yes)$");
            JsonNode? input = GetProperty(payloadObject, "tool_input");
            var reasons = Inspect(name, input, false, false, 0, entries, deny, allowed, payloadObject,
                GetEnvironment(environment, "TIA_MCP_AUDIT_LOG"), GetEnvironment(environment, "LOCALAPPDATA"));
            return reasons.Count == 0 ? "" : Deny(string.Join(" ", reasons));
        }
        catch (Exception ex)
        {
            if (IsTruthy(GetEnvironment(environment, "TIA_MCP_GUARD_DEBUG")))
                error?.WriteLine("tia-write-guard: " + ex.Message);
            if (name.Length == 0) return "";
            Audit(name, GetProperty(payload as JsonObject, "tool_input"), "UNKNOWN", "", false,
                payload as JsonObject, GetEnvironment(environment, "TIA_MCP_AUDIT_LOG"), GetEnvironment(environment, "LOCALAPPDATA"));
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
        IReadOnlyDictionary<string, ToolEntry> entries, HashSet<string> deny, bool allowed, JsonObject payload,
        string? auditPath, string? localAppData)
    {
        if (!entries.TryGetValue(target, out var entry) || !KnownOperations.Contains(entry.Operation))
        {
            Audit(target, arguments, "UNKNOWN", "", preview, payload, auditPath, localAppData);
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
        Audit(target, argumentObject, entry.Operation, entry.Category, isPreview, payload, auditPath, localAppData);

        var reasons = new List<string>();
        if (target.Equals("CallTool", StringComparison.OrdinalIgnoreCase) || target.Equals("PreviewToolCall", StringComparison.OrdinalIgnoreCase))
        {
            string child = NodeString(GetProperty(argumentObject, "name"));
            reasons.AddRange(Inspect(child, GetProperty(argumentObject, "arguments"),
                preview || target.Equals("PreviewToolCall", StringComparison.OrdinalIgnoreCase), apply, depth + 1,
                entries, deny, allowed, payload, auditPath, localAppData));
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
                    entries, deny, allowed, payload, auditPath, localAppData));
            }
            if (target.Equals("ApplyToolBatch", StringComparison.OrdinalIgnoreCase) && !preview && !allowed)
                reasons.Add("TIA write guard: 'ApplyToolBatch' has an opaque stored plan; its targets cannot be classified. Set TIA_MCP_ALLOW_ONLINE_WRITE=1 to execute the batch.");
        }
        if (deny.Contains(entry.Operation) && !isPreview && !allowed)
            reasons.Add($"TIA write guard: '{target}' is a {entry.Operation} operation on a real controller/runtime/simulation. " +
                (hasDryRun ? "Run it with dryRun=true first; to execute, " : "To execute, ") +
                "set TIA_MCP_ALLOW_ONLINE_WRITE=1 in the environment of this Claude Code session (or TIA_MCP_WRITE_GUARD=0 to disable the guard). Every write is recorded in the audit log.");
        return reasons;
    }

    private static IEnumerable<JsonNode?> Enumerate(JsonNode? value)
    {
        if (value is JsonArray array) return array;
        return value == null ? Array.Empty<JsonNode?>() : new JsonNode?[] { value };
    }

    private static void Audit(string target, JsonNode? arguments, string operation, string category, bool preview,
        JsonObject? payload, string? auditPath, string? localAppData)
    {
        if (SafeOperations.Contains(operation)) return;
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
                ["tool"] = target,
                ["category"] = category,
                ["operation"] = operation,
                ["preview"] = preview,
                ["arguments"] = Redact(arguments, 0),
            };
            File.AppendAllText(path, line.ToJsonString(JsonOptions) + Environment.NewLine, new UTF8Encoding(false));
        }
        catch /* swallow(fail-open-guard): audit failures must not bypass the deny policy */ { }
    }

    private static JsonNode? Redact(JsonNode? value, int depth)
    {
        if (value == null) return null;
        if (depth >= 20) return JsonValue.Create("<truncated>");
        if (value is JsonObject obj)
        {
            var result = new JsonObject();
            foreach (var pair in obj)
                result[pair.Key] = Regex.IsMatch(pair.Key, "password|secret|token|credential", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
                    ? JsonValue.Create("<redacted>") : Redact(pair.Value, depth + 1);
            return result;
        }
        if (value is JsonArray array)
        {
            var result = new JsonArray();
            foreach (JsonNode? item in array) result.Add(Redact(item, depth + 1));
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
