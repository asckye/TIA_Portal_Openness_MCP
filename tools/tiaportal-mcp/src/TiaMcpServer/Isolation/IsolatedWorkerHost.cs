using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcpServer.ModelContextProtocol;
using TiaMcpServer.Siemens;

namespace TiaMcpServer.Isolation
{
    internal static class IsolatedWorkerHost
    {
        internal static OpennessWorkerSupervisor? Current { get; private set; }
        internal static bool IsChild { get; private set; }
        internal static string? NativeFault { get; private set; }
        private static Timer? parentMonitor;

        internal static void Configure(CliOptions options, Func<ProcessStartInfo>? startOverride = null)
        {
            if (Current != null) throw new InvalidOperationException("Worker host already configured.");
            var roster = McpServer.IsLiteProfile() ? McpServer.GetLiteTools() : McpServer.GetAllTools();
            string exe = Assembly.GetExecutingAssembly().Location;
            Current = new OpennessWorkerSupervisor(startOverride ?? (() => StartInfo(exe, options)),
                EngineRouter.CompiledTiaMajorVersion, Hash(exe), roster.Select(t => t.ProtocolTool.Name), TimeSpan.FromSeconds(options.WorkerTimeoutSeconds));
            AppDomain.CurrentDomain.ProcessExit += (_, __) => Current?.Dispose();
        }

        internal static void Stop() { Current?.Dispose(); Current = null; }
        internal static string Hash(string path)
        {
            using var stream = File.OpenRead(path); using var sha = SHA256.Create();
            return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }

        internal static string Quote(string value)
        {
            return TiaOpenness.Shared.ProcessArguments.Quote(value);
        }

        private static ProcessStartInfo StartInfo(string exe, CliOptions options)
        {
            using var parent = Process.GetCurrentProcess();
            var args = new List<string> { "--openness-worker-child", "--worker-parent-pid", parent.Id.ToString(),
                "--worker-parent-start", parent.StartTime.ToUniversalTime().Ticks.ToString(), "--tia-major-version", EngineRouter.CompiledTiaMajorVersion.ToString(),
                "--transport", "stdio", "--profile", McpServer.ResolvedProfile(), "--logging", "0" };
            if (!string.IsNullOrWhiteSpace(options.TiaPortalLocation)) args.AddRange(new[] { "--tia-portal-location", options.TiaPortalLocation! });
            if (options.PortalWithUserInterface) args.Add("--with-ui");
            // Preserve the host's interpretation of relative PublicAPI and tool file paths.
            return new ProcessStartInfo(exe, string.Join(" ", args.Select(Quote))) { WorkingDirectory = Environment.CurrentDirectory };
        }

        internal static void BeginChild(CliOptions options)
        {
            IsChild = true;
            PortalFailureClassifier.ProcessLostObserved = reason => NativeFault = reason;
            if (options.WorkerParentPid <= 0 || options.WorkerParentStart <= 0) throw new ArgumentException("A worker requires its owning host identity.");
            void CheckParent(object? ignored)
            {
                try
                {
                    using var parent = Process.GetProcessById(options.WorkerParentPid);
                    if (!parent.HasExited && parent.StartTime.ToUniversalTime().Ticks == options.WorkerParentStart) return;
                }
                catch { }
                // End this client only. Do not Close/Save/Dispose a TIA project after an unknown interruption.
                Environment.Exit(74);
            }
            CheckParent(null);
            parentMonitor = new Timer(CheckParent, null, 1000, 1000);
            WriteHello();
        }

        internal static void WriteHello()
        {
            Console.WriteLine(new JsonObject { ["kind"] = "tia-openness-worker", ["protocol"] = 1,
                ["engineMajor"] = EngineRouter.CompiledTiaMajorVersion, ["engineSha256"] = Hash(Assembly.GetExecutingAssembly().Location),
                ["pid"] = Process.GetCurrentProcess().Id }.ToJsonString());
            Console.Out.Flush();
        }

        internal static bool IsControl(string? name) => string.Equals(name, "ReadOpennessWorkerStatus", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "RestartOpennessWorker", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "ReadNativeInvocationLog", StringComparison.OrdinalIgnoreCase);

        internal static IList<McpServerTool> Wrap(IList<McpServerTool> tools)
        {
            var result = new List<McpServerTool>();
            foreach (var tool in tools)
            {
                // Local controls must remain accessible even while a worker request is hung.
                // Host controls are bounded diagnostics (journal reads cap at 500 records).
                // Do not create parent-side export handles: engineering exports live only in the worker.
                // Host controls share their own short-lived gate/journal. This process's
                // gate is separate from the engineering gate inside the child.
                var guarded = McpServer.WrapWithSerializedCalls(McpServer.WrapWithArgDiagnostics(new List<McpServerTool> { tool }))[0];
                result.Add(new ProxyTool(tool.ProtocolTool, guarded));
            }
            return result;
        }

        private sealed class ProxyTool : McpServerTool
        {
            private readonly Tool tool;
            private readonly McpServerTool local;
            internal ProxyTool(Tool tool, McpServerTool local) { this.tool = tool; this.local = local; }
            public override Tool ProtocolTool => tool;
            public override async ValueTask<CallToolResult> InvokeAsync(RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken = default)
            {
                bool controlBridge = tool.Name == "CallTool" && request.Params?.Arguments != null &&
                    request.Params.Arguments.TryGetValue("name", out var target) && target.ValueKind == JsonValueKind.String && IsControl(target.GetString());
                if (IsControl(tool.Name) || controlBridge) return await local.InvokeAsync(request, cancellationToken).ConfigureAwait(false);
                var supervisor = Current ?? throw new InvalidOperationException("Worker host stopped.");
                try
                {
                    var parameters = JsonNode.Parse(JsonSerializer.Serialize(request.Params, McpJsonUtilities.DefaultOptions)) as JsonObject
                        ?? throw new ArgumentException("Tool request is required.");
                    // Preserve progress metadata. Notification delivery cannot block the worker's reader.
                    Action<JsonObject> progress = frame =>
                    {
                        try
                        {
                            _ = request.Server.SendNotificationAsync("notifications/progress", frame["params"], cancellationToken: cancellationToken)
                                .ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
                        }
                        catch { }
                    };
                    var envelope = await supervisor.CallAsync(parameters, progress, cancellationToken).ConfigureAwait(false);
                    if (envelope["error"] != null) return Error("Worker rejected the request: " + envelope["error"]!.ToJsonString(), false, supervisor);
                    return JsonSerializer.Deserialize<CallToolResult>(envelope["result"]!.ToJsonString(), McpJsonUtilities.DefaultOptions)
                        ?? throw new InvalidDataException("Worker returned no tool result.");
                }
                catch (WorkerCallException ex) { return Error(ex.Message, ex.OutcomeUnknown, supervisor); }
            }
            private static CallToolResult Error(string message, bool unknown, OpennessWorkerSupervisor supervisor) => new CallToolResult {
                IsError = true,
                Content = new[] { new TextContentBlock { Text = new JsonObject { ["message"] = message,
                    ["meta"] = new JsonObject { ["success"] = false, ["nativeOutcomeUnknown"] = unknown, ["automaticReplay"] = false, ["worker"] = supervisor.Snapshot() } }.ToJsonString() } }
            };
        }
    }
}
