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
    internal static System.Text.Json.Nodes.JsonObject CatalogDocument(string release, IEnumerable<Tool> tools,
        System.Text.Json.Nodes.JsonArray behaviorCapabilities, System.Text.Json.Nodes.JsonObject callDiscipline)
        => new() {
            ["releaseKey"] = release, ["profile"] = ReleaseCatalogData.Releases[release].ProfileName,
            ["behaviorCapabilities"] = behaviorCapabilities.DeepClone(), ["callDiscipline"] = callDiscipline.DeepClone(),
            ["tools"] = System.Text.Json.JsonSerializer.SerializeToNode(tools, global::ModelContextProtocol.McpJsonUtilities.DefaultOptions)
        };

    internal static Task<int> Run(HostOptions options, HostFileLogger logger, CancellationToken stoppingToken = default)
        => RunCore(options, logger, null, stoppingToken);

    // No CLI/environment switch reaches this seam; only the friend test assembly calls it.
    internal static Task<int> RunForTest(HostOptions options, HostFileLogger logger, Func<IToolCatalogView, IToolCatalogView> catalogForTest,
            CancellationToken stoppingToken, Func<IFoundationSessionWorker, IReadOnlyList<McpServerTool>, IReadOnlyList<McpServerTool>>? toolsForTest = null) =>
        RunCore(options, logger, catalogForTest, stoppingToken, toolsForTest);

    private static async Task<int> RunCore(HostOptions options, HostFileLogger logger, Func<IToolCatalogView, IToolCatalogView>? catalogForTest,
            CancellationToken stoppingToken, Func<IFoundationSessionWorker, IReadOnlyList<McpServerTool>, IReadOnlyList<McpServerTool>>? toolsForTest = null)
    {
        if (options.Transport == "http" && !options.CatalogOnly && options.HostCommand == null)
        {
            // HTTP discovery metadata is release data; only an initialized MCP session needs a tool catalog and worker client.
            // Constructing and discarding a probe here put cold schema generation ahead of Kestrel on every launch.
            EngineHostConfiguration.ReleaseKey = options.ReleaseKey;
            await RunHttp(options, logger, catalogForTest, stoppingToken, toolsForTest);
            return 0;
        }
        LocalStabilityTelemetry.StartupStage("host.catalog.begin");
        using var session = CreateSession(options, catalogForTest, toolsForTest);
        LocalStabilityTelemetry.StartupStage("host.catalog.end");
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
            var catalog = CatalogDocument(options.ReleaseKey, pipeline.Tools.Select(t => t.ProtocolTool),
                pipeline.BehaviorCapabilities, McpServer.SchemaHintStatistics());
            Console.WriteLine(catalog.ToJsonString(global::ModelContextProtocol.McpJsonUtilities.DefaultOptions));
            return 0;
        }
        if (options.HostCommand != null)
        {
            using var context = pipeline.EnterSession();
            await HostToolCommand.Run(options.HostCommand, pipeline);
            return 0;
        }
        await using var operatorEndpoint = OperatorEndpoint
            .Start(options, async token => await CreateHostSessionCore(options, true, catalogForTest, token, toolsForTest));
        LocalStabilityTelemetry.StartupStage("host.operator.started");
        using var operatorStopping = stoppingToken.Register(() => operatorEndpoint?.Stop());
        if (options.Transport == "operator")
        {
            session.Dispose();
            await operatorEndpoint!.Completion;
            return 0;
        }
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
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, operatorEndpoint?.ShutdownToken ?? CancellationToken.None);
            // Registration is visible before transport startup. A control request
            // during startup must join normal shutdown after StartAsync completes.
            await host.StartAsync(stoppingToken);
            await host.WaitForShutdownAsync(lifetime.Token);
        }
        return 0;
    }

    // The installation that answers a not-ready environment. V20/V21 keep the engine's answer (P8-04j2): the engine located TIA
    // from --tia-portal-location, TiaPortalLocation or the registry, so an explicit --public-api names only the worker's assemblies.
    internal static (string? Directory, string? Source) ReadinessInstallation(HostOptions options)
    {
        if (!ReleaseCatalogData.Releases[options.ReleaseKey].MergeWorkerReadiness || !options.PublicApiOption)
            return (options.ApiDirectory, options.ApiDirectorySource);
        string? installation = string.IsNullOrWhiteSpace(options.TiaPortalLocation) ? null
            : TiaMcp.Versioning.TiaVersionCatalog.Get(options.ReleaseKey).FindApiDirectory(options.TiaPortalLocation);
        return (installation, installation == null ? null : "host-option");
    }

    internal static WorkerClient CreateWorker(HostOptions options) =>
        new WorkerClient(options.ReleaseKey, options.WorkerExe, options.ApiDirectory, options.NativeEnabled, options.EngineTimeoutSeconds, options
            .WorkerTimeoutConfig)
        { Bundled = options.BundledWorker, WithUi = options.WithUi, TiaPortalLocation = options.TiaPortalLocation,
            SharedSession = ReleaseCatalogData.Releases[options.ReleaseKey].SharedSession };

    private static Session CreateSession(HostOptions options, Func<IToolCatalogView, IToolCatalogView>? catalogForTest = null, Func<IFoundationSessionWorker,
            IReadOnlyList<McpServerTool>, IReadOnlyList<McpServerTool>>? toolsForTest = null)
    {
        EngineHostConfiguration.ReleaseKey = options.ReleaseKey;
        WorkerClient worker = CreateWorker(options);
        LocalStabilityTelemetry.StartupStage("session.worker.created");
        try
        {
            var data = ReleaseCatalogData.Releases[options.ReleaseKey];
            IReadOnlyList<McpServerTool>? roster = null;
            Func<System.Text.Json.Nodes.JsonObject> production = () => FoundationPassiveDiagnostics
                .Readiness(options.ReleaseKey, options.ApiDirectory, options.ApiDirectorySource);
            var (installation, installationSource) = ReadinessInstallation(options);
            Func<System.Text.Json.Nodes.JsonObject> notReady = () => FoundationPassiveDiagnostics
                .Readiness(options.ReleaseKey, installation, installationSource);
            Func<System.Text.Json.Nodes.JsonObject> readiness = () => worker.Readiness(production, notReady);
            var shared = LegacyHostToolRegistry.Create(worker, options.ReleaseKey, options.NativeEnabled, options.ApiDirectory,
                options.ApiDirectorySource, readiness, () => roster!, data.ExampleProfile, hostPorts: false);
            LocalStabilityTelemetry.StartupStage("session.registry.created");
            if (toolsForTest != null) shared = toolsForTest(worker, shared);
            var services = FoundationHostPorts.CreateServices(worker, options.ReleaseKey, readiness, options.ApiDirectory);
            LocalStabilityTelemetry.StartupStage("session.services.created");
            var pipeline = new EngineHostPipeline("", options.WorkerExe, options.ReleaseKey, services,
                options.Profile, shared, data.LiteNames.ToHashSet(StringComparer.Ordinal), catalogForTest);
            LocalStabilityTelemetry.StartupStage("session.pipeline.created");
            services.Roster = () => roster!;
            services.ResetExports = pipeline.ResetExports;
            roster = pipeline.AllTools;
            worker.SessionLocked = () => pipeline.SessionLocked;
            worker.StagingProvider = () => { using var scope = pipeline.EnterSession(); return McpServer.SharedStagingOwner; };
            return new Session(worker, services, pipeline);
        }
        catch { worker.Dispose(); throw; }
    }
    private static HostSession CreateHostSession(HostOptions options, Session session, bool operatorOwned = false)
    {
        var host = new HostSession(options.ReleaseKey, session.Pipeline.Instructions, operatorOwned, session.Pipeline.Tools.ToArray(),
            session.Pipeline.OperatorTools, () => session.Services.OperatorSnapshot(), session.Dispose);
        SharedSessionState.For(session.Worker).HumanCrossReferences = () => TiaMcp.Adapters.Contracts.NativeCrossReferenceConsent
            .Read(host.NativeCrossReferenceSessionFile, host.NativeCrossReferenceSessionSecret);
        SharedSessionState.For(session.Worker).HostSessionKey = () => host.SessionKey;
        session.Worker.NativeCrossReferenceSessionFile = host.NativeCrossReferenceSessionFile;
        session.Worker.NativeCrossReferenceSessionSecret = host.NativeCrossReferenceSessionSecret;
        if (operatorOwned || options.Transport == "stdio") host.RegisterSession(host.SessionKey, session.Pipeline.RegisterStagingSession);
        else host.RegisterSessionOnBind(session.Pipeline.RegisterHttpSession);
        return host;
    }
    internal static Task<HostSession> CreateHostSession(HostOptions options, bool operatorOwned, CancellationToken token = default)
        => CreateHostSessionCore(options, operatorOwned, null, token);

    internal static Task<HostSession> CreateHostSessionForTest(HostOptions options, bool operatorOwned, Func<IToolCatalogView, IToolCatalogView> catalogForTest,
            CancellationToken token, Func<IFoundationSessionWorker, IReadOnlyList<McpServerTool>, IReadOnlyList<McpServerTool>>? toolsForTest = null) =>
        CreateHostSessionCore(options, operatorOwned, catalogForTest, token, toolsForTest);

    private static Task<HostSession> CreateHostSessionCore(HostOptions options, bool operatorOwned, Func<IToolCatalogView, IToolCatalogView>? catalogForTest,
            CancellationToken token, Func<IFoundationSessionWorker, IReadOnlyList<McpServerTool>, IReadOnlyList<McpServerTool>>? toolsForTest = null)
    {
        token.ThrowIfCancellationRequested();
        var session = CreateSession(options, catalogForTest, toolsForTest);
        try { return Task.FromResult(CreateHostSession(options, session, operatorOwned)); }
        catch { session.Dispose(); throw; }
    }

    private sealed class Session(WorkerClient worker, FoundationHostPorts.Services services, EngineHostPipeline pipeline) : IDisposable
    {
        private int disposed;
        internal WorkerClient Worker => worker;
        internal FoundationHostPorts.Services Services => services;
        internal EngineHostPipeline Pipeline => pipeline;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            worker.SessionLocked = null;
            worker.Dispose(); pipeline.Dispose();
        }
    }

    private static async Task RunHttp(HostOptions options, HostFileLogger logger,
        Func<IToolCatalogView, IToolCatalogView>? catalogForTest, CancellationToken stoppingToken, Func<IFoundationSessionWorker, IReadOnlyList<McpServerTool>,
            IReadOnlyList<McpServerTool>>? toolsForTest = null)
    {
        await using var operatorEndpoint = OperatorEndpoint
            .Start(options, async token => await CreateHostSessionCore(options, true, catalogForTest, token, toolsForTest));
        using var operatorStopping = stoppingToken.Register(() => operatorEndpoint?.Stop());
        LocalStabilityTelemetry.StartupStage("host.operator.started");
        LocalStabilityTelemetry.StartupStage("http.builder.begin");
        var builder = WebApplication.CreateBuilder(HttpHostOptions.Create());
        LocalStabilityTelemetry.StartupStage("http.builder.end");
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
        builder.Logging.AddProvider(logger);
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.WebHost.UseSetting("urls", options.HttpPrefix);
        var mcp = builder.Services.AddMcpServer(o => o.ServerInstructions = ReleaseCatalogData.Releases[options.ReleaseKey].Instructions)
            .WithHttpTransport(o => {
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
        EngineHostPipeline.RegisterHandlers(mcp, options.ReleaseKey);
        var app = builder.Build();
        LocalStabilityTelemetry.StartupStage("http.app.built");
        var boundary = new HttpRequestBoundary(options);
        var authorization = Encoding.UTF8.GetBytes("Bearer " + options.ApiKey);
        app.Use(boundary.Invoke);
        app.Use(async (context, next) => {
            if (context.Request.Path == "/mcp/health") { await next(context); return; }
            if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(context.Request.Headers.Authorization.ToString()), authorization))
            { context.Response.StatusCode = 401; return; }
            await next(context);
        });
        app.MapMcp("/mcp");
        app
            .MapGet("/mcp/health", () => new {
                status = "ok", fileVersion = typeof(HostOptions).Assembly.GetCustomAttributes<System.Reflection.AssemblyMetadataAttribute>()
                    .Single(attribute => attribute.Key == "TiaMcpRelease").Value, releaseKey = options.ReleaseKey, profile = ReleaseCatalogData
                    .Releases[options.ReleaseKey].ProfileName
            });
        app.MapGet("/mcp/ready", () => new { mcpHostReady = true, releaseKey = options.ReleaseKey, nativeAcceptance = "NOT RUN" });
        using var stopping = app.Lifetime.ApplicationStopping.Register(WorkerShutdown.StopAll);
        using var started = app.Lifetime.ApplicationStarted.Register(() => LocalStabilityTelemetry.StartupStage("http.listening"));
        LocalStabilityTelemetry.StartupStage("http.run.begin");
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, operatorEndpoint?.ShutdownToken ?? CancellationToken.None);
        // Console's synchronized reader can block even from ReadLineAsync. It must
        // never hold up HTTP startup or shutdown; this reader ends with the process.
        try
        {
            await app.StartAsync(stoppingToken);
            if (Environment.GetEnvironmentVariable("TIA_MCP_WORKBENCH_STARTED") == "1" && Console.IsInputRedirected)
                _ = Task.Run(() => ReadOwnedControl(operatorEndpoint, lifetime.Token));
            await app.WaitForShutdownAsync(lifetime.Token);
        }
        finally { lifetime.Cancel(); await app.DisposeAsync(); }
    }

    private static async Task ReadOwnedControl(OperatorEndpoint? endpoint, CancellationToken token)
    {
        try
        {
            while (await Console.In.ReadLineAsync(token).ConfigureAwait(false) is { } line)
            {
                // The launcher owns this redirected input handle; EOF alone never stops an HTTP host.
                if (line == "tiamcp.host-control/1 shutdown")
                {
                    if (endpoint?.ShutdownFromOwnedInput() == true) return;
                    Console.Error.WriteLine("Host control: HOST_BUSY");
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) /* swallow(teardown): host shutdown cancels its owned control reader */ { }
        catch (IOException error) { OperatorEndpoint.Report(error); }
    }

}
