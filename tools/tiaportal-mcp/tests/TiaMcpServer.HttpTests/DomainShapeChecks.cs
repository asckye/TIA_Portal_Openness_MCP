using ModelContextProtocol.Server;
using System;
using System.Linq;
using System.Reflection;

internal static class DomainShapeChecks
{
    internal static void Run(Assembly server, Action<bool, string> check)
    {
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        var surface = EngineSurface.For(server);
        var portal = server.GetType("TiaMcpServer.Siemens.Portal", true)!;
        var contract = server.GetType("TiaMcpServer.Siemens.IEngineeringSession", true)!;
        var provider = (IServiceProvider)server.GetType("TiaMcpServer.EngineServices", true)!.GetProperty("Provider", all)!.GetValue(null)!;
        var session = provider.GetService(contract);
        check(session != null && ReferenceEquals(session, provider.GetService(portal)), "Optional domains share the Portal singleton");
        var domains = new[] {
            (Name: "TestSuite", Count: 4), (Name: "V20Options", Count: 5),
            (Name: "OptionalEngineering", Count: 2), (Name: "SpecializedExchange", Count: 1),
            (Name: "SoftwareUnitDeep", Count: 7),
            (Name: "Dcc", Count: 8), (Name: "Teamcenter", Count: 3), (Name: "Startdrive", Count: 11)
        };
        foreach (var domain in domains)
        {
            var service = server.GetType("TiaMcpServer.Siemens.Services." + domain.Name + "Service", true)!;
            var tools = server.GetType("TiaMcpServer.ModelContextProtocol." + domain.Name + "Tools", true)!;
            foreach (var type in new[] { service, tools })
                check(type.IsSealed && !type.GetInterfaces().Any(item => item.Name == "IDisposable" || item.Name == "IAsyncDisposable"),
                    type.FullName + " is sealed and non-disposable");
            var methods = tools.GetMethods(all).Where(method => method.GetCustomAttribute<McpServerToolAttribute>() != null).ToArray();
            check(methods.Length == domain.Count, domain.Name + " tool count");
            foreach (var tool in methods)
            {
                var name = tool.GetCustomAttribute<McpServerToolAttribute>()!.Name!;
                var method = surface.Method(name);
                var target = surface.Target(method);
                var toolTarget = surface.Target(tool);
                check(method.DeclaringType == service && surface.Tool(name) == tool && !tool.IsStatic
                    && ReferenceEquals(target, surface.Target(method)) && ReferenceEquals(toolTarget, surface.Target(tool)),
                    domain.Name + " singleton ownership: " + name);
                check(ReferenceEquals(service.GetField("_session", all)!.GetValue(target), session)
                    && ReferenceEquals(tools.GetFields(all).Single(field => field.FieldType == service).GetValue(toolTarget), target),
                    domain.Name + " tool uses its service with the shared session: " + name);
                var il = tool.GetMethodBody()!.GetILAsByteArray()!;
                bool callsService = Enumerable.Range(0, Math.Max(0, il.Length - 4)).Any(index =>
                    (il[index] == 0x28 || il[index] == 0x6f) && BitConverter.ToInt32(il, index + 1) == method.MetadataToken);
                EngineSurface.CheckIl(check, callsService, domain.Name + " tool calls service: " + name, tool, method);
                check(server.GetType("TiaMcpServer.ModelContextProtocol.McpServer", true)!.GetMethod(name, all) == null,
                    name + " needs no static CLI forwarder");
            }
        }
        foreach (var name in new[] { "ResolveSoftwareContainerUncached", "RequireHardwareUtility", "ExactSiVArcRoot",
            "ExactMasterCopy", "ExactLibraryType", "ExactTypeVersion", "MultilingualJson", "RequireUnitProvider", "ExactUnit",
            "OptionalUnit", "BlockRootOf", "TypeRootOf", "ExactObjectUnder", "DocumentMessages", "DocumentExportRow", "DocumentImportRow" })
            check(surface.Method(name, all).DeclaringType == portal && contract.GetMethod(name) != null,
                name + " stays on the kernel and is exposed through IEngineeringSession");
        check(surface.Method("LinkedTagRows", all).DeclaringType == portal, "LinkedTagRows remains shared with hardware on the kernel");
        foreach (var name in new[] { "ExactDriveItem", "ExactDriveContainer", "DccContainerSummary", "DcbLibraryRow",
            "AddressRow", "ExactTechnology", "InterfaceRow" })
            check(surface.Method(name, all).DeclaringType == portal && contract.GetMethod(name) != null,
                name + " stays on the kernel and is exposed through IEngineeringSession");
        check(surface.Method("ExactDriveObject", all, new[] { typeof(string), typeof(string), typeof(ushort), typeof(int) }).DeclaringType == portal
            && contract.GetMethod("ExactDriveObject") != null, "Shared drive resolution stays on the kernel");
        var teamcenter = server.GetType("TiaMcpServer.Siemens.Services.TeamcenterService", true)!;
        foreach (var name in new[] { "_teamcenterConnection", "_teamcenterConnectionLabel" })
            check(surface.Field(name, all).DeclaringType == teamcenter && portal.GetField(name, all) == null,
                name + " is private Teamcenter service state");
    }
}
