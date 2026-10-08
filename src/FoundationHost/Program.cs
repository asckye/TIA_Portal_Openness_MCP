using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Security.Cryptography;
using System.Text;
using TiaMcp.LegacyHost;
using HostOptions = TiaMcp.LegacyHost.HostOptions;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

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

if (options.EngineWorkerExe != null) return await EngineHost.Run(options, fileLogger);

if (options.CatalogOnly)
{
    using var catalogWorker = new WorkerClient(options.ReleaseKey, options.WorkerExe, options.ApiDirectory, false) { Bundled = options.BundledWorker };
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { releaseKey = options.ReleaseKey, profile = "plc-foundation", nativeAcceptance = "NOT RUN",
        behaviorCapabilities = TiaMcp.Logic.V4.BehaviorCapabilities.Table(typeof(FoundationV4Tool).Assembly, options.ReleaseKey),
        tools = LegacyHostToolRegistry.Create(catalogWorker, options.ReleaseKey, false, options.ApiDirectory, options.ApiDirectorySource).Select(t => t.ProtocolTool) }));
    return 0;
}

const string instructions = TiaOpenness.Shared.ToolUsageCatalog.Instructions + " This release uses the PLC foundation catalog. InitializeEnvironment reports environment/session state; ListPortalProcessProjects returns current process/project identities. Only listed contracts are available.";
if (options.Transport == "http")
{
    var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = Array.Empty<string>() });
    builder.Logging.ClearProviders();
    builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
    builder.Logging.AddProvider(fileLogger);
    builder.Logging.SetMinimumLevel(LogLevel.Warning);
    builder.WebHost.UseSetting("urls", options.HttpPrefix);
    builder.Services.AddMcpServer(o => o.ServerInstructions = instructions)
        .WithHttpTransport(o => {
            o.ConfigureSessionOptions = (context, server, cancellationToken) => {
                var worker = new WorkerClient(options.ReleaseKey, options.WorkerExe, options.ApiDirectory, options.NativeEnabled) { Bundled = options.BundledWorker };
                context.Items["foundationWorker"] = worker;
                var toolCollection = new McpServerPrimitiveCollection<McpServerTool>();
                foreach (var tool in LegacyHostToolRegistry.Create(worker, options.ReleaseKey, options.NativeEnabled, options.ApiDirectory, options.ApiDirectorySource)) toolCollection.Add(tool);
                server.Capabilities = new ServerCapabilities { Tools = new ToolsCapability {
                    ToolCollection = toolCollection
                } };
                return Task.CompletedTask;
            };
            o.RunSessionHandler = async (context, server, cancellationToken) => {
                using var worker = (WorkerClient)context.Items["foundationWorker"]!;
                using var stagingSession = FoundationTool.RegisterStagingSession(worker, server.SessionId ?? Guid.NewGuid().ToString("N"));
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
    await app.RunAsync();
}
else
{
    using var worker = new WorkerClient(options.ReleaseKey, options.WorkerExe, options.ApiDirectory, options.NativeEnabled) { Bundled = options.BundledWorker };
    using var stagingSession = FoundationTool.RegisterStagingSession(worker, Guid.NewGuid().ToString("N"));
    var builder = Host.CreateEmptyApplicationBuilder(settings: null);
    builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
    builder.Logging.AddProvider(fileLogger);
    builder.Logging.SetMinimumLevel(LogLevel.Warning);
    builder.Services.AddMcpServer(o => o.ServerInstructions = instructions)
        .WithStdioServerTransport().WithTools(LegacyHostToolRegistry.Create(worker, options.ReleaseKey, options.NativeEnabled, options.ApiDirectory, options.ApiDirectorySource));
    using var host = builder.Build();
    await host.RunAsync();
}
return 0;
