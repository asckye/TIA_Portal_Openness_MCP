using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using TiaMcp.BuildCommon;

namespace TiaMcp.DiagnosticClients;

public sealed class Campaign
{
    private IMcpClient? client;
    private readonly Func<IMcpClient> connect;
    private HashSet<string> lite = new(StringComparer.Ordinal);
    public bool Connected => client is not null;
    public Campaign(Func<IMcpClient> connect) => this.connect = connect;
    public Campaign(IMcpClient client, IEnumerable<string> lite) { this.client = client; this.lite = lite.ToHashSet(StringComparer.Ordinal); connect = () => client; }
    public JsonObject CallRaw(string name, JsonObject args)
    {
        if (client is null)
        {
            client = connect(); McpSession.Initialize(client, "Probe-McpServer");
            lite = McpSession.Tools(client).Select(t => (string)t!["name"]!).ToHashSet(StringComparer.Ordinal);
        }
        var request = lite.Contains(name) ? new JsonObject { ["name"] = name, ["arguments"] = args.DeepClone() } :
            new JsonObject { ["name"] = "CallTool", ["arguments"] = new JsonObject { ["name"] = name, ["arguments"] = args.DeepClone() } };
        return McpResults.Envelope(client.Rpc("tools/call", request, timeoutSeconds: 900));
    }
    public static JsonObject ResultFields(JsonObject value)
    {
        var data = value["data"] as JsonObject ?? new JsonObject();
        var fields = data["evidence"]?.DeepClone() as JsonObject ?? new JsonObject();
        foreach (var source in new[] { data, value["meta"]!.AsObject() }) foreach (var field in source) fields[field.Key] = field.Value?.DeepClone();
        if (data["export"] is JsonObject export) fields["exportId"] = export["id"]?.DeepClone();
        return fields;
    }
    public JsonObject Alive()
    {
        var value = CallRaw("GetSessionState", new JsonObject());
        if (value["data"] is JsonObject data && data["evidence"]?["hmiReadHealth"] is JsonObject health && health["portalProcess"] is JsonObject process)
            return new JsonObject { ["pid"] = process["boundProcessId"]?.DeepClone(), ["alive"] = process["processAlive"]?.DeepClone(), ["blocked"] = health["snapshotReadsBlocked"]?.DeepClone(), ["connected"] = data["isConnected"]?.DeepClone(), ["project"] = data["project"]?.DeepClone() };
        return new JsonObject { ["raw"] = Trim(PythonJson.Dumps(value, ensureAscii: false), 200) };
    }
    public static (bool Ok, string Text) Classify(JsonObject value)
    {
        if (value.ContainsKey("rpcError")) return (false, "rpc:" + Trim(value["rpcError"]?.ToString() ?? "", 300));
        value = McpResults.Envelope(value);
        return ((bool)value["ok"]!, value["error"] is JsonObject error ? error["code"] + ": " + error["message"] : (string?)value["data"]?["summary"] ?? "");
    }
    public static string Verdict(JsonObject value, string expect)
    {
        value = McpResults.Envelope(value);
        if (McpSession.ResetRequired(value)) return "UNCONFIRMED";
        return ((bool)value["ok"]! && expect is "ok" or "any") || (!(bool)value["ok"]! && expect is "error" or "any") ? "PASS" : "FAIL";
    }
    public static string Trim(string text, int length) => text.Length <= length ? text : text[..length];
    public static string Assemble(string exportId, Func<string, JsonObject, JsonObject> call)
    {
        var parts = new StringBuilder(); var offset = 0;
        while (true)
        {
            var value = McpResults.Successful(call("GetExportContent", new JsonObject { ["exportId"] = exportId, ["offset"] = offset, ["length"] = 20000 }));
            parts.Append(value["data"]!["text"]!.GetValue<string>());
            var paging = value["meta"]!["paging"]!.AsObject();
            if (!paging.ContainsKey("nextOffset")) throw new ArgumentException("Missing export paging offset");
            if (paging["nextOffset"] is null) return parts.ToString();
            var following = paging["nextOffset"]!.GetValue<int>();
            if (following <= offset) throw new ArgumentException("Export paging did not advance");
            offset = following;
        }
    }
    public static string ExportRoot(JsonObject config, string run)
    {
        var bundle = (string?)config["bundleRoot"] ?? Environment.GetEnvironmentVariable("TIA_MCP_BUNDLE_ROOT") ?? throw new ArgumentException("Campaign requires bundleRoot or TIA_MCP_BUNDLE_ROOT for exports");
        if (run.Length == 0 || run.Contains("..", StringComparison.Ordinal) || run.IndexOfAny(['/', '\\', ':']) >= 0) throw new ArgumentException("Run must be one directory name");
        var expected = bundle.Replace('\\', '/').TrimEnd('/') + "/exports/" + run;
        var configured = (string?)config["exportRoot"];
        if (configured is not null && configured.Replace('\\', '/').TrimEnd('/') != expected.Replace('\\', '/')) throw new ArgumentException("exportRoot must be <bundle-root>/exports/<run>");
        return expected;
    }
    public static JsonNode? Expand(JsonNode? node, string exportRoot, string lastExport)
    {
        if (node is JsonObject obj) return new JsonObject(obj.Select(p => new KeyValuePair<string, JsonNode?>(p.Key, Expand(p.Value, exportRoot, lastExport))));
        if (node is JsonArray array) return new JsonArray(array.Select(n => Expand(n, exportRoot, lastExport)).ToArray());
        if (node is JsonValue scalar && scalar.TryGetValue<string>(out var text))
        {
            // Plans are frozen data. Root substitution also covers paths inside JSON/XML strings.
            text = text == "LAST" ? lastExport : text.Replace("${EXPORT_ROOT}", exportRoot, StringComparison.Ordinal);
            if (text.Contains("C:\\Users\\SIEMENS\\Desktop", StringComparison.OrdinalIgnoreCase) || text.Contains("C:\\\\Users\\\\SIEMENS\\\\Desktop", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Campaign desktop paths are retired; use ${EXPORT_ROOT}");
            return JsonValue.Create(text);
        }
        return node?.DeepClone();
    }
    public void Run(JsonArray plan, int start, string exportRoot, TextWriter ledger, TextWriter output, Func<string, string>? redact = null)
    {
        redact ??= s => s; var last = "";
        for (var i = start; i < plan.Count; i++)
        {
            var step = plan[i]!.AsObject(); var tool = (string)step["tool"]!;
            var arguments = Expand(step["args"] ?? new JsonObject(), exportRoot, last)!.AsObject();
            var expect = (string?)step["expect"] ?? "ok";
            var timer = System.Diagnostics.Stopwatch.StartNew(); var value = CallRaw(tool, arguments); timer.Stop();
            var (ok, text) = Classify(value); var fields = ResultFields(value);
            if (fields["exportId"] is JsonValue id && id.TryGetValue<string>(out var export)) last = export;
            var state = Alive(); var verdict = Verdict(value, expect); var keys = new JsonObject();
            foreach (var key in step["keys"]?.AsArray() ?? []) if (fields.ContainsKey((string)key!)) keys[(string)key!] = fields[(string)key!]?.DeepClone();
            var rec = new JsonObject { ["i"] = i, ["tool"] = tool, ["args"] = arguments, ["expect"] = expect, ["ok"] = ok, ["verdict"] = verdict, ["text"] = Trim(text, 600), ["secs"] = Math.Round(timer.Elapsed.TotalSeconds, 1), ["note"] = step["note"]?.DeepClone() ?? JsonValue.Create(""), ["contract"] = "v4", ["outcome"] = value["meta"]?["outcome"]?.DeepClone(), ["execution"] = value["meta"]?["execution"]?.DeepClone(), ["keys"] = keys, ["tia"] = state };
            ledger.WriteLine(redact(PythonJson.Dumps(rec, ensureAscii: false))); ledger.Flush();
            output.WriteLine(redact(string.Format(CultureInfo.InvariantCulture, "{0,3} {1,-4} {2,-36} {3,5:F1}s {4}", i, verdict, tool, timer.Elapsed.TotalSeconds, Trim(text, 150).Replace('\n', ' '))));
            foreach (var field in keys) output.WriteLine(redact("         " + field.Key + " = " + Trim(PythonJson.Dumps(field.Value, ensureAscii: false), 500)));
            if (McpSession.ResetRequired(value)) { output.WriteLine("!!! Unconfirmed outcome; stop without retry or cleanup"); break; }
            if ((bool?)state["alive"] == false || (bool?)state["connected"] == false) { output.WriteLine("!!! TIA / binding lost after step " + i + " " + redact(PythonJson.Dumps(state, ensureAscii: false))); break; }
        }
    }
}
