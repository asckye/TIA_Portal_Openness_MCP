using ModelContextProtocol.Server;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;

// All engine types come from the woven release EXE loaded by the harness, never a project reference.
internal sealed class EngineSurface
{
    private const BindingFlags Public = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static;
    private const BindingFlags All = Public | BindingFlags.NonPublic;
    private static readonly ConditionalWeakTable<Assembly, EngineSurface> surfaces = new ConditionalWeakTable<Assembly, EngineSurface>();

    // engine-decomposition.md: Portal remains the session; add the registered domain services here as they move.
    // Do not scan arbitrary Siemens helpers or SDK types as services.
    private static readonly string[] serviceTypeNames = {
        "TiaMcpServer.Siemens.Services.CfcService",
        "TiaMcpServer.Siemens.Services.TestSuiteService",
        "TiaMcpServer.Siemens.Services.V20OptionsService",
        "TiaMcpServer.Siemens.Services.OptionalEngineeringService",
        "TiaMcpServer.Siemens.Services.SpecializedExchangeService",
        "TiaMcpServer.Siemens.Services.SoftwareUnitDeepService",
        "TiaMcpServer.Siemens.Services.DccService",
        "TiaMcpServer.Siemens.Services.TeamcenterService",
        "TiaMcpServer.Siemens.Services.StartdriveService",
        "TiaMcpServer.Siemens.Services.SafetyManagementService",
        "TiaMcpServer.Siemens.Services.SafetyValidationService",
        "TiaMcpServer.Siemens.Services.SecurityDeepService",
        "TiaMcpServer.Siemens.Services.CertificateManagementService",
        "TiaMcpServer.Siemens.Services.ProjectSecurityService",
        "TiaMcpServer.Siemens.Services.PlcTablesService",
        "TiaMcpServer.Siemens.Services.LibraryService",
        "TiaMcpServer.Siemens.Services.VersionControlService",
        "TiaMcpServer.Siemens.Services.SivarcService",
        "TiaMcpServer.Siemens.Services.ClassicHmiFoldersService",
        "TiaMcpServer.Siemens.Services.MotionProDiagClassicHmiService",
        "TiaMcpServer.Siemens.Services.AlarmsService",
        "TiaMcpServer.Siemens.Services.OpcUaService",
        "TiaMcpServer.Siemens.Services.TechnologyObjectsService",
        "TiaMcpServer.Siemens.Services.DevicesService",
        "TiaMcpServer.Siemens.Services.HardwareManagementService",
        "TiaMcpServer.Siemens.Services.HardwareAmlService",
        "TiaMcpServer.Siemens.Services.ModulesService",
        "TiaMcpServer.Siemens.Services.HardwareDevicesService",
        "TiaMcpServer.Siemens.Services.HardwareNetworkPortService",
        "TiaMcpServer.Siemens.Services.HardwareServicesPortService",
        "TiaMcpServer.Siemens.Services.HardwareNetworkService",
        "TiaMcpServer.Siemens.Services.HardwareServicesService",
        "TiaMcpServer.Siemens.Services.OnlineDownloadService",
        "TiaMcpServer.Siemens.Services.PlcBlocksService",
        "TiaMcpServer.Siemens.Services.PlcOrganisationPortService",
        "TiaMcpServer.Siemens.Services.PlcSoftwareService",
        "TiaMcpServer.Siemens.Services.ReflectionService",
        "TiaMcpServer.Siemens.Services.EngineeringAuditService",
        "TiaMcpServer.Siemens.Services.SoftwareUnitManagementService",
        "TiaMcpServer.Siemens.Services.TypesService",
        "TiaMcpServer.Siemens.Services.DocumentsService",
        "TiaMcpServer.Siemens.Services.PlcExternalSourcesService",
        "TiaMcpServer.Siemens.Services.NativeExchangeService",
        "TiaMcpServer.Siemens.Services.HmiInspectionService",
        "TiaMcpServer.Siemens.Services.MigrationReadService",
        "TiaMcpServer.Siemens.Services.RuntimeSettingsService",
        "TiaMcpServer.Siemens.Services.GraphicSelectionService",
        "TiaMcpServer.Siemens.Services.GlobalScriptEditService",
        "TiaMcpServer.Siemens.Services.UnifiedHmiService",
        "TiaMcpServer.Siemens.Services.UnifiedObjectServicesService",
        "TiaMcpServer.Siemens.Services.UnifiedUiModelService",
        "TiaMcpServer.Siemens.Services.UnifiedScreenItemsService",
        "TiaMcpServer.Siemens.Services.UnifiedEngineeringService",
        "TiaMcpServer.Siemens.Services.UnifiedExchangeService",
        "TiaMcpServer.Siemens.Services.UnifiedEventsService",
        "TiaMcpServer.Siemens.Services.UnifiedHmiGroupsService",
        "TiaMcpServer.Siemens.Services.HmiExchangeService",
        "TiaMcpServer.Siemens.Services.HmiDescribeService",
        "TiaMcpServer.Siemens.Services.HmiTagDeletionService"
    };
    private static readonly string[] helperTypeNames = { "TiaMcpServer.Siemens.EngineeringSessionHelpers",
        "TiaMcpServer.ModelContextProtocol.OfflineToolExecution",
        "TiaMcpServer.ModelContextProtocol.XmlBuildResults",
        "TiaMcpServer.ModelContextProtocol.PlcCompilation" };
    private readonly Assembly engine;
    private readonly Type[] toolTypes;
    private readonly Type sessionType;
    private readonly Type[] serviceTypes;
    internal Assembly? Adapter { get; private set; }

