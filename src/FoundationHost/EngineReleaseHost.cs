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

namespace TiaMcp.FoundationHost;

internal static class EngineReleaseHost
{
    internal static async Task<int> Run(HostOptions options, HostFileLogger logger, CancellationToken stoppingToken = default)
    {
        using var session = CreateSession(options);
        var worker = session.Worker;
        var pipeline = session.Pipeline;
        if (options.CatalogOnly)
        {
            using var engineContext = pipeline.EnterSession();
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { releaseKey = options.ReleaseKey, profile = "full-engine",
                behaviorCapabilities = pipeline.BehaviorCapabilities, callDiscipline = TiaMcpServer.ModelContextProtocol.McpServer.SchemaHintStatistics(),
                tools = pipeline.Tools.Select(t => t.ProtocolTool) }, global::ModelContextProtocol.McpJsonUtilities.DefaultOptions));
            return 0;
        }
        await worker.Status(CancellationToken.None);
        if (options.HostCommand != null)
        {
            using var context = pipeline.EnterSession();
            await HostToolCommand.Run(options.HostCommand, pipeline);
            return 0;
        }
        if (options.Transport == "http")
        {
            // Verify the release and capabilities before listening; sessions never use this probe.
            session.Dispose();
            await RunHttp(options, logger, pipeline, stoppingToken);
        }
        else
        {
            using var engineContext = pipeline.EnterSession();
            var builder = Host.CreateEmptyApplicationBuilder(settings: null);
            builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
            builder.Logging.AddProvider(logger);
            builder.Logging.SetMinimumLevel(LogLevel.Warning);
            var mcp = builder.Services.AddMcpServer(o => o.ServerInstructions = pipeline.Instructions).WithStdioServerTransport().WithTools(pipeline.Tools);
            pipeline.RegisterHandlers(mcp);
            using var host = builder.Build();
            using var stopping = host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.Register(WorkerShutdown.StopAll);
            await host.RunAsync(stoppingToken);
        }
        return 0;
    }

    private static Session CreateSession(HostOptions options)
    {
        var worker = new EngineWorkerClient(options, EngineCatalog.Hash(options.EngineWorkerExe!));
        try { return new Session(worker, CreatePipeline(options, worker)); }
        catch { worker.Dispose(); throw; }
    }

    private static EngineHostPipeline CreatePipeline(HostOptions options, EngineWorkerClient worker)
    {
        IReadOnlyList<McpServerTool>? roster = null;
        var foundationNames = LegacyHostToolRegistry.Create(worker, options.ReleaseKey, false).Select(t => t.ProtocolTool.Name).Where(n => !TiaMcp.Adapters.Contracts.PortedFamilies.Contains(n)).ToHashSet(StringComparer.Ordinal);
        var shared = LegacyHostToolRegistry.Create(worker, options.ReleaseKey, options.NativeEnabled, options.ApiDirectory,
            options.ApiDirectorySource, worker.FoundationReadiness, () => roster!, name => foundationNames.Contains(name) ? "plc-foundation" : "full-engine");
        var essentials = LegacyHostToolRegistry.Create(worker, "19", false).Select(t => t.ProtocolTool.Name).Where(n => !TiaMcp.Adapters.Contracts.PortedFamilies.Contains(n)).ToHashSet(StringComparer.Ordinal);
        var pipeline = new EngineHostPipeline(options.EngineCatalog!, options.EngineWorkerExe!, options.ReleaseKey, worker, options.Profile, shared, essentials);
        roster = pipeline.AllTools;
        worker.SessionLocked = () => pipeline.SessionLocked;
        return pipeline;
    }

    private sealed class Session(EngineWorkerClient worker, EngineHostPipeline pipeline) : IDisposable
    {
        internal EngineWorkerClient Worker => worker;
        internal EngineHostPipeline Pipeline => pipeline;
        public void Dispose() { worker.SessionLocked = null; worker.Dispose(); pipeline.Dispose(); }
    }

    private static async Task RunHttp(HostOptions options, HostFileLogger logger, EngineHostPipeline probe, CancellationToken stoppingToken)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = Array.Empty<string>() });
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
        builder.Logging.AddProvider(logger);
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.WebHost.UseSetting("urls", options.HttpPrefix);
        var mcp = builder.Services.AddMcpServer(o => o.ServerInstructions = probe.Instructions).WithHttpTransport(o => {
            o.ConfigureSessionOptions = async (context, server, token) => {
                var session = CreateSession(options);
                try
                {
                    using var scope = session.Pipeline.EnterSession();
                    await session.Worker.Status(token);
                    var tools = new McpServerPrimitiveCollection<McpServerTool>();
                    foreach (var tool in session.Pipeline.Tools) tools.Add(tool);
                    // Keep the resource and prompt capabilities registered on the server builder; only the tools are per session.
                    server.Capabilities ??= new ServerCapabilities();
                    server.Capabilities.Tools = new ToolsCapability { ToolCollection = tools };
                    context.Items["engineSession"] = session;
                }
                catch { session.Dispose(); throw; }
            };
            o.RunSessionHandler = async (context, server, token) => {
                using var session = (Session)context.Items["engineSession"]!;
                using var staging = session.Pipeline.RegisterHttpSession(server.SessionId ?? throw new InvalidOperationException("HTTP MCP session id is unavailable."));
                using var scope = session.Pipeline.EnterSession();
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
        app.MapGet("/mcp/health", () => new { status = "ok", fileVersion = typeof(HostOptions).Assembly.GetCustomAttributes<System.Reflection.AssemblyMetadataAttribute>().Single(attribute => attribute.Key == "TiaMcpRelease").Value, releaseKey = options.ReleaseKey, profile = "full-engine" });
        app.MapGet("/mcp/ready", () => new { mcpHostReady = true, releaseKey = options.ReleaseKey, nativeAcceptance = "NOT RUN" });
        using var stopping = app.Lifetime.ApplicationStopping.Register(WorkerShutdown.StopAll);
        await ((IHost)app).RunAsync(stoppingToken);
    }

}
