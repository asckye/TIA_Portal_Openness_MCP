using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.FoundationHost;
using TiaMcp.Logic.V4;
using TiaMcp.Logic.V4.Inputs;
using TiaMcpServer.ModelContextProtocol;

// The net48 engine harness delegates its moved host checks to this net10 process.
// The normal registered test runner still executes every test in this assembly.
internal static class DomainShapeProgram
{
    private static int Main(string[] args)
    {
        if (args.Length != 1 || args[0] is not ("--domain-shape" or "--runtime-channel-shape")) throw new ArgumentException("Expected a domain shape mode.");
        var results = new List<object>();
        int failed = 0;
        void Check(bool ok, string name) { results.Add(new { ok, name }); if (!ok) failed++; }
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        var catalog = new EmptyCatalog();
        using var lifetime = new ImportStagingHostLifetime();
        var invoker = new WorkerToolInvoker(catalog, null!, lifetime);
        var local = (IReadOnlyDictionary<string, MethodInfo>)typeof(WorkerToolInvoker).GetField("local", all)!.GetValue(invoker)!;
        if (args[0] == "--runtime-channel-shape")
        {
            foreach (var name in new[] { "GetPlcWebVars", "GetPlcWebDiagnostics", "GetUnifiedRuntimeTags", "GetUnifiedRuntimeAlarms" })
                Check(local.ContainsKey(name) && local[name].GetParameters().All(p => p.Name != "dryRun"), name + " exposed (read-only, no dryRun)");
            foreach (var name in new[] { "WritePlcWebVars", "WriteUnifiedRuntimeTags", "InvokeUnifiedOpenPipe" })
            {
                var method = local[name];
                Check(Equals(method.GetParameters().Single(p => p.Name == "dryRun").DefaultValue, true), name + " defaults to preview");
                Check(Equals(method.GetParameters().Single(p => p.Name == "confirmWrite").DefaultValue, false), name + " requires explicit confirmWrite");
            }
            var modeTool = local["SetPlcWebOperatingMode"];
            Check(Equals(modeTool.GetParameters().Single(p => p.Name == "dryRun").DefaultValue, true), "SetPlcWebOperatingMode defaults to preview");
            Check(Equals(modeTool.GetParameters().Single(p => p.Name == "confirmModeChange").DefaultValue, false), "SetPlcWebOperatingMode requires explicit confirmModeChange");
            foreach (var name in new[] { "GetPlcWebVars", "WritePlcWebVars", "GetPlcWebDiagnostics", "SetPlcWebOperatingMode" })
                Check(Equals(local[name].GetParameters().Single(p => p.Name == "ignoreCertificateErrors").DefaultValue, false), name + " validates certificates by default");
            Console.WriteLine("HOST-DOMAIN:" + JsonSerializer.Serialize(results));
            return failed == 0 ? 0 : 1;
        }
        var callback = new CallbackInvoker();
        EngineHostConfiguration.Worker = null!;
        McpServer.ConfigureToolBridge(catalog, callback, () => false, new HashSet<string>());
        foreach (var domain in new[] { (Type: typeof(RuntimeTools), Count: 5), (Type: typeof(RuntimeChannelTools), Count: 8), (Type: typeof(PlcSimAdvancedTools), Count: 5) })
        {
            var type = domain.Type;
            var target = invoker.LocalTarget(type);
            Check(type.IsSealed && !typeof(IDisposable).IsAssignableFrom(type), type.Name + " tools are sealed and non-disposable");
            Check(ReferenceEquals(target, invoker.LocalTarget(type)), type.Name + " tools are singletons");
            var methods = type.GetMethods(all).Where(method => method.GetCustomAttribute<McpServerToolAttribute>() != null).ToArray();
            Check(methods.Length == domain.Count, type.Name + " runtime tool count");
            foreach (var method in methods)
            {
                var name = method.GetCustomAttribute<McpServerToolAttribute>()!.Name!;
                Check(!method.IsStatic && local[name] == method && ReferenceEquals(target, invoker.LocalTarget(method.DeclaringType!)), type.Name + " instance ownership: " + method.Name);
                Check(typeof(McpServer).GetMethod(method.Name, all) == null, method.Name + " has no CLI caller or static forwarder");
            }
            if (type == typeof(RuntimeTools))
            {
                var putGet = (Func<string, JsonObject>)type.GetField("_putGet", all)!.GetValue(target)!;
                Check(ReferenceEquals(putGet("fixture-device"), callback.Data) && callback.Name == "GetPlcPutGetAccess"
                    && callback.Path == "fixture-device", "Runtime project reads share the current engineering session through its dispatcher");
            }
            else if (type == typeof(PlcSimAdvancedTools))
                Check(type.GetFields(all).Single(field => field.Name == "_service").FieldType.Name == "PlcSimAdvancedPortService"
                    && !type.GetFields(all).Any(field => field.FieldType.Name == "IEngineeringSession"), type.Name + " uses its worker port and needs no engineering session");
            else Check(type.GetConstructors().All(constructor => constructor.GetParameters().Length == 0), type.Name + " needs no engineering session");
        }
        var runtime = typeof(TiaMcpServer.Runtime.S7LiveReader).Assembly;
        Check(runtime.GetType("TiaMcpServer.Runtime.PlcSimAdvancedChannel") == null, "net10 host protocol assembly excludes PlcSimAdvancedChannel");
        Check(runtime.GetType("TiaMcp.PlcWorker.Runtime.PlcSimAdvancedAdapter") == null, "net10 host protocol assembly excludes PlcSimAdvancedAdapter");
        Console.WriteLine("HOST-DOMAIN:" + JsonSerializer.Serialize(results));
        return failed == 0 ? 0 : 1;
    }

    private sealed class EmptyCatalog : IToolCatalogView
    {
        public IReadOnlyDictionary<string, ToolDescriptor> All { get; } = new Dictionary<string, ToolDescriptor>();
        public IReadOnlyDictionary<string, ToolDescriptor> IncludingUnavailable => All;
        public IReadOnlyList<ToolDescriptor> Lite { get; } = Array.Empty<ToolDescriptor>();
        public JsonArray BehaviorCapabilities { get; } = new();
        public ToolDescriptor? Find(string name, bool includeUnavailable = false) => null;
    }

    private sealed class CallbackInvoker : IToolInvoker
    {
        internal JsonObject Data { get; } = new();
        internal string? Name, Path;
        public ToolInvocationResult Invoke(string name, ToolArguments arguments, bool preview)
        {
            Name = name; Path = arguments.Json.GetProperty("devicePath").GetString();
            return new ToolInvocationResult(new CallToolResult { StructuredContent = new JsonObject { ["data"] = Data } }, false);
        }
        public Error? Bind(string name, ToolArguments arguments, out IBoundToolCall? call) => throw new NotSupportedException();
        public Error? ValidateArguments(ToolDescriptor tool, JsonElement arguments, JsonElement schema, bool typedFamiliesOnly = false) => throw new NotSupportedException();
        public McpServerTool CreateTool(ToolDescriptor tool) => throw new NotSupportedException();
    }
}
