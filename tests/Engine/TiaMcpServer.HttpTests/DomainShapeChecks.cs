using ModelContextProtocol.Server;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;

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
                var implementation = tool;
                var serviceName = name == "ListTestSuiteCases" ? "ReadTestSuiteCases" : name == "ListSivarcRules" ? "ReadSiVArcRules" : name == "ManageSivarcRule" ? "ManageSiVArcRule" : name;
                var driveContract = domain.Name == "Dcc" || domain.Name == "Startdrive" || domain.Name == "Teamcenter";
                if (driveContract)
                {
                    check(tool.ReturnType.Name == "CallToolResult", name + " exposes the V4 envelope boundary");
                    serviceName = tool.Name;
                }
                if (domain.Name == "SoftwareUnitDeep" || domain.Name == "Library" || domain.Name == "Sivarc" || domain.Name == "VersionControl")
                {
                    check(tool.Name.EndsWith("V4", StringComparison.Ordinal) && tool.ReturnType.Name == "CallToolResult",
                        name + " exposes the V4 envelope boundary");
                    serviceName = tool.Name.Substring(0, tool.Name.Length - "V4".Length);
                    if (domain.Name == "Library" || domain.Name == "Sivarc" || domain.Name == "VersionControl")
                        serviceName = serviceName switch {
                            "GetLibraryOverview" => "ReadLibraryOverview", "GetLibraryType" => "ReadLibraryType",
                            "GetSivarcRuleTree" => "ReadSivarcRuleTree", "ListSivarcBlockDefinitions" => "ReadSivarcBlockDefinitions",
                            "GenerateSivarc" => "GenerateSiVArc", "ListVersionControlWorkspaces" => "GetVersionControlWorkspaces",
                            "SynchronizeVersionControlWorkspace" => "SyncVersionControlWorkspace", _ => serviceName
                        };
                    implementation = tools.GetMethod(serviceName, all)!;
                    check(implementation.GetCustomAttribute<McpServerToolAttribute>() == null,
                        name + " retains its unregistered service implementation without an alias");
                    var callbacks = EngineSurface.MethodFamily(tool).Where(method =>
                        method.Name.StartsWith("<" + tool.Name + ">", StringComparison.Ordinal));
                    check(callbacks.Any(callback => {
                        var body = callback.GetMethodBody()!.GetILAsByteArray()!;
                        return Enumerable.Range(0, Math.Max(0, body.Length - 4)).Any(index =>
                            (body[index] == 0x28 || body[index] == 0x6f)
                            && BitConverter.ToInt32(body, index + 1) == implementation.MetadataToken);
                    }), name + " V4 mapping calls its original implementation");
                }
                // Tool and service signatures may differ (casts in the tool); parameter types only separate overloads.
                MethodInfo method;
                try { method = surface.Method(serviceName); }
                catch (AmbiguousMatchException) { method = surface.Method(serviceName, implementation.GetParameters().Select(parameter => parameter.ParameterType).ToArray()); }
                if (driveContract)
                    implementation = EngineSurface.MethodFamily(tool).FirstOrDefault(callback => {
                        if (!callback.Name.StartsWith("<" + tool.Name + ">", StringComparison.Ordinal)) return false;
                        var body = callback.GetMethodBody()?.GetILAsByteArray();
                        return body != null && Enumerable.Range(0, Math.Max(0, body.Length - 4)).Any(index =>
                            (body[index] == 0x28 || body[index] == 0x6f)
                            && BitConverter.ToInt32(body, index + 1) == method.MetadataToken);
                    }) ?? tool;
                var target = surface.Target(method);
                var toolTarget = surface.Target(tool);
                check(method.DeclaringType == service && surface.Tool(name) == tool && !tool.IsStatic
                    && ReferenceEquals(target, surface.Target(method)) && ReferenceEquals(toolTarget, surface.Target(tool)),
                    domain.Name + " singleton ownership: " + name);
                check(ReferenceEquals(service.GetField("_session", all)!.GetValue(target), session)
                    && ReferenceEquals(tools.GetFields(all).Single(field => field.FieldType == service).GetValue(toolTarget), target),
                    domain.Name + " tool uses its service with the shared session: " + name);
                var il = implementation.GetMethodBody()!.GetILAsByteArray()!;
                bool callsService = Enumerable.Range(0, Math.Max(0, il.Length - 4)).Any(index =>
                    (il[index] == 0x28 || il[index] == 0x6f) && BitConverter.ToInt32(il, index + 1) == method.MetadataToken);
                if (new[] { "TestSuite", "V20Options", "OptionalEngineering", "SpecializedExchange" }.Contains(domain.Name) && tool.ReturnType.Name == "CallToolResult")
                {
                    callsService = EngineSurface.MethodFamily(tool).Where(callback => callback.Name.StartsWith("<" + tool.Name + ">", StringComparison.Ordinal))
                        .Any(callback => {
                            var body = callback.GetMethodBody()!.GetILAsByteArray()!;
                            return Enumerable.Range(0, Math.Max(0, body.Length - 4)).Any(index =>
                                (body[index] == 0x28 || body[index] == 0x6f) && BitConverter.ToInt32(body, index + 1) == method.MetadataToken);
                        });
                }
                EngineSurface.CheckIl(check, callsService, domain.Name + " tool calls service: " + name, implementation, method);
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
            check(ex.InnerException?.GetType().Name == "PortalException"
                && ex.InnerException.GetType().GetProperty("Code")!.GetValue(ex.InnerException)?.ToString() == "InvalidState"
                && ex.InnerException.Message ==
                "No project is open. Call Connect, then AttachToOpenProject / OpenProject first.",
                "Disconnected VCI acquisition retains its message and exposes a typed precondition");
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
            if (domain.Name == "HmiExchange")
                check(methods.Select(method => method.GetCustomAttribute<McpServerToolAttribute>()!.Name).OrderBy(name => name)
                    .SequenceEqual(new[] { "ListHmiScreens", "ListHmiTagTables", "ListHmiTags", "ListHmiConnections",
                        "ExportHmiScreen", "ExportHmiTagTable", "ExportHmiConnection", "ExportHmiProgram",
                        "ImportHmiScreen", "ImportHmiTagTable", "ImportHmiConnection",
                        "ImportHmiScreensFromDirectory", "ImportHmiTagTablesFromDirectory" }.OrderBy(name => name)),
                    "HMI exchange registers exactly the V4 names without legacy aliases");
            foreach (var tool in methods)
            {
                var name = tool.GetCustomAttribute<McpServerToolAttribute>()!.Name!;
                check(!tool.IsStatic && surface.Tool(name) == tool, domain.Name + " owns " + name);
                string serviceName = domain.Name == "HmiExchange" && name.StartsWith("ListHmi", StringComparison.Ordinal)
                    ? "Get" + name.Substring(4) : name;
                var implementation = tool;
                if (domain.Name == "HmiDescribe")
                {
                    check(tool.Name.EndsWith("V4", StringComparison.Ordinal), name + " exposes its V4 boundary");
                    implementation = tools.GetMethod(tool.Name.Substring(0, tool.Name.Length - 2), all)!;
                    check(implementation.GetCustomAttribute<McpServerToolAttribute>() == null,
                        name + " retains an unregistered implementation without an alias");
                }
                var called = name == "CompileHmiDiagnostics" ? surface.Method("CompileAndDiagnoseCore", all)
                    : service.GetMethod(serviceName, all)!;
                bool callsService = HmiCallsService(tool, called, tools, new HashSet<MethodBase>());
                if (domain.Name == "HmiDescribe")
                    check(HmiCallsService(tool, implementation, tools, new HashSet<MethodBase>()),
                        name + " V4 boundary calls its original implementation");
                EngineSurface.CheckIl(check, callsService,
                    name + " calls its service or shared compiler", tool, called);
                check(tool.ReturnType == typeof(ModelContextProtocol.Protocol.CallToolResult), name + " returns the V4 envelope carrier");
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

    private static readonly Dictionary<short, OpCode> HmiOpCodes = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(field => field.FieldType == typeof(OpCode)).Select(field => (OpCode)field.GetValue(null)!).ToDictionary(op => op.Value);

    // V4 adapters invoke the original service through captured delegates and, for classic HMI, a response helper.
    // Follow only methods reachable from this entry within its tool type; unrelated tool calls cannot satisfy the check.
    internal static bool HmiCallsService(MethodBase caller, MethodInfo service, Type owner, HashSet<MethodBase> seen)
    {
        if (!seen.Add(caller)) return false;
        var il = caller.GetMethodBody()?.GetILAsByteArray() ?? Array.Empty<byte>();
        for (int i = 0; i < il.Length;)
        {
            short code = il[i++] == 0xfe ? (short)(0xfe00 | il[i++]) : (short)il[i - 1];
            var op = HmiOpCodes[code];
            if (op == OpCodes.Call || op == OpCodes.Callvirt || op == OpCodes.Ldftn || op == OpCodes.Ldvirtftn)
            {
                var target = caller.Module.ResolveMethod(BitConverter.ToInt32(il, i));
                if (target == service) return true;
                var declaring = target!.DeclaringType;
                while (declaring != null && declaring != owner) declaring = declaring.DeclaringType;
                if (declaring == owner && HmiCallsService(target, service, owner, seen)) return true;
            }
            i += op.OperandType == OperandType.InlineNone ? 0
                : op.OperandType == OperandType.ShortInlineBrTarget || op.OperandType == OperandType.ShortInlineI || op.OperandType == OperandType.ShortInlineVar ? 1
                : op.OperandType == OperandType.InlineVar ? 2
                : op.OperandType == OperandType.InlineI8 || op.OperandType == OperandType.InlineR ? 8
                : op.OperandType == OperandType.InlineSwitch ? 4 + 4 * BitConverter.ToInt32(il, i) : 4;
        }
        return false;
    }
}
