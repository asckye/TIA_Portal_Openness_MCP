using ModelContextProtocol.Server;
using System;
using System.Collections;
using System.Linq;
using System.Reflection;

internal static class EngineSurfaceChecks
{
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    [McpServerToolType]
    public sealed class InstanceTools
    {
        private readonly string dependency;
        internal int Calls;
        public InstanceTools(string dependency) { this.dependency = dependency; }
        [McpServerTool(Name = "SurfaceAlias")]
        public string Read(string suffix) => dependency + ":" + suffix + ":" + ++Calls;
        [McpServerTool(Name = "SurfaceDuplicate")]
        public void FirstDuplicate() { }
        private string Helper(int value) => value.ToString();
        public string Overload(int value) => value.ToString();
        public string Overload(string value) => value;
    }

    [McpServerToolType]
    public static class StaticTools
    {
        [McpServerTool]
        public static string SurfaceFallback() => "static";
        [McpServerTool(Name = "surfaceduplicate")]
        public static void SecondDuplicate() { }
    }

    public sealed class UnmarkedTools
    {
        [McpServerTool]
        public static void SurfaceUnmarked() { }
    }

    public sealed class Session
    {
        public void Shared() { }
        public void Signature(string value) { }
    }

    public sealed class Service
    {
        private int value = 7;
        public int Value => value;
        public void Shared() { }
        public void Signature(int value) { }
        public void Collision() { }
        public Func<int> WithDelegate(int offset) => () => value + offset;
    }

    public sealed class OtherService { public void Collision() { } }

    private sealed class Provider : IServiceProvider
    {
        internal readonly InstanceTools Instance = new InstanceTools("injected");
        internal int Requests;
        public object? GetService(Type type) { Requests++; return type == typeof(InstanceTools) ? Instance : null; }
    }

    internal static void Run(Assembly server, Action<bool, string> check)
    {
        var surface = new EngineSurface(server, typeof(EngineSurfaceChecks).Assembly.GetTypes(), typeof(Session), typeof(Service), typeof(OtherService));
        bool Missing(Action lookup, string name)
        {
            try { lookup(); return false; }
            catch (MissingMemberException ex) { return ex.Message.Contains(name) && ex.Message.Contains("Searched:"); }
        }
        bool Ambiguous(Action lookup, params string[] names)
        {
            try { lookup(); return false; }
            catch (AmbiguousMatchException ex) { return names.All(ex.Message.Contains); }
        }

        var instance = surface.Tool("surfacealias");
        check(instance.Name == "Read" && instance.DeclaringType == typeof(InstanceTools), "EngineSurface resolves MCP aliases case-insensitively with the declaring type");
        var fallback = surface.Tool("SurfaceFallback");
        check(fallback.DeclaringType == typeof(StaticTools) && fallback.IsStatic, "EngineSurface falls back to the CLR name across tool types");
        check(Missing(() => surface.Tool("Read"), "Read"), "EngineSurface does not expose an aliased tool by its CLR name");
        check(Missing(() => surface.Tool("SurfaceUnmarked"), "SurfaceUnmarked"), "EngineSurface ignores methods on types without the tool-type attribute");
        check(Missing(() => surface.Tool("Overload"), "Overload"), "EngineSurface excludes non-tool helpers from MCP name lookup");
        check(surface.ToolMethod("Helper", BindingFlags.NonPublic | BindingFlags.Instance).IsPrivate, "EngineSurface finds private CLR helpers");
        check(surface.ToolMethod("Overload", new[] { typeof(int) }).GetParameters()[0].ParameterType == typeof(int), "EngineSurface selects CLR overloads by parameter types");
        check(Ambiguous(() => surface.ToolMethod("Overload"), typeof(InstanceTools).FullName!, "Int32", "String"), "EngineSurface reports ambiguous CLR overloads with declaring types and signatures");
        check(Ambiguous(() => surface.Tool("SURFACEDUPLICATE"), typeof(InstanceTools).FullName!, "FirstDuplicate", typeof(StaticTools).FullName!, "SecondDuplicate"), "EngineSurface reports case-insensitive MCP name collisions across types");
        check(Missing(() => surface.ToolMethod("Helper", BindingFlags.Public | BindingFlags.Instance), "Helper"), "EngineSurface preserves explicit method visibility flags");

        check(surface.Method("Shared").DeclaringType == typeof(Session), "EngineSurface searches the session before services");
        check(surface.Method("Signature", new[] { typeof(int) }).DeclaringType == typeof(Service), "EngineSurface searches services for the requested overload");
        check(surface.Member("value").DeclaringType == typeof(Service) && surface.Field("value").IsPrivate
            && surface.Property("Value").DeclaringType == typeof(Service), "EngineSurface resolves members, private fields and properties on services");
        check(Missing(() => surface.Method("Absent"), "Absent"), "EngineSurface reports missing session members with the search set");
        check(Ambiguous(() => surface.Method("Collision"), typeof(Service).FullName!, typeof(OtherService).FullName!), "EngineSurface rejects ambiguous members across services");
        var family = surface.Method("WithDelegate");
        check(EngineSurface.MethodFamily(family).Any(method => method.DeclaringType!.DeclaringType == typeof(Service) && method.Name.Contains("WithDelegate")), "EngineSurface follows delegate IL on the resolved service type");
        string? failure = null;
        EngineSurface.CheckIl((ok, message) => failure = message, false, "original assertion", family);
        check(failure != null && failure.StartsWith("original assertion") && failure.Contains(typeof(Service).FullName!) && failure.Contains("WithDelegate"), "EngineSurface IL failures retain the assertion and name its declaring type");

        var engine = EngineSurface.For(server);
        var catalogType = server.GetType("TiaMcpServer.ModelContextProtocol.ToolCatalog", true)!;
        var catalog = catalogType.GetProperty("Engine", All)!.GetValue(null)!;
        var entries = (IEnumerable)catalogType.GetProperty("Methods", All)!.GetValue(catalog)!;
        check(entries.Cast<object>().All(entry =>
        {
            var type = entry.GetType();
            return engine.Tool((string)type.GetProperty("Key")!.GetValue(entry)!) == (MethodInfo)type.GetProperty("Value")!.GetValue(entry)!;
        }), "EngineSurface matches every tool in the woven engine catalog");

        var services = server.GetType("TiaMcpServer.EngineServices", true)!;
        var host = services.GetProperty("Host", All)!;
        var previous = host.GetValue(null);
        var provider = new Provider();
        try
        {
            services.GetMethod("SetServiceProvider", All)!.Invoke(null, new object[] { provider });
            check(ReferenceEquals(surface.Target(instance), provider.Instance), "EngineSurface resolves an instance target through the loaded EngineServices");
            check((string?)surface.Invoke(instance, new object[] { "first" }) == "injected:first:1"
                && (string?)surface.Invoke(instance, new object[] { "second" }) == "injected:second:2" && provider.Requests == 3,
                "EngineSurface invokes the registered singleton with its injected dependency");
            check(surface.Target(fallback) == null && (string?)surface.Invoke(fallback, null) == "static" && provider.Requests == 3,
                "EngineSurface invokes static tools with null and without resolving services");
        }
        finally { host.SetValue(null, previous); }
        check(ReferenceEquals(host.GetValue(null), previous), "EngineSurface fixture restores the engine service provider");
        PilotToolChecks.Run(server, check);
    }
}
