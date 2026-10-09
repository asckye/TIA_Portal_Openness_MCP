using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Remoting.Messaging;
using System.Runtime.Remoting.Proxies;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

internal static class CompileCandidateChecks
{
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    private sealed class RequestServer : RealProxy
    {
        internal RequestServer() : base(typeof(IMcpServer)) { }
        public override IMessage Invoke(IMessage message) => new ReturnMessage(null, null, 0, null, (IMethodCallMessage)message);
    }

    internal static void Run(Assembly server, bool safe, string output, Action<bool, string> check)
    {
        var facade = server.GetType("TiaMcpServer.ModelContextProtocol.McpServer", true)!;
        var methods = (IReadOnlyDictionary<string, MethodInfo>)facade.GetMethod("AllToolMethods", All)!.Invoke(null, new object[] { true })!;
        var advertised = (IList<McpServerTool>)facade.GetMethod("GetAllTools", All)!.Invoke(null, null)!;
        var schemas = new JsonObject();
        CallToolResult Invoke(string name, JsonObject args)
        {
            var tool = advertised.Single(t => t.ProtocolTool.Name == name);
            return tool.InvokeAsync(new RequestContext<CallToolRequestParams>((IMcpServer)new RequestServer().GetTransparentProxy()) {
                Params = new CallToolRequestParams { Name = name, Arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(args.ToJsonString()) }
            }).AsTask().GetAwaiter().GetResult();
        }
        JsonObject Body(CallToolResult result)
        {
            var body = JsonNode.Parse(((TextContentBlock)result.Content.Single()).Text)!.AsObject();
            check(JsonNode.DeepEquals(body, result.StructuredContent) && result.IsError == !(bool)body["ok"]!, "Candidate MCP result views agree");
            return body;
        }
        foreach (string name in new[] { "CompilePlcSoftware", "CompilePlcDiagnostics", "CompileDevice", "CompileHmiDiagnostics" })
        {
            var method = methods[name];
            check((method.GetCustomAttributes().Any(a => a.GetType().Name == "BehaviorCandidateAttribute")) == safe,
                name + " selects exactly the expected method");
            var registered = advertised.Where(t => t.ProtocolTool.Name == name).ToArray();
            check(registered.Length == 1, name + " is advertised exactly once");
            var schema = registered[0].ProtocolTool.InputSchema;
            schemas[name] = JsonNode.Parse(schema.GetRawText());
            var properties = schema.GetProperty("properties");
            check(properties.TryGetProperty("mode", out _) == safe,
                name + " advertises only the selected schema");
            if (safe)
            {
                check(properties.GetProperty("mode").GetProperty("default").GetString() == "preview"
                    && !properties.GetProperty("confirm").GetProperty("default").GetBoolean(), name + " has exact preview/confirmation defaults");
                check(properties.GetProperty("offlinePolicy").GetProperty("default").GetString() == "require", name + " requires offline without auto-offline");
            }
            var usage = Body(Invoke("GetToolUsage", new JsonObject { ["toolName"] = name }));
            var sample = usage["data"]!["example"]!["request"]!["params"]!["arguments"]!.AsObject();
            check(facade.GetMethod("ValidateV4Arguments", All)!.Invoke(null,
                new object[] { method, JsonSerializer.SerializeToElement(sample), schema }) == null, name + " usage example satisfies the selected schema");
                var invalid = (JsonObject)sample.DeepClone(); invalid[safe ? "obsoleteCompileOption" : "offlinePolicy"] = "obsolete-schema";
            foreach (var call in new[] {
                (Name: name, Args: invalid),
                (Name: "CallTool", Args: new JsonObject { ["name"] = name, ["arguments"] = invalid.DeepClone() }),
                (Name: "PreviewToolBatch", Args: new JsonObject { ["operations"] = new JsonArray(new JsonObject { ["name"] = name, ["arguments"] = invalid.DeepClone() }), ["expectedProject"] = "Fixture" }) })
            {
                if (EngineSurface.IsHostTool(server, call.Name)) continue;
                var rejected = Body(Invoke(call.Name, call.Args));
                check((string?)rejected["error"]?["code"] == "INVALID_ARGUMENT" && (string?)rejected["meta"]?["execution"] == "not-started",
                    name + " rejects the other schema through " + call.Name);
                if (safe && call.Name == name) check((string?)rejected["meta"]?["behaviorPolicy"] == "safe-v4", name + " direct admission discloses selected policy");
            }
            if (safe)
            {
                foreach (var call in new[] {
                    (Name: name, Args: (JsonObject)sample.DeepClone()),
                    (Name: "CallTool", Args: new JsonObject { ["name"] = name, ["arguments"] = sample.DeepClone() }) })
                {
                    if (EngineSurface.IsHostTool(server, call.Name)) continue;
                    var preview = Body(Invoke(call.Name, call.Args));
                    check((string?)preview["error"]?["code"] == "PROJECT_NOT_BOUND" && (string?)preview["meta"]?["tool"] == name
                        && (string?)preview["meta"]?["behaviorPolicy"] == "safe-v4" && (string?)preview["meta"]?["execution"] == "not-started",
                        name + " valid preview reaches the candidate binding guard through " + call.Name);
                }
                var badIdentity = (JsonObject)sample.DeepClone(); badIdentity["mode"] = "invalid-mode";
                var unbound = Body(Invoke(name, badIdentity));
                check((string?)unbound["error"]?["code"] == "INVALID_ARGUMENT" && (string?)unbound["meta"]?["behaviorPolicy"] == "safe-v4",
                    name + " dispatch validates mode without a native call");
            }
        }
        if (output.Length > 0) File.WriteAllText(output, schemas.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), new System.Text.UTF8Encoding(false));
    }
}
