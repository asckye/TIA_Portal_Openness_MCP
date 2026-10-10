#!/usr/bin/env dotnet
// Direct MCP HTTP probe, bypassing system proxies; credentials are never printed.
// Usage: dotnet run scripts/diagnostics/Probe-McpServer.cs -- tools|call|bridge [Tool] [json|@file] [--url URL] [--token TOKEN]
#:property PublishAot=false
#:property NuGetAudit=false
#:project TiaMcp.DiagnosticClients/TiaMcp.DiagnosticClients.csproj

using System.Text;
using System.Text.Json.Nodes;
using TiaMcp.BuildCommon;
using TiaMcp.DiagnosticClients;

Console.OutputEncoding = new UTF8Encoding(false);
Connection? connection = null;
try
{
    var options = new Arguments(args);
    var url = options.Take("--url"); var token = options.Take("--token"); var timeout = int.Parse(options.Take("--timeout") ?? "600");
    if (options.Rest.Count == 0 || options.Flag("--help")) { Console.WriteLine("tools [filter ...] | call <Tool> [json|@file] | bridge <Tool> [json|@file]; --url/--token or TIA_MCP_URL/TIA_MCP_TOKEN or ~/.claude.json"); return 0; }
    var mode = options.Rest[0]; options.Rest.RemoveAt(0);
    if (mode is not ("tools" or "call" or "bridge")) throw new ArgumentException("Use tools, call or bridge");
    if (mode != "tools") Console.Error.WriteLine(McpSession.ApprovalNotice);
    connection = Connection.Load(url, token);
    using var client = new HttpProbe(connection, timeout);
    Console.WriteLine(connection.Redact("server: " + McpSession.Initialize(client, "Probe-McpServer")));
    if (mode == "tools")
    {
        var tools = McpSession.Tools(client); Console.WriteLine("tools: " + tools.Count);
        foreach (var tool in tools.OrderBy(t => (string)t!["name"]!, StringComparer.Ordinal))
            if (options.Rest.Count == 0 || options.Rest.Any(f => ((string)tool!["name"]!).Contains(f, StringComparison.OrdinalIgnoreCase))) Console.WriteLine(connection.Redact("  " + tool!["name"]));
        return 0;
    }
    if (options.Rest.Count == 0) throw new ArgumentException("tool name required");
    var name = options.Rest[0]; var arguments = Arguments.Load(options.Rest.ElementAtOrDefault(1));
    if (mode == "bridge") { arguments = new JsonObject { ["name"] = name, ["arguments"] = arguments }; name = "CallTool"; }
    var reply = client.Rpc("tools/call", new JsonObject { ["name"] = name, ["arguments"] = arguments })!;
    if (reply.AsObject().ContainsKey("error")) Console.WriteLine(connection.Redact("** rpc error: " + PythonJson.Dumps(reply["error"], ensureAscii: false)));
    else
    {
        var value = McpResults.Envelope(reply); Console.WriteLine(connection.Redact(PythonJson.Dumps(value, ensureAscii: false, indent: 2)));
        if (!(bool)value["ok"]!) Console.WriteLine(connection.Redact("** error=" + value["error"]!["code"] + " **"));
    }
    return 0;
}
catch (Exception ex) { Console.Error.WriteLine(connection?.Redact(ex.Message) ?? ex.Message); return 1; }
