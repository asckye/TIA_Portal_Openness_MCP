using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using TiaMcp.BuildCommon;

namespace TiaMcp.ReleaseTool;

internal static class ScriptToolCalls
{
    internal static readonly string[] DataAllowlist = ["scripts/diagnostics/campaign/historical_tool_names.json", "scripts/checks/write-guard-operations-v3.3.0.json"];
    private static readonly string[] Helpers = ["call", "call_raw", "call_guide_tool", "S", "Call", "invoke_tool", "CallTool", "InvokeTool", "CallRaw", "CallGuideTool", "InvokeToolAsync", "CallToolAsync"];
    private static readonly string[] Protocol = ["initialize", "notifications/initialized", "tools/list", "tools/call", "ping", "resources/list", "resources/read", "prompts/list", "prompts/get"];
    internal static HashSet<string> Registrations(string root)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in SourceCheck.Files(Path.Combine(root, "src/Engine")).Where(p => p.EndsWith(".cs", StringComparison.Ordinal)))
            foreach (Match match in Regex.Matches(Repository.ReadSource(path), "McpServerTool\\(Name\\s*=\\s*\"([^\"]+)\"")) names.Add(match.Groups[1].Value);
        var catalog = XDocument.Load(Path.Combine(root, "src/Logic/ModelContextProtocol/ToolProfiles.resx")).Descendants("data").Single(n => n.Attribute("name")?.Value == "Catalog").Element("value")!.Value;
        foreach (var release in JsonNode.Parse(catalog)!["releases"]!.AsObject()) foreach (var row in release.Value!.AsArray()) names.Add(row!["name"]!.GetValue<string>());
        return names;
    }
    internal static List<(int Line, string Name)> Calls(string path, string source)
    {
        var calls = new List<(int, string)>();
        if (path.EndsWith(".json", StringComparison.Ordinal))
        {
            void Walk(JsonNode? node)
            {
                if (node is JsonObject obj)
                {
                    foreach (var (key, args) in new[] { ("tool", "args"), ("name", "arguments") })
                        if (obj.ContainsKey(args) && obj[key] is JsonValue v && v.TryGetValue<string>(out var name)) calls.Add((1, name));
                    foreach (var field in obj) Walk(field.Value);
                }
                else if (node is JsonArray array) foreach (var item in array) Walk(item);
            }
            Walk(JsonNode.Parse(source));
            return calls;
        }
        if (!path.EndsWith(".cs", StringComparison.Ordinal)) return calls;
        void Visit(IReadOnlyList<Token> tokens)
        {
            var pairs = MatchingPairs.Find(tokens);
            bool NativeCallback(int index)
            {
                var end = pairs[index + 1];
                var comma = McpText.ExpressionEnd(tokens, pairs, index + 2);
                if (comma + 1 >= end || tokens[comma].Value != ",") return false;
                var opening = comma + 1;
                if (tokens[opening].Value == "(") return pairs.TryGetValue(opening, out var closing) && closing + 1 < end && tokens[closing + 1].Value == "=>";
                return opening + 1 < end && tokens[opening].Kind == "identifier" && tokens[opening + 1].Value == "=>";
            }
            for (var i = 0; i < tokens.Count; i++)
            {
                var token = tokens[i];
                if (token.Expressions.Count > 0) Visit(token.Expressions);
                if (i + 2 < tokens.Count && token.Kind == "identifier" && Helpers.Contains(token.Value) && tokens[i + 1].Value == "(" && tokens[i + 2].Kind == "literal")
                {
                    var name = McpText.LiteralParts(tokens[i + 2]).Text;
                    var payload = false;
                    try { JsonNode.Parse(name); payload = true; } catch (System.Text.Json.JsonException) { }
                    if (name.Length > 0 && !name.Contains("::", StringComparison.Ordinal) && !Protocol.Contains(name) && !payload && !(token.Value == "Call" && name.Contains('.') && NativeCallback(i))) calls.Add((SourceCheck.Line(source, token.OffsetStart), name));
                }
                if (token.Value != "{" || !pairs.TryGetValue(i, out var end)) continue;
                var fields = new Dictionary<string, (Token Value, int Index)>();
                for (var j = i + 1; j < end; j++)
                {
                    var key = tokens[j].Kind == "literal" ? McpText.LiteralParts(tokens[j]).Text : tokens[j].Value;
                    if (tokens[j].Value == "[" && j + 4 < end && tokens[j + 1].Kind == "literal" && tokens[j + 2].Value == "]" && tokens[j + 3].Value == "=") fields[McpText.LiteralParts(tokens[j + 1]).Text] = (tokens[j + 4], j);
                    else if (j + 2 < end && tokens[j + 1].Value is "=" or ":") fields[key] = (tokens[j + 2], j);
                    if (pairs.TryGetValue(j, out var closing)) j = closing;
                }
                foreach (var (key, args) in new[] { ("tool", "args"), ("name", "arguments") })
                    if (fields.ContainsKey(args) && fields.TryGetValue(key, out var field) && field.Value.Kind == "literal") calls.Add((SourceCheck.Line(source, field.Value.OffsetStart), McpText.LiteralParts(field.Value).Text));
            }
        }
        Visit(new CSharpLexer(source).Scan().Tokens);
        return calls;
    }
    internal static List<(int Line, string Name)> Check(string path, string source, ISet<string> registered) => DataAllowlist.Contains(path) ? [] : Calls(path, source).Where(c => !registered.Contains(c.Name)).Distinct().ToList();
    internal static int Run(string root)
    {
        var names = Registrations(root);
        var errors = new List<string>(); var count = 0;
        foreach (var path in SourceCheck.Files(Path.Combine(root, "scripts")).Where(p => Path.GetExtension(p) is ".cs" or ".json").Order(StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(root, path).Replace('\\', '/'); count++;
            errors.AddRange(Check(relative, Repository.ReadSource(path), names).Select(c => $"{relative}:{c.Line}: unregistered tool {c.Name}"));
        }
        return SourceCheck.Report("Script tool calls", errors, $"{count} files, {names.Count} registered names.");
    }
}
