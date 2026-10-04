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
        foreach (var domain in new[] { (Name: "Runtime", Count: 7), (Name: "RuntimeChannel", Count: 8), (Name: "PlcSimAdvanced", Count: 5) })
        {
            var tools = server.GetType("TiaMcpServer.ModelContextProtocol." + domain.Name + "Tools", true)!;
            check(tools.IsSealed && !typeof(IDisposable).IsAssignableFrom(tools), domain.Name + " tools are sealed and non-disposable");
            var target = provider.GetService(tools);
            check(target != null && ReferenceEquals(target, provider.GetService(tools)), domain.Name + " tools are singletons");
            var methods = tools.GetMethods(all).Where(method => method.GetCustomAttribute<McpServerToolAttribute>() != null).ToArray();
            check(methods.Length == domain.Count, domain.Name + " runtime tool count");
            foreach (var method in methods)
            {
                check(!method.IsStatic && surface.Tool(method.Name) == method && ReferenceEquals(surface.Target(method), target),
                    domain.Name + " instance ownership: " + method.Name);
                check(server.GetType("TiaMcpServer.ModelContextProtocol.McpServer", true)!.GetMethod(method.Name, all) == null,
                    method.Name + " has no CLI caller or static forwarder");
            }
            if (domain.Name == "Runtime")
                check(ReferenceEquals(tools.GetField("_session", all)!.GetValue(target), session), "Runtime project reads share the engineering session");
            else
                check(tools.GetConstructors().All(constructor => constructor.GetParameters().Length == 0), domain.Name + " needs no engineering session");
        }
        foreach (var name in new[] { "GetPutGetAccess", "TraceTagCause", "TraceTagCauseLive" })
            check(contract.GetMethod(name) != null && surface.Method(name, all).DeclaringType == portal,
                name + " keeps its project implementation behind IEngineeringSession");
        var runtime = Assembly.LoadFrom(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(server.Location)!, "TiaMcp.Runtime.dll"));
        foreach (var name in new[] { "S7LiveReader", "OpcUaLiveReader", "S7WebApiChannel", "UnifiedOpenPipeChannel" })
            check(server.GetType("TiaMcpServer.Runtime." + name) == null && runtime.GetType("TiaMcpServer.Runtime." + name) != null,
                name + " belongs to the protocol assembly with its namespace preserved");
        foreach (var name in new[] { "PlcSimAdvancedChannel", "EnvironmentDoctor" })
            check(server.GetType("TiaMcpServer.Runtime." + name) != null && runtime.GetType("TiaMcpServer.Runtime." + name) == null,
                name + " stays in the engine");
        check(!runtime.GetReferencedAssemblies().Any(reference => reference.Name!.StartsWith("Siemens.Engineering", StringComparison.Ordinal)
            || reference.Name.StartsWith("Siemens.Simatic.Simulation", StringComparison.Ordinal) || reference.Name == "TiaMcpServer"),
            "Runtime assembly has no engineering dependency");
        check(!runtime.GetTypes().Any(type => type.FullName!.Contains("NativeCall")), "Runtime assembly is not woven");
        var domains = new[] {
            (Name: "TestSuite", Count: 4), (Name: "V20Options", Count: 5),
            (Name: "OptionalEngineering", Count: 2), (Name: "SpecializedExchange", Count: 1),
            (Name: "SoftwareUnitDeep", Count: 7),
            (Name: "Dcc", Count: 8), (Name: "Teamcenter", Count: 3), (Name: "Startdrive", Count: 11),
            (Name: "Library", Count: 18), (Name: "VersionControl", Count: 5), (Name: "Sivarc", Count: 9), (Name: "OnlineDownload", Count: 12)
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
                if (domain.Name == "Library" && new[] { "AnalyzeGlobalLibraryPackage", "PlanGlobalLibraryTemplateReuse",
                    "AnalyzeHmiTemplateReference", "AnalyzeUnifiedHmiTemplateLayout" }.Contains(name))
                {
                    var instance = surface.Target(tool);
                    check(!tool.IsStatic && surface.Tool(name) == tool && ReferenceEquals(instance, surface.Target(tool)),
                        "Offline library tool keeps its registered singleton: " + name);
                    check(ReferenceEquals(tools.GetFields(all).Single(field => field.FieldType == service).GetValue(instance), provider.GetService(service)),
                        "Offline library tool shares the existing library service: " + name);
                    check(server.GetType("TiaMcpServer.ModelContextProtocol.McpServer", true)!.GetMethod(name, all) == null,
                        name + " needs no static CLI forwarder");
                    continue;
                }
                // Tool and service signatures may differ (casts in the tool); parameter types only separate overloads.
                MethodInfo method;
                try { method = surface.Method(name); }
                catch (AmbiguousMatchException) { method = surface.Method(name, tool.GetParameters().Select(parameter => parameter.ParameterType).ToArray()); }
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
                var forwarder = server.GetType("TiaMcpServer.ModelContextProtocol.McpServer", true)!.GetMethod(name, all);
                check(forwarder == null && ReferenceEquals(surface.Target(tool), provider.GetService(tool.DeclaringType!)),
                    name + " resolves directly without a static CLI forwarder");
            }
        }
        foreach (var name in new[] { "ResolveSoftwareContainerUncached", "RequireHardwareUtility", "ExactSiVArcRoot",
            "ExactMasterCopy", "ExactLibraryType", "ExactTypeVersion", "MultilingualJson", "RequireUnitProvider", "ExactUnit",
            "OptionalUnit", "BlockRootOf", "TypeRootOf", "ExactObjectUnder", "DocumentMessages", "DocumentExportRow", "DocumentImportRow",
            "AvailablePlcPathsSuffix", "GetAllPlcSoftware", "ReadPlcConsistency", "RecoverableAuditError",
            "ReadReflectedString", "EnumerateReflectedProperty" })
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
        foreach (var name in new[] { "LibraryRef", "EngineeringLibraryFolder", "ExactMasterCopyPlcSource", "LibraryPathOf",
            "OptionPackageLibraryTypeKind", "ExactEngineeringDevice", "ExactDeviceItem", "ResolveHmiScreenOrThrow",
            "ExactNameList", "RequireSivarc", "SivarcShape" })
            check(surface.Method(name, all).DeclaringType == portal && contract.GetMethod(name) != null,
                name + " remains shared on the kernel through IEngineeringSession");
        check(surface.Method("Family", all).DeclaringType == portal && contract.GetMethod("GetSivarcFamily") != null,
            "SiVArc families remain shared with the optional-engineering reader");

        var vci = server.GetType("TiaMcpServer.Siemens.Services.VersionControlService", true)!;
        var first = provider.GetService(vci)!;
        var second = Activator.CreateInstance(vci, new[] { session })!;
        foreach (var name in new[] { "_vciOwnerProject", "_vciCached", "_vciKeepAlive" })
            check(vci.GetField(name, all) is FieldInfo field && !field.IsStatic,
                "VCI state belongs to the service instance: " + name);
        var rootsField = vci.GetField("_vciKeepAlive", all)!;
        var roots = (System.Collections.IList)rootsField.GetValue(first)!;
        var otherRoots = (System.Collections.IList)rootsField.GetValue(second)!;
        var owner = vci.GetField("_vciOwnerProject", all)!;
        var sentinel = new object();
        roots.Add(sentinel); owner.SetValue(first, sentinel);
        check(!ReferenceEquals(roots, otherRoots) && otherRoots.Count == 0, "VCI services do not share retained proxies");
        try
        {
            vci.GetMethod("RequireVci", all)!.Invoke(first, null);
            check(false, "Disconnected VCI service refuses acquisition");
        }
        catch (TargetInvocationException ex)
        {
            check(ex.InnerException is InvalidOperationException && ex.InnerException.Message ==
                "No project is open. Call Connect, then AttachToOpenProject / OpenProject first.",
                "Disconnected VCI acquisition retains its original error");
        }
        check(roots.Count == 0 && owner.GetValue(first) == null && vci.GetField("_vciCached", all)!.GetValue(first) == null,
            "Disconnected VCI acquisition clears the cached owner and retained proxies");
        check(ReferenceEquals(contract.GetProperty("Logger")!.GetValue(session), portal.GetField("_logger", all)!.GetValue(session)),
            "Online/download keeps the existing Portal logger instance and category");

        var mcp = server.GetType("TiaMcpServer.ModelContextProtocol.McpServer", true)!;
        foreach (var domain in new[] { (Name: "HmiExchange", Count: 13), (Name: "HmiDescribe", Count: 7), (Name: "HmiTagDeletion", Count: 1) })
        {
            var service = server.GetType("TiaMcpServer.Siemens.Services." + domain.Name + "Service", true)!;
            var tools = server.GetType("TiaMcpServer.ModelContextProtocol." + domain.Name + "Tools", true)!;
            var target = provider.GetService(service)!;
            var toolTarget = provider.GetService(tools)!;
            foreach (var type in new[] { service, tools })
                check(type.IsSealed && !typeof(IDisposable).IsAssignableFrom(type)
                    && ReferenceEquals(provider.GetService(type), provider.GetService(type)), domain.Name + " singleton lifetime: " + type.Name);
            check(ReferenceEquals(service.GetField("_session", all)!.GetValue(target), session)
                && ReferenceEquals(tools.GetField("_service", all)!.GetValue(toolTarget), target), domain.Name + " shares the engineering session");
            var methods = tools.GetMethods(all).Where(method => method.GetCustomAttribute<McpServerToolAttribute>() != null).ToArray();
            check(methods.Length == domain.Count, domain.Name + " tool count");
            foreach (var tool in methods)
            {
                var name = tool.GetCustomAttribute<McpServerToolAttribute>()!.Name!;
                check(!tool.IsStatic && surface.Tool(name) == tool, domain.Name + " owns " + name);
                var called = name == "CompileAndDiagnoseHmi" ? mcp.GetMethod("CompileAndDiagnoseHmiCore", all)!
                    : service.GetMethod(name, all)!;
                var il = tool.GetMethodBody()!.GetILAsByteArray()!;
                EngineSurface.CheckIl(check, Enumerable.Range(0, Math.Max(0, il.Length - 4)).Any(index =>
                    (il[index] == 0x28 || il[index] == 0x6f) && BitConverter.ToInt32(il, index + 1) == called.MetadataToken),
                    name + " calls its service or shared compiler", tool, called);
                var forwarder = mcp.GetMethod(name, all);
                check(forwarder == null && ReferenceEquals(surface.Target(tool), toolTarget),
                    name + " CLI target is the registered instance without a static forwarder");
            }
        }
        foreach (var name in new[] { "TryGetHmiTagRoot", "TryGetHmiTagTablesCollection", "TryFindHmiTagTable",
            "EnumerateHmiTagTablesRecursive", "FindExistingByName", "TryResolveChildGroupByPath",
            "TryImportEngineeringObjectIntoCollection", "GetBindingIdentity" })
            check(portal.GetMethods(all).Any(method => method.Name == name) && contract.GetMethod(name) != null,
                name + " remains shared on the kernel through IEngineeringSession");
        var types = server.GetType("TiaMcpServer.Siemens.Services.TypesService", true)!;
        var exchange = server.GetType("TiaMcpServer.Siemens.Services.HmiExchangeService", true)!;
        check(types.GetConstructors().Single().GetParameters().Select(p => p.ParameterType)
            .SequenceEqual(new[] { contract, exchange })
            && ReferenceEquals(types.GetField("_hmiExchange", all)!.GetValue(provider.GetService(types)), provider.GetService(exchange)),
            "Types service injects the registered HMI exchange singleton without a kernel dependency cycle");
        foreach (var name in new[] { "ImportHmiScreensFromDirectory", "ImportHmiTagTablesFromDirectory" })
            check(contract.GetMethod(name) == null && portal.GetMethod(name, all) == null
                && surface.Method(name).DeclaringType == exchange,
                name + " resolves directly to the HMI exchange service without a kernel forwarder");
        check(surface.Property("LastImportNotes").DeclaringType!.Name == "HmiExchangeService"
            && portal.GetProperty("LastImportNotes", all) == null, "HMI import notes belong to the exchange service");
    }
}