    internal static EngineSurface For(Assembly engine) => surfaces.GetValue(engine, Create);

    internal static string[] HostTools(Assembly engine)
    {
        var families = Program.FindReferencedType(engine, "TiaMcp.Adapters.Contracts.PortedFamilies");
        return ((System.Collections.IEnumerable)families.GetField("All")!.GetValue(null)!).Cast<object>()
            .Where(f => new[] { "F01", "F02", "F03" }.Contains((string)f.GetType().GetProperty("Name")!.GetValue(f)!))
            .SelectMany(f => (string[])f.GetType().GetProperty("Tools")!.GetValue(f)!).ToArray();
    }

    internal static bool IsHostTool(Assembly engine, string name) => HostTools(engine).Contains(name, StringComparer.Ordinal);

    internal static string[] PortedTools(Assembly engine, string release)
    {
        var families = Program.FindReferencedType(engine, "TiaMcp.Adapters.Contracts.PortedFamilies");
        var available = families.GetMethod("Available", new[] { typeof(string), typeof(string) });
        return ((System.Collections.IEnumerable)families.GetField("All")!.GetValue(null)!).Cast<object>()
            .Where(f => (bool)f.GetType().GetMethod("Available")!.Invoke(f, new object[] { release })!)
            .SelectMany(f => (string[])f.GetType().GetProperty("Tools")!.GetValue(f)!)
            .Where(tool => available == null || (bool)available.Invoke(null, new object[] { release, tool })!).ToArray();
    }

    internal static void CheckHostRetirement(Assembly engine, Action<bool, string> check)
    {
        var names = HostTools(engine);
        check(names.Length == 64 && names.Distinct(StringComparer.Ordinal).Count() == 64, "G4 B1 has exactly 64 host declarations");
        var catalog = engine.GetType("TiaMcpServer.ModelContextProtocol.ToolCatalog", true)!;
        var instance = catalog.GetProperty("Engine", All)!.GetValue(null)!;
        var methods = ((System.Collections.IEnumerable)catalog.GetProperty("Methods", All)!.GetValue(instance)!).Cast<object>()
            .Select(p => (string)p.GetType().GetProperty("Key")!.GetValue(p)!).ToArray();
        foreach (var name in names) check(!methods.Contains(name, StringComparer.Ordinal), "G4 engine does not register " + name);
        foreach (var name in new[] { "PlcOfflineTools", "HmiOfflineTools", "HostMetaTools", "EngineeringDiagnosticsTools", "OfflineAnalysisTools", "PlcDocumentationTools", "ExportTools", "TemplateTools", "QualityAuditTools", "V21EcosystemTools" })
            check(engine.GetType("TiaMcpServer.ModelContextProtocol." + name, false) == null, "G4 engine has no " + name + " implementation");

    }

