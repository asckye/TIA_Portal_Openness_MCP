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
        foreach (var domain in new[] { (Name: "Session", Count: 9), (Name: "ProjectSession", Count: 8), (Name: "Diagnostics", Count: 1) })
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
            var portedType = domain.Name is "Session" or "ProjectSession"
                ? server.GetType("TiaMcpServer.ModelContextProtocol.ProjectLifecycle" + (domain.Name == "Session" ? "Session" : "Project") + "Tools", true) : null;
            var types = portedType == null ? new[] { type } : new[] { type, portedType };
            var methods = types.SelectMany(item => item.GetMethods(all)).Where(method => method.GetCustomAttribute<McpServerToolAttribute>() != null).ToArray();
            check(methods.Length == domain.Count, domain.Name + " tool count");
            foreach (var method in methods)
            {
                var methodTarget = provider.GetService(method.DeclaringType!);
                bool ported = method.DeclaringType == portedType;
                var name = method.GetCustomAttribute<McpServerToolAttribute>()!.Name!;
                check(!method.IsStatic && surface.Tool(name) == method && ReferenceEquals(methodTarget, surface.Target(method)),
                    name + " resolves to the instance tool");
                if (ported)
                {
                    var service = method.DeclaringType!.GetField("_service", all)!.GetValue(methodTarget)!;
                    check(method.DeclaringType.IsSealed && !typeof(IDisposable).IsAssignableFrom(method.DeclaringType)
                        && ReferenceEquals(methodTarget, provider.GetService(method.DeclaringType))
                        && service.GetType().GetField("call", all)!.GetValue(service) is Delegate
                        && !service.GetType().GetFields(all).Any(field => field.FieldType == contract),
                        name + " shares the typed lifecycle worker service singleton");
                }
                var forwarder = facade.GetMethod(method.Name, all, null, method.GetParameters().Select(p => p.ParameterType).ToArray(), null)!;
                check(forwarder == null && ReferenceEquals(surface.Target(method), provider.GetService(method.DeclaringType!)),
                    name + " resolves directly through EngineServices without a static compatibility forwarder");
                check(((System.Collections.Generic.IReadOnlyDictionary<string, string>)Program.FindServerType(server, "TiaMcpServer.ModelContextProtocol.ToolExecution").GetField("Table", all)!.GetValue(null)!)[name] == (ported ? "foundation" : "worker"), name + " retains its execution owner");

            }
        }

        JsonObject Invoke(string name, string arguments)
        {
            var method = name == "CallTool" ? surface.ToolMethod("DispatchNestedTool", all) : surface.Tool(name);
            var values = JsonNode.Parse(arguments)!.AsObject();
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var args = method.GetParameters().Select(parameter => values.TryGetPropertyValue(parameter.Name!, out var value)
                ? JsonSerializer.Deserialize(value!.ToJsonString(), parameter.ParameterType, options) : parameter.DefaultValue).ToArray();
            var response = (CallToolResult)surface.Invoke(method, args)!;
            return JsonNode.Parse(((TextContentBlock)response.Content.Single()).Text)!.AsObject();
        }
        var state = Invoke("CallTool", "{\"name\":\"GetSessionState\",\"arguments\":{}}");
        check(state["ok"]!.GetValue<bool>() && state["data"]!["summary"]!.GetValue<string>().Contains("TIA-Portal MCP server state retrieved"),
            "CallTool invokes the migrated GetSessionState");
        // P8-81b: a device is created exactly as specified, so the spec names the PLC type (the tool-usage example's values).
        var scaffold = Invoke("CallTool", "{\"name\":\"BuildProjectScaffold\",\"arguments\":{\"spec\":\"{\\\"projectName\\\":\\\"Offline\\\",\\\"directoryPath\\\":\\\"C:/domain-offline\\\",\\\"plcMlfb\\\":\\\"6ES7 513-1AM03-0AB0\\\",\\\"plcVersion\\\":\\\"V3.0\\\"}\",\"dryRun\":true}}");
        check(scaffold["ok"]!.GetValue<bool>() && scaffold["data"]!["summary"]!.GetValue<string>().Contains("0 ok, 0 failed"),
            "CallTool invokes the migrated BuildProjectScaffold preview without connecting");
        EngineSurface.CheckHostRetirement(server, check);
    }
}
