using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using TiaMcp.BuildCommon;

namespace TiaMcp.DiagnosticClients;

public static class WrongPathSweep
{
    private static readonly string[] Prefixes = ["Get", "List", "Describe", "Find", "Probe", "Validate", "Check", "Read", "Search", "Analyze", "Preflight", "Inspect", "Diagnose"];
    private static readonly Regex Deny = new("Set|Write|Delete|Create|Add|Plug|Import|Export|Compile|Download|Upload|Save|Clear|Sync|ConnectPortal|DisconnectPortal|Open|Close|Scaffold|Build|Generate|Ensure|Attach|Detach|Go(Online|Offline)|Start|Stop|Apply|Repair|Fix|Rename|Move|Copy|Reset|Run", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly string[] PathArguments = ["softwarePath", "plcSoftwarePath", "deviceItemPath", "devicePath", "blockPath", "objectPath"];
    private static readonly JsonObject Bogus = new() { ["softwarePath"] = "NoSuchPlc_zzz", ["plcSoftwarePath"] = "NoSuchPlc_zzz", ["deviceItemPath"] = "NoSuchStation_zzz/NoSuchItem_zzz", ["devicePath"] = new JsonArray("NoSuchStation_zzz"), ["blockPath"] = "NoSuchBlock_zzz", ["blockName"] = "NoSuchBlock_zzz", ["objectPath"] = "NoSuchBlock_zzz", ["tagTableName"] = "NoSuchTable_zzz", ["objectKind"] = "Block", ["maxDepth"] = 3, ["changedOnly"] = true };
    public static bool Eligible(string name, JsonArray required) => Prefixes.Any(p => name.StartsWith(p, StringComparison.Ordinal)) && !Deny.IsMatch(name) && required.Any(r => PathArguments.Contains((string)r!));
    public static int Run(IMcpClient client, string project, TextWriter output)
    {
        var tools = McpSession.Tools(client);
        McpResults.Successful(McpSession.Call("ConnectPortal", client, new JsonObject()));
        McpResults.Successful(McpSession.Call("OpenProject", client, new JsonObject { ["path"] = project }, 900));
        McpResults.Successful(McpSession.Call("ListDevices", client, new JsonObject()));
        var suspects = new List<(string Name, JsonObject Args, string Body)>(); var honest = new List<string>(); var skipped = new List<(string Name, string[] Missing)>(); var reset = false;
        foreach (var tool in tools)
        {
            var name = (string)tool!["name"]!; var schema = tool["inputSchema"] as JsonObject ?? new JsonObject(); var required = schema["required"] as JsonArray ?? [];
            if (!Eligible(name, required)) continue;
            var arguments = new JsonObject(); var missing = new List<string>();
            foreach (var argument in required)
            {
                var key = (string)argument!;
                if (!Bogus.ContainsKey(key)) { missing.Add(key); continue; }
                var value = Bogus[key]!.DeepClone();
                if ((string?)schema["properties"]?[key]?["type"] == "array" && value is JsonValue) value = new JsonArray(value);
                arguments[key] = value;
            }
            if (missing.Count > 0) { skipped.Add((name, missing.ToArray())); continue; }
            JsonObject result;
            try { result = McpSession.Call(name, client, arguments); }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.Text.Json.JsonException)
            { suspects.Add((name, arguments, "Protocol/timeout: " + ex.Message)); reset = true; break; }
            if (McpSession.ResetRequired(result)) { suspects.Add((name, arguments, PythonJson.Dumps(result, ensureAscii: false))); reset = true; break; }
            if (!(bool)result["ok"]! && (string?)result["error"]?["code"] == "INVALID_ARGUMENT") skipped.Add((name, ["V4 argument admission; wrong-path behavior not exercised"]));
            else if (!(bool)result["ok"]!) honest.Add(name);
            else suspects.Add((name, arguments, PythonJson.Dumps(result, ensureAscii: false)));
        }
        if (!reset) McpResults.Successful(McpSession.Call("CloseProject", client, new JsonObject()));
        output.WriteLine($"Wrong-path refusals {honest.Count} | suspects {suspects.Count} | not covered {skipped.Count}");
        foreach (var row in suspects) output.WriteLine(row.Name + " " + PythonJson.Dumps(row.Args, ensureAscii: false) + " " + Campaign.Trim(row.Body, 300));
        foreach (var row in skipped) output.WriteLine("NOT COVERED " + row.Name + " " + string.Join(", ", row.Missing));
        return suspects.Count > 0 ? 1 : 0;
    }
}
