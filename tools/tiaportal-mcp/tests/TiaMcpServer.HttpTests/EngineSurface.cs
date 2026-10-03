using ModelContextProtocol.Server;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;

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
        "TiaMcpServer.Siemens.Services.AddressesService",
        "TiaMcpServer.Siemens.Services.HardwareNetworkService",
        "TiaMcpServer.Siemens.Services.HardwareServicesService"
    };
    private static readonly string[] helperTypeNames = { "TiaMcpServer.Siemens.EngineeringSessionHelpers" };
    private readonly Assembly engine;
    private readonly Type[] toolTypes;
    private readonly Type sessionType;
    private readonly Type[] serviceTypes;

    internal static EngineSurface For(Assembly engine) => surfaces.GetValue(engine, assembly =>
        new EngineSurface(assembly, LoadableTypes(assembly), assembly.GetType("TiaMcpServer.Siemens.Portal", true)!,
            serviceTypeNames.Concat(helperTypeNames).Select(name => assembly.GetType(name, true)!).ToArray()));

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
        if (session.Length != 0) return Unique(session, "session member '" + name + "'", new[] { sessionType });
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
        => method.Invoke(method.IsStatic ? null : System.Runtime.Serialization.FormatterServices.GetUninitializedObject(method.DeclaringType!), arguments);

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
