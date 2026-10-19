using System.Security.Cryptography;
using System.Reflection;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcp.FoundationHost;

internal static class SharedReleaseHost
{
    internal static Task<int> Run(HostOptions options, HostFileLogger logger, CancellationToken stoppingToken = default)
        => RunCore(options, logger, null, stoppingToken);

    // No CLI/environment switch reaches this seam; only the friend test assembly calls it.
    internal static Task<int> RunForTest(HostOptions options, HostFileLogger logger, Func<IToolCatalogView, IToolCatalogView> catalogForTest, CancellationToken stoppingToken, Func<IFoundationSessionWorker, IReadOnlyList<McpServerTool>, IReadOnlyList<McpServerTool>>? toolsForTest = null) => RunCore(options, logger, catalogForTest, stoppingToken, toolsForTest);

    private static async Task<int> RunCore(HostOptions options, HostFileLogger logger, Func<IToolCatalogView, IToolCatalogView>? catalogForTest, CancellationToken stoppingToken, Func<IFoundationSessionWorker, IReadOnlyList<McpServerTool>, IReadOnlyList<McpServerTool>>? toolsForTest = null)
    {
        using var session = CreateSession(options, catalogForTest, toolsForTest);
        var pipeline = session.Pipeline;
        if (options.CatalogOnly)
        {
            using var engineContext = pipeline.EnterSession();
            var data = ReleaseCatalogData.Releases[options.ReleaseKey];
            if (options.CatalogToolsList != null && data.HasManifestCatalog)
            {
                HostToolsListWriter.Write(options, pipeline);
                return 0;
            }
            var catalog = new System.Text.Json.Nodes.JsonObject { ["releaseKey"] = options.ReleaseKey, ["profile"] = data.ProfileName };
            if (!data.CatalogCallDiscipline) catalog["nativeAcceptance"] = "NOT RUN";
            catalog["behaviorCapabilities"] = pipeline.BehaviorCapabilities.DeepClone();
            if (data.CatalogCallDiscipline) catalog["callDiscipline"] = System.Text.Json.JsonSerializer.SerializeToNode(McpServer.SchemaHintStatistics());
            catalog["tools"] = System.Text.Json.JsonSerializer.SerializeToNode(pipeline.Tools.Select(t => t.ProtocolTool), global::ModelContextProtocol.McpJsonUtilities.DefaultOptions);
            Console.WriteLine(catalog.ToJsonString(global::ModelContextProtocol.McpJsonUtilities.DefaultOptions));
            return 0;
        }
        if (options.HostCommand != null)
        {
            using var context = pipeline.EnterSession();
            await HostToolCommand.Run(options.HostCommand, pipeline);
            return 0;
        }
        await using var operatorEndpoint = OperatorEndpoint.Start(options, async token => await CreateHostSessionCore(options, true, catalogForTest, token, toolsForTest));
        using var operatorStopping = stoppingToken.Register(() => operatorEndpoint?.Stop());
        if (options.Transport == "operator")
        {
            session.Dispose();
            await operatorEndpoint!.Completion;
            return 0;
        }
        if (options.Transport == "http")
        {
            // Build the host catalog before listening; each MCP session starts its worker on demand.
            session.Dispose();
            await RunHttp(options, logger, pipeline, operatorEndpoint, catalogForTest, stoppingToken, toolsForTest);
        }
        else
        {
            using var engineContext = pipeline.EnterSession();
            using var hostSession = CreateHostSession(options, session);
            using var registration = operatorEndpoint?.Register(hostSession);
            var builder = Host.CreateEmptyApplicationBuilder(settings: null);
            builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
            builder.Logging.AddProvider(logger);
            builder.Logging.SetMinimumLevel(LogLevel.Warning);
            var mcp = builder.Services.AddMcpServer(o => o.ServerInstructions = pipeline.Instructions).WithStdioServerTransport().WithTools(hostSession.Tools);
            pipeline.RegisterHandlers(mcp);
            using var host = builder.Build();
            using var stopping = host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.Register(WorkerShutdown.StopAll);
            await host.RunAsync(stoppingToken);
        }
        return 0;
    }

