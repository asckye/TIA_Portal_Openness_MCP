using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using TiaMcp.Logic.V4;
using TiaMcp.Logic.V4.Inputs;
using TiaMcp.WorkerChannel;
using TiaMcpServer.ModelContextProtocol;
using TiaMcpServer.Runtime;
using TiaOpenness.Shared;

namespace TiaMcpServer.Worker
{
    // The woven engine remains the only owner of Siemens objects. No MCP transport
    // or audit wrapper runs in this process; the host owns approval and audit.
    internal sealed class EngineWorkerHost
    {
        private long epoch;
        private string? nativeFault;
        private string binding = "null";

        internal static string Hash(string path)
        {
            using var input = File.OpenRead(path);
            using var sha = SHA256.Create();
            return BitConverter.ToString(sha.ComputeHash(input)).Replace("-", "").ToLowerInvariant();
        }

        internal static void Run()
        {
            if (Thread.CurrentThread.GetApartmentState() != ApartmentState.MTA)
                throw new InvalidOperationException("Engine worker requires the MTA owner thread.");
            // SDK-only proofs are separate, explicitly marked test artifacts. The
            // production executable never honours a readiness environment override.
            if (Environment.GetEnvironmentVariable("TIA_MCP_ENGINE_WORKER_SDK_READY") == "1"
                && System.Attribute.GetCustomAttributes(typeof(EngineWorkerHost).Assembly, typeof(AssemblyMetadataAttribute))
                    .OfType<AssemblyMetadataAttribute>().Any(a => a.Key == "TiaMcpEngineWorkerSdkFixture" && a.Value == "true"))
                OpennessReadiness.MarkReady(true);
            using var services = new ServiceCollection().AddLogging().AddEngine(includeSession: OpennessReadiness.Ready).BuildServiceProvider();
            EngineServices.SetServiceProvider(services);
            var host = new EngineWorkerHost();
            PortalFailureClassifier.ProcessLostObserved += host.ProcessLost;
            try
            {
                string exe = Assembly.GetExecutingAssembly().Location;
                var identity = new ChannelIdentity(McpServer.ReleaseKey, Hash(exe),
                    Hash(Path.Combine(Path.GetDirectoryName(exe)!, "TiaMcp.Adapter." + McpServer.ReleaseKey + ".dll")),
                    System.Diagnostics.Process.GetCurrentProcess().Id, Environment.GetEnvironmentVariable("TIA_MCP_ENGINE_NONCE")!);
                new ChannelServer(Console.OpenStandardInput(), Console.OpenStandardOutput(), identity,
                    () => new ChannelBinding(host.epoch, host.binding != "null"), host.Dispatch, ChannelProfile.Engine).Run();
            }
            finally { PortalFailureClassifier.ProcessLostObserved -= host.ProcessLost; }
        }

        private void ProcessLost(string reason) => nativeFault = reason;
        private JsonObject Status() => new JsonObject {
            ["readiness"] = new JsonObject { ["ready"] = OpennessReadiness.Ready, ["cause"] = OpennessReadiness.Cause,
                ["recommendedFix"] = OpennessReadiness.FixEn, ["recommendedFixZh"] = OpennessReadiness.FixZh },
            ["binding"] = JsonNode.Parse(binding), ["session"] = CachedSession(), ["nativeFault"] = nativeFault
        };

        private static JsonNode? CachedSession()
        {
            if (!OpennessReadiness.Ready) return null;
            // Reflection keeps Siemens types out of the no-install JIT path. GetState
            // uses cached identity and OS liveness; it never queries native collections.
            var type = typeof(EngineWorkerHost).Assembly.GetType("TiaMcpServer.Siemens.Portal")!;
            var portal = EngineServices.GetIfInitialized(type);
            return portal == null ? null : JsonSerializer.SerializeToNode(type.GetMethod("GetState")!.Invoke(portal, null));
        }

        private ChannelResponse Dispatch(ChannelRequest request)
        {
            if (request.Method == "engine.status") return ChannelResponse.Success(Status().ToJsonString());
            if (request.Method != "engine.invoke")
                return ChannelResponse.Error(new ChannelFailure("Unknown engine operation.", -32602, ChannelOutcome.RejectedBeforeNative));
            using var document = JsonDocument.Parse(request.ArgumentsJson);
            var args = document.RootElement;
            string id = args.GetProperty("requestId").GetString()!;
            string name = args.GetProperty("name").GetString()!;
            bool preview = args.GetProperty("preview").GetBoolean();
            using var correlation = InvocationJournal.UseCorrelation(id);
            using var previewScope = preview ? McpServer.BeginReadOnlyApprovalPreview() : null;
            using var auditPreview = preview ? AuditInvocation.ReadOnlyPreview() : null;
            using var nativeCalls = InvocationJournal.BeginNativeCallScope();
            var error = McpServer.BindV4Call(name, new ToolArguments(args.GetProperty("arguments")), out var method, out var call);
            CallToolResult result;
            if (error != null) result = McpServer.V4Reject(name, error);
            else if (!OpennessReadiness.Ready && !ToolTaxonomy.IsSafeWithoutTia(name))
            {
                var environment = Status()["readiness"]!.DeepClone();
                result = McpServer.V4Reject(name, new Error(OpennessReadiness.Cause + " " + OpennessReadiness.FixEn,
                    new ResourceUnavailableDetails("tia-openness-environment")), new JsonObject { ["environment"] = environment });
            }
            else result = McpServer.ToolResult(McpServer.InvokeToolMethod(method!, call!));
            string after = InvocationJournal.BindingSnapshot?.Invoke()?.ToJsonString() ?? "null";
            if (after != binding) { epoch++; binding = after; }
            var reply = Status();
            reply["nativeCallIssued"] = nativeCalls.NativeCallIssued;
            reply["result"] = JsonSerializer.SerializeToNode(result, global::ModelContextProtocol.McpJsonUtilities.DefaultOptions);
            string wire = reply.ToJsonString();
            if (System.Text.Encoding.UTF8.GetByteCount(wire) < ChannelLimits.ResponseBytes - 256)
                return ChannelResponse.Success(wire);
            string? execution = (string?)result.StructuredContent?["meta"]?["execution"];
            bool unknown = (string?)result.StructuredContent?["meta"]?["outcome"] == "unknown";
            var limited = McpServer.V4Result(name, new JsonObject { ["nativeOutcomeUnknown"] = unknown },
                new Error("Worker response exceeds its byte limit.", new LimitExceededDetails("response", ChannelLimits.ResponseBytes, System.Text.Encoding.UTF8.GetByteCount(wire))),
                execution == "not-started" ? Outcome.RejectedBeforeOperation : execution == "read-only" ? Outcome.ReadFailed : Outcome.Failed,
                execution == "not-started" ? Execution.NotStarted : execution == "read-only" ? Execution.ReadOnly : Execution.Completed, Completeness.None);
            reply["result"] = JsonSerializer.SerializeToNode(limited, global::ModelContextProtocol.McpJsonUtilities.DefaultOptions);
            return ChannelResponse.Success(wire, reply.ToJsonString());
        }
    }
}
