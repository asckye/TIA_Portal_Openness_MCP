using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace TiaMcp.LegacyHost;

internal static class EngineHost
{
    internal static async Task<int> Run(HostOptions options, HostFileLogger logger)
    {
        using var worker = new EngineWorkerClient(options, EngineCatalog.Hash(options.EngineWorkerExe!));
        IReadOnlyList<McpServerTool>? roster = null;
        var foundationNames = LegacyHostToolRegistry.Create(worker, options.ReleaseKey, false).Select(t => t.ProtocolTool.Name).ToHashSet(StringComparer.Ordinal);
        var shared = LegacyHostToolRegistry.Create(worker, options.ReleaseKey, options.NativeEnabled, options.ApiDirectory,
            options.ApiDirectorySource, worker.FoundationReadiness, () => roster!, name => foundationNames.Contains(name) ? "plc-foundation" : "full-engine");
        var essentials = LegacyHostToolRegistry.Create(worker, "19", false).Select(t => t.ProtocolTool.Name).ToHashSet(StringComparer.Ordinal);
        using var pipeline = new EngineHostPipeline(options.EngineCatalog!, options.EngineWorkerExe!, options.ReleaseKey, worker, options.Profile, shared, essentials);
        roster = pipeline.AllTools;
        worker.SessionLocked = () => TiaMcpServer.ModelContextProtocol.McpServer.SessionPrecheckRefusal("GetSessionState") != null;
        if (options.CatalogOnly)
        {
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { releaseKey = options.ReleaseKey, profile = "full-engine",
                behaviorCapabilities = pipeline.BehaviorCapabilities, callDiscipline = TiaMcpServer.ModelContextProtocol.McpServer.SchemaHintStatistics(),
                tools = pipeline.Tools.Select(t => t.ProtocolTool) }, global::ModelContextProtocol.McpJsonUtilities.DefaultOptions));
            return 0;
        }
        var status = await worker.Status(CancellationToken.None);
        if ((string?)status["releaseKey"] != options.ReleaseKey || !System.Text.Json.Nodes.JsonNode.DeepEquals(status["behaviorCapabilities"], pipeline.BehaviorCapabilities))
            throw new InvalidDataException("Engine worker release or behavior capabilities mismatch.");
        if (options.Transport == "http")
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = Array.Empty<string>() });
            builder.Logging.ClearProviders();
            builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
            builder.Logging.AddProvider(logger);
            builder.Logging.SetMinimumLevel(LogLevel.Warning);
            builder.WebHost.UseSetting("urls", options.HttpPrefix);
            var mcp = builder.Services.AddMcpServer(o => o.ServerInstructions = pipeline.Instructions).WithHttpTransport(o => {
                o.RunSessionHandler = async (_, server, token) => {
                    using var session = pipeline.RegisterHttpSession(server.SessionId ?? Guid.NewGuid().ToString("N"));
                    await server.RunAsync(token);
                };
            }).WithTools(pipeline.Tools);
            pipeline.RegisterHandlers(mcp);
            var app = builder.Build();
            app.Use(async (context, next) => {
                if (context.Request.Path == "/mcp/health") { await next(context); return; }
                if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(context.Request.Headers.Authorization.ToString()), Encoding.UTF8.GetBytes("Bearer " + options.ApiKey)))
                { context.Response.StatusCode = 401; return; }
                await next(context);
            });
            app.MapMcp("/mcp");
            app.MapGet("/mcp/health", () => new { status = "ok", releaseKey = options.ReleaseKey, profile = "full-engine" });
            await app.RunAsync();
        }
        else
        {
            var builder = Host.CreateEmptyApplicationBuilder(settings: null);
            builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
            builder.Logging.AddProvider(logger);
            builder.Logging.SetMinimumLevel(LogLevel.Warning);
            var mcp = builder.Services.AddMcpServer(o => o.ServerInstructions = pipeline.Instructions).WithStdioServerTransport().WithTools(pipeline.Tools);
            pipeline.RegisterHandlers(mcp);
            using var host = builder.Build();
            await host.RunAsync();
        }
        return 0;
    }

}
