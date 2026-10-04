using ModelContextProtocol.Server;
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
                var preflight = Invoke("PreflightToolCall", name, "{\"probe\":true,\"PROBE\":false}");
                check(preflight.ToJsonString().IndexOf("duplicate", StringComparison.OrdinalIgnoreCase) >= 0,
                    name + " preflight resolves the migrated tool before argument rejection");
            }
        }

        JsonObject Invoke(string name, params object[] args)
        {
            var method = facade.GetMethod(name, all, null, args.Select(arg => arg.GetType()).ToArray(), null)!;
            var response = method.Invoke(null, args)!;
            return JsonNode.Parse(JsonSerializer.Serialize(response, response.GetType()))!.AsObject();
        }
        var state = Invoke("CallTool", "GetState", "{}");
        check(state["Meta"]!["bridgeSuccess"]!.GetValue<bool>() && state["Message"]!.GetValue<string>().Contains("TIA-Portal MCP server state retrieved"),
            "CallTool invokes the migrated GetState");
        var scaffold = Invoke("CallTool", "ScaffoldProject", "{\"spec\":\"{\\\"projectName\\\":\\\"Offline\\\",\\\"directoryPath\\\":\\\"C:/domain-offline\\\"}\",\"dryRun\":true}");
        check(scaffold["Meta"]!["bridgeSuccess"]!.GetValue<bool>() && scaffold["Message"]!.GetValue<string>().Contains("0 ok, 0 failed"),
            "CallTool invokes the migrated ScaffoldProject preview without connecting");
        var read = Invoke("ReadToolBatch", "[{\"name\":\"GetState\",\"arguments\":{}},{\"name\":\"ReadPortalInfo\",\"arguments\":{\"includeProcesses\":false}}]", "");
        check(read["Meta"]!["results"]!.AsArray().Count == 2 && read["Meta"]!["success"]!.GetValue<bool>(),
            "ReadToolBatch resolves both migrated session readers offline");
        var plan = (JsonArray)facade.GetMethod("ValidateBatch", all)!.Invoke(null, new object[] {
            "[{\"name\":\"ShowObjectInEditor\",\"arguments\":{\"dryRun\":false}}]", true })!;
        check(plan[0]!["arguments"]!["dryRun"]!.GetValue<bool>(), "Write batch validation resolves ShowObjectInEditor and forces preview");
        var apply = Invoke("ApplyToolBatch", "offline-invalid-token");
        check(apply["Meta"]!["success"]!.GetValue<bool>() == false && apply["Meta"]!["mayHaveModifiedProject"] == null,
            "ApplyToolBatch rejects an invalid token before any project write");
    }
}
