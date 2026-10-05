using ModelContextProtocol.Server;
using ModelContextProtocol.Protocol;
using System;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

internal static class SessionToolChecks
{
    internal static void Run(Assembly server, Action<bool, string> check)
    {
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        var surface = EngineSurface.For(server);
        var contract = server.GetType("TiaMcpServer.Siemens.IEngineeringSession", true)!;
        var portal = server.GetType("TiaMcpServer.Siemens.Portal", true)!;
        var facade = server.GetType("TiaMcpServer.ModelContextProtocol.McpServer", true)!;
        var provider = (IServiceProvider)server.GetType("TiaMcpServer.EngineServices", true)!.GetProperty("Provider", all)!.GetValue(null)!;
        var session = provider.GetService(contract);
        var isControl = server.GetType("TiaMcpServer.Isolation.IsolatedWorkerHost", true)!.GetMethod("IsControl", all)!;
        foreach (var domain in new[] { (Name: "Session", Count: 9), (Name: "ProjectSession", Count: 11), (Name: "Diagnostics", Count: 4) })
        {
            var type = server.GetType("TiaMcpServer.ModelContextProtocol." + domain.Name + "Tools", true)!;
            var target = provider.GetService(type);
            check(type.IsSealed && !type.GetInterfaces().Any(item => item.Name == "IDisposable" || item.Name == "IAsyncDisposable"),
                domain.Name + " tools are sealed and non-disposable");
            check(target != null && ReferenceEquals(target, provider.GetService(type))
                && ReferenceEquals(type.GetField("_session", all)!.GetValue(target), session)
                && ReferenceEquals(session, provider.GetService(portal)), domain.Name + " tools share the kernel singleton");
            check(type.GetConstructors().Single().GetParameters().Select(p => p.ParameterType).SequenceEqual(
                    domain.Name == "ProjectSession" ? new[] { contract,
                        server.GetType("TiaMcpServer.ModelContextProtocol.SessionTools", true)!,
                        server.GetType("TiaMcpServer.ModelContextProtocol.PlcBuildTools", true)!,
                        server.GetType("TiaMcpServer.ModelContextProtocol.DevicesTools", true)!,
                        server.GetType("TiaMcpServer.ModelContextProtocol.DocumentsTools", true)! } : new[] { contract }),
                domain.Name + " tools declare their session and tool dependencies");
            var methods = type.GetMethods(all).Where(method => method.GetCustomAttribute<McpServerToolAttribute>() != null).ToArray();
            check(methods.Length == domain.Count, domain.Name + " tool count");
            foreach (var method in methods)
            {
                var name = method.GetCustomAttribute<McpServerToolAttribute>()!.Name!;
                check(!method.IsStatic && surface.Tool(name) == method && ReferenceEquals(target, surface.Target(method)),
                    name + " resolves to the instance tool");
                var forwarder = facade.GetMethod(method.Name, all, null, method.GetParameters().Select(p => p.ParameterType).ToArray(), null)!;
                check(forwarder == null && ReferenceEquals(surface.Target(method), provider.GetService(method.DeclaringType!)),
                    name + " resolves directly through EngineServices without a static compatibility forwarder");
                check(!(bool)isControl.Invoke(null, new object[] { name })!, name + " remains proxied to the isolated worker");
                var preflight = Invoke("PreviewToolCall", new JsonObject { ["name"] = name,
                    ["arguments"] = new JsonObject { ["probe"] = true } }.ToJsonString());
                check((string?)preflight["error"]?["code"] == "INVALID_ARGUMENT",
                    name + " preflight resolves the migrated tool before argument rejection");
            }
        }

        JsonObject Invoke(string name, string arguments)
        {
            var method = surface.Tool(name);
            var values = JsonNode.Parse(arguments)!.AsObject();
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var args = method.GetParameters().Select(parameter => values.TryGetPropertyValue(parameter.Name!, out var value)
                ? JsonSerializer.Deserialize(value!.ToJsonString(), parameter.ParameterType, options) : parameter.DefaultValue).ToArray();
            var response = (CallToolResult)surface.Invoke(method, args)!;
            return JsonNode.Parse(((TextContentBlock)response.Content.Single()).Text)!.AsObject();
        }
        var state = Invoke("CallTool", "{\"name\":\"GetState\",\"arguments\":{}}");
        check(state["meta"]!["success"]!.GetValue<bool>() && state["message"]!.GetValue<string>().Contains("TIA-Portal MCP server state retrieved"),
            "CallTool invokes the migrated GetState");
        var scaffold = Invoke("CallTool", "{\"name\":\"ScaffoldProject\",\"arguments\":{\"spec\":\"{\\\"projectName\\\":\\\"Offline\\\",\\\"directoryPath\\\":\\\"C:/domain-offline\\\"}\",\"dryRun\":true}}");
        check(scaffold["meta"]!["success"]!.GetValue<bool>() && scaffold["message"]!.GetValue<string>().Contains("0 ok, 0 failed"),
            "CallTool invokes the migrated ScaffoldProject preview without connecting");
        var read = Invoke("RunReadOnlyToolBatch", "{\"operations\":[{\"name\":\"GetState\",\"arguments\":{}},{\"name\":\"ReadPortalInfo\",\"arguments\":{\"includeProcesses\":false}}]}");
        check(read["data"]!["items"]!.AsArray().Count == 2 && read["ok"]!.GetValue<bool>(),
            "RunReadOnlyToolBatch resolves both migrated session readers offline");
        var validate = facade.GetMethod("ValidateBatch", all)!;
        var batchArguments = new object?[] { JsonSerializer.Deserialize(
            "[{\"name\":\"ShowObjectInEditor\",\"arguments\":{\"dryRun\":false}}]", validate.GetParameters()[0].ParameterType,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }), true, null };
        check(validate.Invoke(null, batchArguments) == null, "Write batch validation accepts the session preview tool");
        var plan = JsonNode.Parse(JsonSerializer.Serialize(batchArguments[2], batchArguments[2]!.GetType(),
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }))!.AsArray();
        check(plan[0]!["arguments"]!["dryRun"]!.GetValue<bool>(), "Write batch validation resolves ShowObjectInEditor and forces preview");
        var apply = Invoke("ApplyToolBatch", "{\"token\":\"offline-invalid-token\"}");
        check(!apply["ok"]!.GetValue<bool>() && (string?)apply["error"]?["code"] == "NOT_FOUND"
            && (string?)apply["meta"]?["execution"] == "not-started",
            "ApplyToolBatch rejects an invalid token before any project write");
    }
}
