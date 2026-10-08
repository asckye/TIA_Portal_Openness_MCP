using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.Remoting.Messaging;
using System.Runtime.Remoting.Proxies;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

internal static class BehaviorCapabilityChecks
{
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    private sealed class RequestServer : RealProxy
    {
        internal RequestServer() : base(typeof(IMcpServer)) { }
        public override IMessage Invoke(IMessage message) => new ReturnMessage(null, null, 0, null, (IMethodCallMessage)message);
    }
    internal static void Run(Assembly server, Action<bool, string> check)
    {
        var facade = server.GetType("TiaMcpServer.ModelContextProtocol.McpServer", true)!;
        var logic = Program.FindServerType(server, "TiaMcp.Logic.V4.BehaviorCapabilities");
        string release = (string)facade.GetProperty("ReleaseKey", All)!.GetValue(null)!;
        var table = (JsonArray)logic.GetMethod("Table")!.Invoke(null, new object[] { server, release })!;
        var logicTable = (JsonArray)logic.GetMethod("Table")!.Invoke(null, new object[] { logic.Assembly, release })!;
        check(table.Count == 8 && JsonNode.DeepEquals(table, logicTable), "Host and Logic behavior tables agree");
        var methods = (IReadOnlyDictionary<string, MethodInfo>)facade.GetMethod("AllToolMethods", All)!.Invoke(null, new object[] { true })!;
        var advertised = (IList<McpServerTool>)facade.GetMethod("GetAllTools", All)!.Invoke(null, null)!;
        JsonObject Invoke(string name, JsonObject args, CancellationToken cancellation = default)
        {
            var selected = advertised.Single(t => t.ProtocolTool.Name == name);
            // Exercise the existing queue boundary explicitly; offline CallTool
            // itself has no serialization gate and its SDK propagates cancellation.
            if (cancellation.IsCancellationRequested)
                selected = (McpServerTool)Activator.CreateInstance(server.GetType("TiaMcpServer.ModelContextProtocol.SerializedCallTool", true)!,
                    All, null, new object[] { selected }, null)!;
            var result = selected.InvokeAsync(
                new RequestContext<CallToolRequestParams>((IMcpServer)new RequestServer().GetTransparentProxy()) {
                    Params = new CallToolRequestParams { Name = name, Arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(args.ToJsonString()) }
                }, cancellation).AsTask().GetAwaiter().GetResult();
            var body = JsonNode.Parse(((TextContentBlock)result.Content.Single()).Text)!.AsObject();
            check(JsonNode.DeepEquals(body, result.StructuredContent), name + " content views agree");
            return body;
        }
        var index = Invoke("GetToolUsage", new JsonObject());
        check(JsonNode.DeepEquals(table, index["data"]!["behaviorCapabilities"]), "Usage index reports actual selected behavior table");
        foreach (var row in table)
        {
            check((string?)row!["state"] == "current" && (string?)row["l5"] == "NOT RUN", "Released family stays current: " + (string)row["family"]!);
            foreach (string name in row["entries"]!.AsArray().Select(e => (string)e!))
            {
                check(!methods[name].GetCustomAttributes().Any(a => a.GetType().Name == "BehaviorCandidateAttribute"), "Default build selects current method: " + name);
                var tool = advertised.Single(t => t.ProtocolTool.Name == name).ProtocolTool;
                check(tool.Description!.Contains("behaviorPolicy=current") && tool.Description.Contains("V4 native acceptance is pending"), "Description disclosure: " + name);
                var invalid = new JsonObject { ["Sentinel"] = 1, ["sentinel"] = 2 };
                var calls = new[] { Invoke(name, invalid), Invoke("CallTool", new JsonObject { ["name"] = name, ["arguments"] = invalid.DeepClone() }),
                    Invoke("RunReadOnlyToolBatch", new JsonObject { ["operations"] = new JsonArray(new JsonObject { ["name"] = name, ["arguments"] = invalid.DeepClone() }) }),
                    Invoke("CallTool", new JsonObject { ["name"] = name, ["arguments"] = invalid.DeepClone() }, new CancellationToken(true)) };
                foreach (var body in calls)
                    check((string?)body["meta"]!["behaviorPolicy"] == "current" && body["meta"]!["warnings"]!.AsArray().Any(w => (string?)w!["code"] == "UNVERIFIED_BEHAVIOR")
                        && (string?)body["meta"]!["execution"] == "not-started", "D1 admission disclosure: " + name + "; " + body["meta"]!.ToJsonString());
            }
        }
    }
}
