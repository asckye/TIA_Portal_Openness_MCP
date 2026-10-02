using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TiaMcp.Versioning;
using TiaMcp.LegacyHost;

string Option(string key) { var index=Array.IndexOf(args,key); return index>=0 && index+1<args.Length ? args[index+1] : ""; }
var release=Option("--release-key");
TiaVersionCatalog.Get(release); // Exact identity; original V14/V15 excluded.
using var worker=new WorkerClient(release,Option("--worker-exe"),Option("--public-api"),args.Contains("--native-session"));
var builder=Host.CreateEmptyApplicationBuilder(settings:null);
builder.Logging.AddConsole(o=>o.LogToStandardErrorThreshold=LogLevel.Trace);
builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.Logging.AddFilter("ModelContextProtocol",LogLevel.Warning); // Never enable request/password trace logging here.
builder.Services.AddMcpServer(o=>o.ServerInstructions="PLC foundation source-preview host. Native acceptance is NOT complete. Only listed tools exist; HMI/download/online writes are unavailable. Legacy production catalog remains disabled. Native operations default to disabled; mutations default to dryRun=true.")
    .WithStdioServerTransport().WithTools(LegacyHostToolRegistry.Create(worker, release, args.Contains("--native-session")));
using var host=builder.Build();
await host.RunAsync();
