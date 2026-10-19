extern alias foundationhost;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.FoundationHost;
using TiaMcp.Logic.V4.Inputs;
using TiaMcpServer.ModelContextProtocol;

namespace PlcExchangeTests;

internal static class PlcExchangeTarget
{
    internal static string Release => typeof(PlcExchangeTarget).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
        .Single(attribute => attribute.Key == "ExchangeRelease").Value!;
    internal static string Worker => Path.Combine(TiaMcp.Engine.Tests.Shared.TestPaths.RepositoryRoot(AppContext.BaseDirectory),
        "src", "PlcWorker", "bin", Release, "Release", "net48", "TiaMcp.PlcWorker." + Release + ".exe");
    internal static Assembly Adapter => Assembly.LoadFrom(Path.Combine(Path.GetDirectoryName(Worker)!, "TiaMcp.Adapter." + Release + ".dll"));
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    private static readonly ImportStagingHostLifetime Lifetime = new();
    internal static void ConfigureHost()
    {
        EngineHostConfiguration.ReleaseKey = Release;
        // The kept host binds its tool bridge to the current MCP session; open one with a worker
        // that refuses dispatch, like the engine variant's process-wide bridge without a worker.
        var worker = new NoWorker();
        EngineHostConfiguration.Worker = worker;
        // Same catalog as a served session: the host-registered shared tools join the declared ones.
        var catalog = new FoundationCatalog(Release, HostTools);
        McpServer.ConfigureToolBridge(catalog, new WorkerToolInvoker(catalog, worker, Lifetime, HostTools), () => false, new HashSet<string>());
    }
    // The four engine-only PlcExternalSourcesTools entries; the kept host registers them as shared tools.
    internal static readonly string[] EngineOnlyExternalSourceTools = { "ListPlcExternalSources", "ImportPlcExternalSource", "DeletePlcExternalSource", "GenerateBlocksFromExternalSource" };
    private static readonly Lazy<IReadOnlyList<McpServerTool>> Registered = new(() =>
        foundationhost::TiaMcp.FoundationHost.LegacyHostToolRegistry.Create(new MetadataWorker(), Release, false, hostPorts: false));
    // Production registration (LegacyHostToolRegistry) against a worker that refuses any dispatch.
    internal static IReadOnlyList<McpServerTool> HostTools => Registered.Value;
    internal static FoundationCatalog HostCatalog() => new(Release, HostTools);
    internal static McpServerTool RegisteredTool(string name) => HostTools.Single(tool => tool.ProtocolTool.Name == name);
    // What the V20/V21 engine host publishes: the frozen engine export
    // (tests/FoundationHost/TiaMcp.EngineHost.Tests/Fixtures) merged with the same shared registration.
    internal static IToolCatalogView EngineHostCatalog(FoundationCatalog host)
    {
        var frozen = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "EngineCatalog" + Release + ".json")))!.AsObject();
        var engine = new EngineCatalog(frozen, (string)frozen["workerSha256"]!, Release);
        return new SharedToolCatalog(engine, HostTools, host.Lite.Select(tool => tool.Name).ToHashSet(StringComparer.Ordinal));
    }
    private sealed class MetadataWorker : foundationhost::TiaMcp.FoundationHost.IFoundationWorker
    {
        public Task<JsonNode?> Call(string operation, JsonObject arguments, CancellationToken token)
            => throw new InvalidOperationException("Registration and pre-operation refusals must not dispatch a worker operation.");
        public void Dispose() { }
    }
    private sealed class NoWorker : IEngineWorker
    {
        public bool Faulted => false;
        public JsonNode? Binding => null;
        public object SessionKey { get; } = new();
        public JsonObject Snapshot() => new();
        public Task<JsonObject> Status(CancellationToken token) => Task.FromResult(Snapshot());
        public Task<JsonObject> Restart(bool confirmed, CancellationToken token) => throw new NotSupportedException();
        public Task<IDisposable> Acquire(CancellationToken token) => throw new NotSupportedException();
        public Task<EngineReply> Invoke(string id, string name, JsonObject arguments, bool preview, CancellationToken token) => throw new NotSupportedException();
        public void Dispose() { }
    }
    internal static McpServerTool CreateTool(MethodInfo method)
        => DeclaredToolMetadata.Create(method.GetCustomAttribute<McpServerToolAttribute>()!.Name!, method,
            _ => throw new InvalidOperationException("Invalid input must stop before service creation."), out _);
    internal static object? Boundary(string name, object?[] args)
    {
        if (name == "ToolInputSchema") return CreateTool((MethodInfo)args[1]!).ProtocolTool.InputSchema;
        // The production registration already carries the schema hints and V4 admission.
        if (name == "WithSchemaHints") return (McpServerTool)args[0]!;
        if (name == "DispatchNestedTool") return McpServer.CallTool((string)args[0]!, (ToolArguments)args[1]!);
        if (name == "ValidateV4Arguments")
        {
            var method = (MethodInfo)args[0]!;
            DeclaredToolMetadata.Create(method.GetCustomAttribute<McpServerToolAttribute>()!.Name!, method,
                _ => throw new InvalidOperationException("Metadata only."), out var descriptor);
            return McpServer.ToolInvoker.ValidateArguments(descriptor, (JsonElement)args[1]!, (JsonElement)args[2]!);
        }
        throw new NotSupportedException(name);
    }
    internal static string[] ParseNames(string json)
    {
        var decoder = Adapter.GetType("TiaMcp.Adapters.Native.Hmi.UnifiedRequestValues", true)!.GetField("Decoder", All)!;
        decoder.SetValue(null, (Func<string, TiaMcp.Adapters.Contracts.UnifiedValue>)(text =>
        {
            using var document = JsonDocument.Parse(text);
            return TiaMcpServer.Siemens.Services.UnifiedInputCodec.Decode(document.RootElement);
        }));
        return (string[])Adapter.GetType("TiaMcp.Adapters.Native.Hmi.UnifiedExchangeLogic", true)!
            .GetMethod("ParseExpectedNames", All)!.Invoke(null, [json, 500])!;
    }
    internal static void DeleteTag(JsonObject meta, Func<object?> find, Action<object> delete)
    {
        var values = (IDictionary<string, object?>)Activator.CreateInstance(Adapter.GetType("TiaMcp.Adapters.Native.Hmi.HmiObject", true)!, true)!;
        Adapter.GetType("TiaMcp.Adapters.Native.Hmi.HmiTagDeletion", true)!.GetMethod("Execute", All)!
            .Invoke(null, [values, false, find, delete]);
        foreach (var pair in values) meta[pair.Key] = JsonSerializer.SerializeToNode(pair.Value);
    }
}