    internal static void CheckPortedHardwareRetirement(Assembly engine, Action<bool, string> check)
    {
        var families = Program.FindReferencedType(engine, "TiaMcp.Adapters.Contracts.PortedFamilies");
        var family = families.GetMethod("ForTool")!.Invoke(null, new object[] { "GetDeviceAddressing" })!;
        var tools = (string[])family.GetType().GetProperty("Tools")!.GetValue(family)!;
        check(tools.Length == 5, "G4 F19 has five shared declarations");
        var all = ((System.Collections.IEnumerable)families.GetField("All")!.GetValue(null)!).Cast<object>()
            .Where(f => new[] { "F18", "F19", "F20", "F21" }.Contains((string)f.GetType().GetProperty("Name")!.GetValue(f)!))
            .SelectMany(f => (string[])f.GetType().GetProperty("Tools")!.GetValue(f)!).ToArray();
        check(all.Length == 46 && all.Distinct().Count() == 46, "G4 B2 and F19 retain all 46 shared declarations including unavailable APIs");
        tools = all;
        check(engine.GetType("TiaMcpServer.Siemens.Services.ModulesService", false) == null, "G4 engine has no native ModulesService");
        foreach (var name in new[] { "HardwareAmlService", "HardwareManagementService", "HardwareDevicesService", "HardwareNetworkPortService", "HardwareServicesPortService" })
            check(!engine.GetType("TiaMcpServer.Siemens.Services." + name, true)!.GetFields(All).Any(f => f.FieldType.FullName == "TiaMcpServer.Siemens.IEngineeringSession"), "G4 managed port has no native session: " + name);

        check(engine.GetType("TiaMcpServer.Siemens.Services.AddressesService", false) == null,
            "G4 engine has no native AddressesService implementation");
        var portal = engine.GetType("TiaMcpServer.Siemens.Portal", true)!;
        foreach (var name in new[] { "ReadDeviceAddressing", "UpdateDeviceAddress", "GetDeviceIpAddress" })
            check(!portal.GetMethods(All).Any(method => method.Name == name), "G4 Portal no longer implements " + name);
        // Shared declarations remain available to nested CLR bridges. Registration
        // ownership is defined by the exported worker catalog consumed by the host.
        string path = Path.GetTempFileName();
        try
        {
            engine.GetType("TiaMcpServer.Cli.ToolCatalogExport", true)!.GetMethod("Write", All)!.Invoke(null, new object[] { path });
            using var catalog = JsonDocument.Parse(File.ReadAllText(path));
            foreach (var tool in tools)
            {
                check(!catalog.RootElement.GetProperty("tools").EnumerateArray().Any(entry => entry.GetProperty("name").GetString() == tool)
                    && !catalog.RootElement.GetProperty("descriptors").EnumerateArray().Any(entry => entry.GetProperty("name").GetString() == tool),
                    "G4 engine worker catalog excludes " + tool);
            }
        }
        finally { File.Delete(path); }
    }

    private static EngineSurface Create(Assembly engine)
    {
        var reference = engine.GetReferencedAssemblies().SingleOrDefault(name => name.Name!.StartsWith("TiaMcp.Adapter.", StringComparison.Ordinal));
        // Baseline engines from before step H remain readable by comparison runs.
        var adapter = reference == null ? null : Assembly.LoadFrom(Path.Combine(Path.GetDirectoryName(engine.Location)!, reference.Name + ".dll"));
        var assemblies = adapter == null ? new[] { engine } : new[] { engine, adapter };
        var services = serviceTypeNames.Concat(helperTypeNames).Where(name => engine.GetType(name, false) != null || name.EndsWith(".ModulesService", StringComparison.Ordinal)).Select(name => name.EndsWith(".ModulesService", StringComparison.Ordinal)
            && engine.GetType(name, false) == null ? "TiaMcpServer.Siemens.Services.HardwareModulesService" : name).Select(name => assemblies
            .Select(assembly => assembly.GetType(name, false)).Where(type => type != null).Single()!).ToList();
        if (adapter != null)
        {
            services.Add(adapter.GetType("TiaMcp.Adapters.PlcServices", true)!);

        }
        return new EngineSurface(engine, assemblies.SelectMany(LoadableTypes), engine.GetType("TiaMcpServer.Siemens.Portal", true)!, services.ToArray()) { Adapter = adapter };
    }

