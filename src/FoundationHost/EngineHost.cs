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
        var catalog = new EngineCatalog(options.EngineCatalog!, options.EngineWorkerExe!);
        if (options.CatalogOnly)
        {
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { releaseKey = "21", profile = "full-engine",
                tools = catalog.Tools.Where(t => EngineCatalog.Slice.Contains(t.Name)) }, global::ModelContextProtocol.McpJsonUtilities.DefaultOptions));
            return 0;
        }
        using var worker = new EngineWorkerClient(options, catalog.WorkerHash);
        var slice = new EngineSlice(catalog, worker);
        if (options.Transport == "http")
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = Array.Empty<string>() });
            builder.Logging.ClearProviders();
            builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
            builder.Logging.AddProvider(logger);
            builder.Logging.SetMinimumLevel(LogLevel.Warning);
            builder.WebHost.UseSetting("urls", options.HttpPrefix);
            var mcp = builder.Services.AddMcpServer(o => o.ServerInstructions = catalog.Instructions).WithHttpTransport().WithTools(slice.Tools(options.Profile));
            Resources(mcp);
            var app = builder.Build();
            app.Use(async (context, next) => {
                if (context.Request.Path == "/mcp/health") { await next(context); return; }
                if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(context.Request.Headers.Authorization.ToString()), Encoding.UTF8.GetBytes("Bearer " + options.ApiKey)))
                { context.Response.StatusCode = 401; return; }
                await next(context);
            });
            app.MapMcp("/mcp");
            app.MapGet("/mcp/health", () => new { status = "ok", releaseKey = "21", profile = "full-engine" });
            await app.RunAsync();
        }
        else
        {
            var builder = Host.CreateEmptyApplicationBuilder(settings: null);
            builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
            builder.Logging.AddProvider(logger);
            builder.Logging.SetMinimumLevel(LogLevel.Warning);
            var mcp = builder.Services.AddMcpServer(o => o.ServerInstructions = catalog.Instructions).WithStdioServerTransport().WithTools(slice.Tools(options.Profile));
            Resources(mcp);
            using var host = builder.Build();
            await host.RunAsync();
        }
        return 0;
    }

    private static void Resources(IMcpServerBuilder builder)
    {
        builder.WithListResourcesHandler((_, token) => { token.ThrowIfCancellationRequested(); return new ValueTask<ListResourcesResult>(new ListResourcesResult { Resources = Array.Empty<Resource>() }); });
        builder.WithListResourceTemplatesHandler((_, token) => { token.ThrowIfCancellationRequested(); return new ValueTask<ListResourceTemplatesResult>(new ListResourceTemplatesResult { ResourceTemplates = Array.Empty<ResourceTemplate>() }); });
    }
}
