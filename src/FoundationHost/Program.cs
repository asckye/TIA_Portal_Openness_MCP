using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Security.Cryptography;
using System.Text;
using TiaMcp.FoundationHost;
using HostOptions = TiaMcp.FoundationHost.HostOptions;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

if (args is ["--help"]) { Console.WriteLine(HostOptions.WorkerTimeoutHelp); return 0; }

using var stabilityTelemetry = LocalStabilityTelemetry.Start();
HostOptions options;
Console.OutputEncoding = new UTF8Encoding(false);
Console.SetError(new StreamWriter(Console.OpenStandardError(), new UTF8Encoding(false)) { AutoFlush = true });
try
{
    var root = TiaOpenness.Shared.BundleLayout.ExtractRootOption(args, out args);
    TiaOpenness.Shared.BundleLayout.Initialize(AppContext.BaseDirectory, root);
}
catch (TiaOpenness.Shared.BundleResourceUnavailableException ex) { Console.Error.WriteLine(ex.Message); return 70; }
catch (ArgumentException ex) { Console.Error.WriteLine("RESOURCE_UNAVAILABLE: " + ex.Message); return 64; }
try { options = HostOptions.Parse(args, AppContext.BaseDirectory); }
catch (ArgumentException ex) { Console.Error.WriteLine(ex.Message); return 2; }
TiaOpenness.Shared.DataLocations.InitializeHost(options.ReleaseKey);
TiaOpenness.Shared.DataLocations.PruneLogsAtStartup();
var fileLogger = new HostFileLogger(options.ReleaseKey);
fileLogger.Write("TiaMcpServer.log", DateTimeOffset.UtcNow.ToString("O") + " release=" + options.ReleaseKey);

if (options.EngineWorkerExe != null) return await EngineReleaseHost.Run(options, fileLogger);

if (options.CatalogOnly)
{
    using var catalogWorker = new WorkerClient(options.ReleaseKey, options.WorkerExe, options.ApiDirectory, false, options.EngineTimeoutSeconds, options.WorkerTimeoutConfig) { Bundled = options.BundledWorker };
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { releaseKey = options.ReleaseKey, profile = "plc-foundation", nativeAcceptance = "NOT RUN",
        behaviorCapabilities = TiaMcp.Logic.V4.BehaviorCapabilities.Table(typeof(FoundationV4Tool).Assembly, options.ReleaseKey),
        tools = LegacyHostToolRegistry.Create(catalogWorker, options.ReleaseKey, false, options.ApiDirectory, options.ApiDirectorySource).Select(t => t.ProtocolTool) }));
    return 0;
}

const string instructions = TiaOpenness.Shared.ToolUsageCatalog.Instructions + " This release uses the PLC foundation catalog. InitializeEnvironment reports environment/session state; ListPortalProcessProjects returns current process/project identities. Only listed contracts are available.";
await using var operatorEndpoint = OperatorEndpoint.Start(options, token => { token.ThrowIfCancellationRequested(); return Task.FromResult<IHostSession>(HostSession.Legacy(options, instructions, true)); });
if (options.Transport == "operator")
{
    await operatorEndpoint!.Completion;
    return 0;
}
if (options.Transport == "http")
{
    var builder = WebApplication.CreateBuilder(TiaMcp.FoundationHost.HttpHostOptions.Create());
    builder.Logging.ClearProviders();
    builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
    builder.Logging.AddProvider(fileLogger);
    builder.Logging.SetMinimumLevel(LogLevel.Warning);
    builder.WebHost.UseSetting("urls", options.HttpPrefix);
    builder.Services.AddMcpServer(o => o.ServerInstructions = instructions)
        .WithHttpTransport(o => {
            o.ConfigureSessionOptions = (context, server, cancellationToken) => {
                var session = HostSession.Legacy(options, instructions);
                context.Items["foundationSession"] = session;
                var toolCollection = new McpServerPrimitiveCollection<McpServerTool>();
                foreach (var tool in session.Tools) toolCollection.Add(tool);
                server.Capabilities = new ServerCapabilities { Tools = new ToolsCapability {
                    ToolCollection = toolCollection
                } };
                return Task.CompletedTask;
            };
            o.RunSessionHandler = async (context, server, cancellationToken) => {
                using var session = (HostSession)context.Items["foundationSession"]!;
                session.BindAi(server);
                using var registration = operatorEndpoint?.Register(session);
                await server.RunAsync(cancellationToken);
            };
        });
    var app = builder.Build();
    app.Use(async (context, next) => {
        if (context.Request.Path == "/mcp/health") { await next(context); return; }
        var supplied = context.Request.Headers.Authorization.ToString();
        var expected = "Bearer " + options.ApiKey;
        if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(supplied), Encoding.UTF8.GetBytes(expected)))
        { context.Response.StatusCode = 401; return; }
        await next(context);
    });
    app.MapMcp("/mcp");
    app.MapGet("/mcp/health", () => new { status = "ok", fileVersion = typeof(HostOptions).Assembly.GetCustomAttributes<System.Reflection.AssemblyMetadataAttribute>().Single(attribute => attribute.Key == "TiaMcpRelease").Value, releaseKey = options.ReleaseKey, profile = "plc-foundation" });
    app.MapGet("/mcp/ready", () => new { mcpHostReady = true, releaseKey = options.ReleaseKey, nativeAcceptance = "NOT RUN" });
    using var stopping = app.Lifetime.ApplicationStopping.Register(WorkerShutdown.StopAll);
    await app.RunAsync();
}
else
{
    using var session = HostSession.Legacy(options, instructions);
    using var registration = operatorEndpoint?.Register(session);
    var builder = Host.CreateEmptyApplicationBuilder(settings: null);
    builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
    builder.Logging.AddProvider(fileLogger);
    builder.Logging.SetMinimumLevel(LogLevel.Warning);
    builder.Services.AddMcpServer(o => o.ServerInstructions = instructions)
        .WithStdioServerTransport().WithTools(session.Tools);
    using var host = builder.Build();
    using var stopping = host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.Register(WorkerShutdown.StopAll);
    await host.RunAsync();
}
return 0;