    internal string[] CrossAssemblyMemberNames() => serviceTypes.Where(type => type.Assembly == Adapter)
        .SelectMany(type => type.GetMembers(All | BindingFlags.DeclaredOnly)).Select(member => member.Name)
        .Intersect(new[] { sessionType }.Concat(serviceTypes.Where(type => type.Assembly == engine))
            .SelectMany(type => type.GetMembers(All | BindingFlags.DeclaredOnly)).Select(member => member.Name))
        .Where(name => name != ".ctor" && name != ".cctor").Distinct().ToArray();

    // Explicit types let the harness exercise future instance tools and services without adding engine fixtures.
    internal EngineSurface(Assembly engine, IEnumerable<Type> toolTypes, Type sessionType, params Type[] serviceTypes)
    {
        this.engine = engine;
        this.toolTypes = toolTypes.Where(type => type.GetCustomAttribute<McpServerToolTypeAttribute>() != null).Distinct().ToArray();
        this.sessionType = sessionType;
        this.serviceTypes = serviceTypes.Distinct().Where(type => type != sessionType).ToArray();
    }

    private static IEnumerable<Type> LoadableTypes(Assembly assembly)
    {
        try { return assembly.GetTypes(); }
        catch (ReflectionTypeLoadException ex)
        {
            foreach (var error in ex.LoaderExceptions.Where(error => error != null))
                Console.Error.WriteLine("EngineSurface: skipped a type that could not load: " + error!.Message);
            return ex.Types.Where(type => type != null)!;
        }
    }

    internal MethodInfo Tool(string toolName) => Unique(
        toolTypes.SelectMany(type => type.GetMethods(Public)).Where(method =>
        {
            var attribute = method.GetCustomAttribute<McpServerToolAttribute>();
            return attribute != null && string.Equals(attribute.Name ?? method.Name, toolName, StringComparison.OrdinalIgnoreCase);
        }), "MCP tool '" + toolName + "'", toolTypes);

    internal MethodInfo ToolMethod(string methodName, BindingFlags flags = All, Type[]? parameterTypes = null)
        => Unique(toolTypes.SelectMany(type => type.GetMethods(flags)).Where(method => Matches(method, methodName, parameterTypes)),
            "tool method '" + methodName + "'", toolTypes);

    internal MethodInfo ToolMethod(string methodName, Type[] parameterTypes) => ToolMethod(methodName, All, parameterTypes);

    internal MemberInfo Member(string name, BindingFlags flags = All)
        => SessionMember(name, type => type.GetMember(name, flags));

    internal FieldInfo Field(string name, BindingFlags flags = All)
        => SessionMember(name, type => type.GetFields(flags).Where(field => field.Name == name));

    internal PropertyInfo Property(string name, BindingFlags flags = Public)
        => SessionMember(name, type => type.GetProperties(flags).Where(property => property.Name == name));

    internal MethodInfo Method(string name, BindingFlags flags = Public, Type[]? parameterTypes = null)
        => SessionMember(name, type => type.GetMethods(flags).Where(method => Matches(method, name, parameterTypes)));

    internal MethodInfo Method(string name, Type[] parameterTypes) => Method(name, Public, parameterTypes);

    private T SessionMember<T>(string name, Func<Type, IEnumerable<T>> find) where T : MemberInfo
    {
        var session = find(sessionType).ToArray();
        if (session.Length != 0) return Unique(session.Concat(serviceTypes.Where(type => type.Assembly == Adapter).SelectMany(find)),
            "session/adapter member '" + name + "'", new[] { sessionType }.Concat(serviceTypes));
        return Unique(serviceTypes.SelectMany(find), "session/service member '" + name + "'", new[] { sessionType }.Concat(serviceTypes));
    }