    private static Session CreateSession(HostOptions options, Func<IToolCatalogView, IToolCatalogView>? catalogForTest = null, Func<IFoundationSessionWorker, IReadOnlyList<McpServerTool>, IReadOnlyList<McpServerTool>>? toolsForTest = null)
    {
        EngineHostConfiguration.ReleaseKey = options.ReleaseKey;
        IFoundationSessionWorker worker = options.EngineWorkerExe != null
            ? new EngineWorkerClient(options, File.Exists(options.EngineWorkerExe) ? EngineCatalog.Hash(options.EngineWorkerExe) : "")
            : new WorkerClient(options.ReleaseKey, options.WorkerExe, options.ApiDirectory, options.NativeEnabled, options.EngineTimeoutSeconds, options.WorkerTimeoutConfig)
                { Bundled = options.BundledWorker, WithUi = options.WithUi, TiaPortalLocation = options.TiaPortalLocation,
                    SharedSession = ReleaseCatalogData.Releases[options.ReleaseKey].SharedSession };
        try
        {
            var data = ReleaseCatalogData.Releases[options.ReleaseKey];
            IReadOnlyList<McpServerTool>? roster = null;
            Func<System.Text.Json.Nodes.JsonObject> readiness = () => worker is WorkerClient plc
                ? plc.Readiness(() => FoundationPassiveDiagnostics.Readiness(options.ReleaseKey, options.ApiDirectory, options.ApiDirectorySource))
                : ((EngineWorkerClient)worker).FoundationReadiness();
            var shared = LegacyHostToolRegistry.Create(worker, options.ReleaseKey, options.NativeEnabled, options.ApiDirectory,
                options.ApiDirectorySource, readiness, () => roster!, data.ExampleProfile, hostPorts: false);
            if (toolsForTest != null) shared = toolsForTest(worker, shared);
            var services = FoundationHostPorts.CreateServices(worker, options.ReleaseKey, readiness, options.ApiDirectory);
            var pipeline = new EngineHostPipeline("", options.EngineWorkerExe ?? options.WorkerExe, options.ReleaseKey, services,
                data.HasLite ? options.Profile : "full", shared, data.LiteNames.ToHashSet(StringComparer.Ordinal), catalogForTest);
            services.Roster = () => roster!;
            services.ResetExports = pipeline.ResetExports;
            roster = pipeline.AllTools;
            if (worker is WorkerClient client)
            {
                client.SessionLocked = () => pipeline.SessionLocked;
                client.StagingProvider = () => { using var scope = pipeline.EnterSession(); return McpServer.SharedStagingOwner; };
            }
            else ((EngineWorkerClient)worker).SessionLocked = () => pipeline.SessionLocked;
            return new Session(worker, services, pipeline);
        }
        catch { worker.Dispose(); throw; }
    }
    private static HostSession CreateHostSession(HostOptions options, Session session, bool operatorOwned = false)
    {
        var host = new HostSession(options.ReleaseKey, session.Pipeline.Instructions, operatorOwned, session.Pipeline.Tools.ToArray(),
            session.Pipeline.OperatorTools, () => session.Services.OperatorSnapshot(), session.Dispose);
        if (operatorOwned || options.Transport == "stdio") host.RegisterSession(host.SessionKey, session.Pipeline.RegisterStagingSession);
        else host.RegisterSessionOnBind(session.Pipeline.RegisterHttpSession);
        return host;
    }
    internal static Task<HostSession> CreateHostSession(HostOptions options, bool operatorOwned, CancellationToken token = default)
        => CreateHostSessionCore(options, operatorOwned, null, token);

    internal static Task<HostSession> CreateHostSessionForTest(HostOptions options, bool operatorOwned, Func<IToolCatalogView, IToolCatalogView> catalogForTest, CancellationToken token, Func<IFoundationSessionWorker, IReadOnlyList<McpServerTool>, IReadOnlyList<McpServerTool>>? toolsForTest = null) => CreateHostSessionCore(options, operatorOwned, catalogForTest, token, toolsForTest);

    private static Task<HostSession> CreateHostSessionCore(HostOptions options, bool operatorOwned, Func<IToolCatalogView, IToolCatalogView>? catalogForTest, CancellationToken token, Func<IFoundationSessionWorker, IReadOnlyList<McpServerTool>, IReadOnlyList<McpServerTool>>? toolsForTest = null)
    {
        token.ThrowIfCancellationRequested();
        var session = CreateSession(options, catalogForTest, toolsForTest);
        try { return Task.FromResult(CreateHostSession(options, session, operatorOwned)); }
        catch { session.Dispose(); throw; }
    }

