using System;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using TiaMcp.PlcWorker;
using TiaMcp.WorkerChannel;

internal static class Program
{
    private static int Main(string[] args)
    {
        var options = WorkerLaunchOptions.Parse(args);
        if (options.TiaPortalLocation != null) Environment.SetEnvironmentVariable("TiaPortalLocation", options.TiaPortalLocation);
        AppDomain.CurrentDomain.UnhandledException += (_, error) => Console.Error.WriteLine(
            "PlcWorker background exception; terminating=" + error.IsTerminating + ": " + (error.ExceptionObject as Exception)?.Message);
        string exe = typeof(Program).Assembly.Location;
        string Hash(string path)
        {
            using var sha = SHA256.Create();
            return BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(path))).Replace("-", "").ToLowerInvariant();
        }
        var identity = new ChannelIdentity(options.ReleaseKey, Hash(exe),
            Hash(Path.Combine(Path.GetDirectoryName(exe)!, "TiaMcp.Adapter." + options.ReleaseKey + ".dll")),
            System.Diagnostics.Process.GetCurrentProcess().Id, options.Nonce);
        bool lost = false, bound = false;
        long epoch = 0;
        string? faultMethod = null, faultTool = null, faultCorrelation = null;
        string Spill(string raw, string? mode)
        {
            if (mode == "oversized-delayed") Thread.Sleep(1200);
            return WorkerReplySpill.Write(Environment.GetEnvironmentVariable("TIA_MCP_WORKER_SPILLS_DIRECTORY")!, raw);
        }
        new ChannelServer(Console.OpenStandardInput(), Console.OpenStandardOutput(), identity, () => new ChannelBinding(epoch, bound), request => {
            if (request.Method == "adapter.HostObserve")
                return ChannelResponse.Success(JsonSerializer.Serialize(new { releaseKey = options.ReleaseKey,
                    diagnosticsDirectory = Environment.GetEnvironmentVariable("TIA_MCP_DIAGNOSTICS_DIRECTORY"),
                    readiness = new { ready = true }, binding = bound ? new { projectPath = "C:/fixture.ap" + options.ReleaseKey } : null,
                    session = new { isConnected = bound } }));
            if (lost) return ChannelResponse.Error(new ChannelFailureException("Explicit restart required.", -32603, ChannelOutcome.RejectedBeforeNative));
            if (request.Method == "adapter.FaultFixtureArm")
            {
                faultMethod = JsonNode.Parse(request.ArgumentsJson)!["method"]!.GetValue<string>();
                faultTool = request.ToolName;
                faultCorrelation = request.CorrelationId;
                if (string.IsNullOrWhiteSpace(faultMethod) || string.IsNullOrWhiteSpace(faultTool) || string.IsNullOrWhiteSpace(faultCorrelation))
                    throw new ArgumentException("Fault target requires a method, tool and correlation.");
                return ChannelResponse.Success("{\"armed\":true}");
            }
            // Readiness and prechecks may share the tool/correlation, but must not
            // consume the operation's fault. Other invocations of that tool are normal too.
            bool target = request.Method == faultMethod && request.ToolName == faultTool && request.CorrelationId == faultCorrelation;
            string? fixtureMode = WorkerFaultInjection.PlcMode(typeof(Program).Assembly);
            string? mode = target ? fixtureMode : null;
            var injected = mode == null ? null : WorkerFaultInjection.PlcBeforeDispatch(typeof(Program).Assembly, request, _ => lost = true);
            if (injected != null) return injected;
            if (request.Method == "adapter.Attach")
            {
                bound = true; epoch++;
                return ChannelResponse.Success("{\"Stage\":\"attached\",\"OwnsPortal\":false,\"AttemptedPids\":[123],\"Strategy\":\"explicit\",\"LaunchMode\":\"attach\"}");
            }
            if (request.Method == "adapter.Disconnect")
            {
                bound = false; epoch++;
                return ChannelResponse.Success("{\"Stage\":\"disconnected\",\"SessionState\":\"terminal\",\"Strategy\":\"non-owning-attachment-only\",\"WorkerAcknowledged\":true,\"Detached\":true,\"SavedProject\":false,\"ClosedProject\":false,\"LaunchMode\":\"never\"}");
            }
            if (mode == "slow" && request.Method != "adapter.ReadState") Thread.Sleep(150);
            string padding = mode is "oversized" or "oversized-delayed" ? new string('x', ChannelLimits.ResponseBytes) : "fixture";
            if (request.Method == "adapter.optional-products.ExecuteOptionalProduct")
            {
                string step = new JsonObject { ["Message"] = "fixture", ["RequiresSessionReset"] = false,
                    ["Meta"] = new JsonObject { ["tool"] = "ListTestSuiteCases", ["success"] = true,
                        ["fixture"] = padding, ["records"] = new JsonArray() } }.ToJsonString();
                return ChannelResponse.WithSpill(step, () => Spill(step, mode));
            }
            string raw = request.Method == "adapter.ReadBlocks" ? new JsonArray(new JsonObject { ["Name"] = padding }).ToJsonString()
                : JsonSerializer.Serialize(new { ReleaseKey = options.ReleaseKey, IsAttached = bound, ProcessId = bound ? (int?)123 : null,
                    // Fault scenarios have a synthetic project so normal ReadState
                    // prechecks can admit the operation that will receive the fault.
                    ProjectFile = bound || !string.IsNullOrEmpty(fixtureMode) ? "C:/fixture.ap" + options.ReleaseKey : null, WithUi = options.WithUi,
                    TiaPortalLocation = Environment.GetEnvironmentVariable("TiaPortalLocation") });
            return ChannelResponse.WithSpill(raw, () => Spill(raw, mode));
        }).Run();
        return 0;
    }
}