    private static bool Matches(MethodInfo method, string name, Type[]? parameterTypes)
        => method.Name == name && (parameterTypes == null || method.GetParameters().Select(parameter => parameter.ParameterType).SequenceEqual(parameterTypes));

    private static T Unique<T>(IEnumerable<T> candidates, string name, IEnumerable<Type> searched) where T : MemberInfo
    {
        var matches = candidates.ToArray();
        if (matches.Length == 1) return matches[0];
        if (matches.Length == 0)
            throw new MissingMemberException("EngineSurface could not find " + name + ". Searched: " + string.Join(", ", searched.Select(type => type.FullName)));
        throw new AmbiguousMatchException("EngineSurface found ambiguous " + name + ": " + string.Join("; ", matches.Select(Describe)));
    }

    internal object? Target(MethodInfo method) => method.IsStatic ? null : engine.GetType("TiaMcpServer.EngineServices", true)!
        .GetMethod("Get", All, null, new[] { typeof(Type) }, null)!.Invoke(null, new object[] { method.DeclaringType! });

    internal object? Invoke(MethodInfo method, object?[]? arguments) => method.Invoke(Target(method), arguments);

    // Guard-only checks must not construct a session or acquire any native resources.
    internal static object? InvokeUninitialized(MethodInfo method, object?[] arguments)
    {
        if (method.IsStatic) return method.Invoke(null, arguments);
        var type = method.DeclaringType!;
        var target = System.Runtime.Serialization.FormatterServices.GetUninitializedObject(type);
        var session = type.GetField("_session", BindingFlags.NonPublic | BindingFlags.Instance);
        if (session?.FieldType.FullName == "TiaMcpServer.Siemens.IEngineeringSession")
        {
            // A migrated guard reaches the same empty kernel through its injected interface.
            // Allocate both objects without running constructors or registering a live session.
            var portal = type.Assembly.GetType("TiaMcpServer.Siemens.Portal", true)!;
            session.SetValue(target, System.Runtime.Serialization.FormatterServices.GetUninitializedObject(portal));
        }
        foreach (var field in type.GetFields(BindingFlags.NonPublic | BindingFlags.Instance))
        {
            if (field.FieldType.Namespace != "TiaMcpServer.Siemens.Services" || !new[] { "HardwareDevicesService", "HardwareModulesService", "HardwareManagementService", "HardwareNetworkPortService", "HardwareServicesPortService", "HardwareAmlService" }.Contains(field.FieldType.Name)) continue;
            var constructor = field.FieldType.GetConstructors(All).Single(c => c.GetParameters().Length >= 3);
            var values = constructor.GetParameters().Select((parameter, index) => index == 0
                ? (object)new Func<string, System.Text.Json.Nodes.JsonObject, System.Text.Json.Nodes.JsonNode?>((_, __) => throw new InvalidOperationException("Guard-only fixture must not dispatch native work."))
                : index == 1 ? (object)new Func<bool>(() => false) : index == 2 ? new Func<string>(() => "") : parameter.DefaultValue).ToArray();
            field.SetValue(target, constructor.Invoke(values));
        }
        return method.Invoke(target, arguments);
    }

    // Include compiler-generated delegate bodies on the resolved declaring type, even after a service move.
    internal static IEnumerable<MethodInfo> MethodFamily(MethodInfo method)
    {
        var type = method.DeclaringType!;
        return type.GetMethods(All).Concat(type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic)
            .SelectMany(nested => nested.GetMethods(All)));
    }

    internal static string Describe(MemberInfo member) => member.DeclaringType!.FullName + "." + member;

    internal static void CheckIl(Action<bool, string> check, bool ok, string message, params MethodInfo[] methods)
        => check(ok, ok ? message : message + " [" + string.Join("; ", methods.Select(Describe)) + "]");
}
