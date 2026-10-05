using ModelContextProtocol.Server;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

internal static class UnifiedHmiDomainShapeChecks
{
    internal static void Run(Assembly server, Action<bool, string> check)
    {
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        var surface = EngineSurface.For(server);
        var portal = server.GetType("TiaMcpServer.Siemens.Portal", true)!;
        var contract = server.GetType("TiaMcpServer.Siemens.IEngineeringSession", true)!;
        var facade = server.GetType("TiaMcpServer.ModelContextProtocol.McpServer", true)!;
        var provider = (IServiceProvider)server.GetType("TiaMcpServer.EngineServices", true)!.GetProperty("Provider", all)!.GetValue(null)!;
        var session = provider.GetService(contract);
        foreach (var domain in new[] {
            (Name: "UnifiedHmi", Count: 22), (Name: "UnifiedObjectServices", Count: 12),
            (Name: "UnifiedUiModel", Count: 7), (Name: "UnifiedScreenItems", Count: 2),
            (Name: "UnifiedEngineering", Count: 3), (Name: "UnifiedExchange", Count: 3),
            (Name: "UnifiedEvents", Count: 1), (Name: "UnifiedHmiGroups", Count: 1)
        })
        {
            var service = server.GetType("TiaMcpServer.Siemens.Services." + domain.Name + "Service", true)!;
            var tools = server.GetType("TiaMcpServer.ModelContextProtocol." + domain.Name + "Tools", true)!;
            var target = provider.GetService(service);
            var toolTarget = provider.GetService(tools);
            check(target != null && toolTarget != null && ReferenceEquals(target, provider.GetService(service))
                && ReferenceEquals(toolTarget, provider.GetService(tools)), domain.Name + " singleton registration");
            check(ReferenceEquals(service.GetField("_session", all)!.GetValue(target), session)
                && ReferenceEquals(tools.GetField("_service", all)!.GetValue(toolTarget), target),
                domain.Name + " tools share the kernel through their service");
            foreach (var type in new[] { service, tools })
                check(type.IsSealed && !type.GetInterfaces().Any(item => item.Name == "IDisposable" || item.Name == "IAsyncDisposable"),
                    type.Name + " is sealed and non-disposable");
            var methods = tools.GetMethods(all).Where(method => method.GetCustomAttribute<McpServerToolAttribute>() != null).ToArray();
            check(methods.Length == domain.Count, domain.Name + " complete tool ownership");
            foreach (var tool in methods)
            {
                var name = tool.GetCustomAttribute<McpServerToolAttribute>()!.Name!;
                check(!tool.IsStatic && surface.Tool(name) == tool && ReferenceEquals(surface.Target(tool), toolTarget),
                    name + " resolves to the instance tool singleton");
                var method = service.GetMethod(name, all);
                if (method != null)
                {
                    check(portal.GetMethod(name, all) == null && surface.Method(name) == method,
                        name + " implementation belongs to its service");
                    var il = tool.GetMethodBody()!.GetILAsByteArray()!;
                    bool callsService = domain.Name == "UnifiedExchange"
                        ? DomainShapeChecks.HmiCallsService(tool, method, tools, new HashSet<MethodBase>())
                        : Enumerable.Range(0, Math.Max(0, il.Length - 4)).Any(index =>
                            (il[index] == 0x28 || il[index] == 0x6f) && BitConverter.ToInt32(il, index + 1) == method.MetadataToken);
                    EngineSurface.CheckIl(check, callsService,
                        name + " tool calls the migrated service", tool, method);
                }
                var forwarder = facade.GetMethod(name, all);
                check(forwarder == null && ReferenceEquals(surface.Target(tool), toolTarget),
                    name + " resolves directly without a static CLI facade");
            }
        }
        foreach (var name in new[] { "FindExistingByName", "TryGetEngineeringAttribute", "SummarizeHmiObjectReadback",
            "CoerceReflectionValue", "ExactUnifiedRoot", "UnifiedCollection", "FillUnifiedHmiPartnerNetworkInfo",
            "TryGetHmiTagRoot", "TryGetHmiTagTablesCollection", "TryFindHmiTagTable" })
            check(surface.Method(name, all).DeclaringType == portal && contract.GetMethod(name) != null,
                name + " remains on the kernel through IEngineeringSession");
        check(surface.Field("UnifiedAssembly", all).DeclaringType == portal
            && ReferenceEquals(contract.GetProperty("UnifiedAssembly")!.GetValue(session), surface.Field("UnifiedAssembly", all).GetValue(null)),
            "Unified assembly initialization remains on the kernel for the loaded-assembly API inventory");
    }
}