    private sealed class Session(IFoundationSessionWorker worker, FoundationHostPorts.Services services, EngineHostPipeline pipeline) : IDisposable
    {
        private int disposed;
        internal FoundationHostPorts.Services Services => services;
        internal EngineHostPipeline Pipeline => pipeline;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            if (worker is WorkerClient plc) plc.SessionLocked = null;
            else ((EngineWorkerClient)worker).SessionLocked = null;
            worker.Dispose(); pipeline.Dispose();
        }
    }

    private static async Task RunHttp(HostOptions options, HostFileLogger logger, EngineHostPipeline probe, OperatorEndpoint? operatorEndpoint,
        Func<IToolCatalogView, IToolCatalogView>? catalogForTest, CancellationToken stoppingToken, Func<IFoundationSessionWorker, IReadOnlyList<McpServerTool>, IReadOnlyList<McpServerTool>>? toolsForTest = null)
    {
        var builder = WebApplication.CreateBuilder(HttpHostOptions.Create());
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
        builder.Logging.AddProvider(logger);
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.WebHost.UseSetting("urls", options.HttpPrefix);
        var mcp = builder.Services.AddMcpServer(o => o.ServerInstructions = probe.Instructions).WithHttpTransport(o => {
            o.ConfigureSessionOptions = (context, server, token) => {
                var session = CreateSession(options, catalogForTest, toolsForTest);
                var hostSession = CreateHostSession(options, session);
                try
                {
                    using var scope = session.Pipeline.EnterSession();
                    var tools = new McpServerPrimitiveCollection<McpServerTool>();
                    foreach (var tool in hostSession.Tools) tools.Add(tool);
                    // Keep the resource and prompt capabilities registered on the server builder; only the tools are per session.
                    server.Capabilities ??= new ServerCapabilities();
                    server.Capabilities.Tools = new ToolsCapability { ToolCollection = tools };
                    context.Items["engineSession"] = session;
                    context.Items["hostSession"] = hostSession;
                    return Task.CompletedTask;
                }
                catch { hostSession.Dispose(); throw; }
            };
            o.RunSessionHandler = async (context, server, token) => {
                var session = (Session)context.Items["engineSession"]!;
                using var hostSession = (HostSession)context.Items["hostSession"]!;
                using var scope = session.Pipeline.EnterSession();
                hostSession.BindAi(server);
                using var registration = operatorEndpoint?.Register(hostSession);
                await server.RunAsync(token);
            };
        });
        probe.RegisterHandlers(mcp);
        var app = builder.Build();
        app.Use(async (context, next) => {
            if (context.Request.Path == "/mcp/health") { await next(context); return; }
            if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(context.Request.Headers.Authorization.ToString()), Encoding.UTF8.GetBytes("Bearer " + options.ApiKey)))
            { context.Response.StatusCode = 401; return; }
            await next(context);
        });
        app.MapMcp("/mcp");
        app.MapGet("/mcp/health", () => new { status = "ok", fileVersion = typeof(HostOptions).Assembly.GetCustomAttributes<System.Reflection.AssemblyMetadataAttribute>().Single(attribute => attribute.Key == "TiaMcpRelease").Value, releaseKey = options.ReleaseKey, profile = ReleaseCatalogData.Releases[options.ReleaseKey].ProfileName });
        app.MapGet("/mcp/ready", () => new { mcpHostReady = true, releaseKey = options.ReleaseKey, nativeAcceptance = "NOT RUN" });
        using var stopping = app.Lifetime.ApplicationStopping.Register(WorkerShutdown.StopAll);
        await ((IHost)app).RunAsync(stoppingToken);
    }

}

// Compatibility entry for existing harnesses. Every transport and release uses SharedReleaseHost.
internal static class EngineReleaseHost
{
    internal static Task<int> Run(HostOptions options, HostFileLogger logger, CancellationToken token = default) => SharedReleaseHost.Run(options, logger, token);
    internal static Task<int> RunForTest(HostOptions options, HostFileLogger logger, Func<IToolCatalogView, IToolCatalogView> catalog, CancellationToken token, Func<IFoundationSessionWorker, IReadOnlyList<McpServerTool>, IReadOnlyList<McpServerTool>>? toolsForTest = null) => SharedReleaseHost.RunForTest(options, logger, catalog, token, toolsForTest);
    internal static Task<HostSession> CreateHostSession(HostOptions options, bool operatorOwned, CancellationToken token = default) => SharedReleaseHost.CreateHostSession(options, operatorOwned, token);
    internal static Task<HostSession> CreateHostSessionForTest(HostOptions options, bool operatorOwned, Func<IToolCatalogView, IToolCatalogView> catalog, CancellationToken token, Func<IFoundationSessionWorker, IReadOnlyList<McpServerTool>, IReadOnlyList<McpServerTool>>? toolsForTest = null) => SharedReleaseHost.CreateHostSessionForTest(options, operatorOwned, catalog, token, toolsForTest);
}
