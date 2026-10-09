using System;
using System.IO;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using TiaMcp.Adapters;
using TiaMcp.PlcWorker;
using TiaMcpServer.Siemens;
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
        private PlcFoundationEngine? foundation;
        private FoundationWorkerDispatcher? foundationDispatch;
        private PortalProcessLease? processLease;
        private long processStartTicks;
        private Portal? portal;
        private SharedSessionLifecycle? lifecycle;
        private bool ownedPortal;


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
            // Methods that touch Siemens types are only compiled when Openness is ready; without TIA their JIT
            // fails to load the Siemens assemblies and the worker would crash on its way out.
            bool foundationStarted = OpennessReadiness.Ready;
            if (foundationStarted) host.InitializeFoundation();
            PortalFailureClassifier.ProcessLostObserved += host.ProcessLost;
            try
            {
                string exe = Assembly.GetExecutingAssembly().Location;
                var identity = new ChannelIdentity(McpServer.ReleaseKey, Hash(exe),
                    Hash(Path.Combine(Path.GetDirectoryName(exe)!, "TiaMcp.Adapter." + McpServer.ReleaseKey + ".dll")),
                    System.Diagnostics.Process.GetCurrentProcess().Id, Environment.GetEnvironmentVariable("TIA_MCP_ENGINE_NONCE")!);
                new ChannelServer(Console.OpenStandardInput(), Console.OpenStandardOutput(), identity,
                    host.Observe, host.Dispatch, ChannelProfile.Engine).Run();
            }
            finally
            {
                PortalFailureClassifier.ProcessLostObserved -= host.ProcessLost;
                if (foundationStarted) host.ReleaseFoundation();
            }
        }

        private void ProcessLost(string reason) => nativeFault = reason;
        [MethodImpl(MethodImplOptions.NoInlining)]
        private void InitializeFoundation()
        {
            foundation = new PlcFoundationEngine(McpServer.ReleaseKey, Path.GetDirectoryName(typeof(global::Siemens.Engineering.TiaPortal).Assembly.Location)!);
            foundationDispatch = new FoundationWorkerDispatcher(foundation, typeof(EngineWorkerHost).Assembly,
                error => { _ = PortalFailureClassifier.IsPortalProcessLost(error); });
            portal = (Portal)EngineServices.Get(typeof(Portal));
            var engineering = (IEngineeringSession)portal;
            foundation.AcquireEngineeringEditAccess = engineering.AcquireHmiEditAccess;
            foundation.EngineeringProjectMissing = engineering.IsProjectNull;
            foundation.ResolveEngineeringBlock = (software, block) => engineering.GetBlock(software, block);
            foundation.EngineeringBlockGroupPath = group => engineering.GetPlcBlockGroupPath((global::Siemens.Engineering.SW.Blocks.PlcBlockGroup)group);
            foundation.EngineeringRecordExportPath = portal.RecordAnalysisExportPath;
            // The shared binding and host lane already capture the target. Its
            // original engineering step performs native binding checks at the
            // original point; do not add Project.Path reads or PLC local-session
            // restrictions to a migrated engineering operation.
            foundation.SharedFamilyMutationIdentity = expected => TiaMcp.Adapters.MutationIdentityPolicy.RequireSameProject(expected, foundation.ReadState().ProjectFile);
            foundation.EngineeringStep = (tool, action) => {
                var step = engineering.RunHmiStepTool(tool, meta => {
                    var values = meta.ToDictionary(p => p.Key, p => (object?)p.Value);
                    try { return action(values); }
                    catch (TiaMcp.Adapters.Contracts.HardwareAddressingException failure)
                    { throw new PortalException((PortalErrorCode)Enum.Parse(typeof(PortalErrorCode), failure.Status), failure.Message); }
                    finally { foreach (var pair in values) meta[pair.Key] = pair.Value is JsonNode node ? node.DeepClone() : JsonSerializer.SerializeToNode(pair.Value); }
                });
                return new TiaMcp.Adapters.Contracts.HardwareAddressingReply {
                    Message = step.Message, Meta = step.Meta.ToDictionary(p => p.Key, p =>
                        p.Value is JsonValue value && value.TryGetValue<bool>(out var flag) ? (object?)flag : p.Value),
                    RequiresSessionReset = (bool?)step.Meta["connectionUnavailable"] == true
                        || (bool?)step.Meta["mayHaveChanged"] == true && step.Meta["after"] == null
                };
            };
            lifecycle = new SharedSessionLifecycle(RefreshFromEngine, RefreshFromFoundation, reason => {
                nativeFault = reason;
                // Keep the original attachment solely for an owner-thread recovery
                // Disconnect. The lifecycle fault blocks every other native operation.
                foundationDispatch.SessionOutcome.MarkUncertain();
                portal.ClearFoundationSession(reason);
            });
            lifecycle.AttachProcess = (pid, ticks, lease) => {
                processStartTicks = ticks; processLease = lease; ownedPortal = false;
                foundation.Attach(pid);
                RefreshFromFoundation();
            };
            portal.UseSharedLifecycle(lifecycle, () => {
                foundation.Disconnect(); foundationDispatch.EndSharedSession();
            });
            HardwareAddressWorkerBridge.HasProject = () => !string.IsNullOrEmpty(foundation.ReadState().ProjectFile);
            HardwareAddressWorkerBridge.ProjectIdentity = () => foundation.ReadState().ProjectFile;
            HardwareAddressWorkerBridge.Call = (operation, arguments) => {
                var response = DispatchFoundation(new ChannelRequest(0, "adapter." + operation, arguments.ToJsonString(), (_, __) => { }, InvocationJournal.CorrelationId));
                if (response.Failure != null) throw new InvalidOperationException(response.Failure.Message);
                return JsonNode.Parse(response.ResultJson);
            };
            RefreshFromFoundation();
        }

        private void RefreshFromFoundation()
        {
            if (foundationDispatch!.Detached)
            {
                portal!.ClearFoundationSession();
                processLease?.ReleaseCleanly(); processLease = null; processStartTicks = 0; ownedPortal = false;
            }
            else if (foundationDispatch.SessionOutcome.RequiresReset || nativeFault != null)
            { lifecycle!.Lock(nativeFault ?? TiaOpenness.Shared.SessionBehavior.Recovery); return; }
            else foundation!.BorrowSharedSession((tia, project, session, state) =>
                portal!.AdoptFoundationSession(tia, project, session, state, processStartTicks, processLease, ownedPortal));
        }

        private void RefreshFromEngine()
        {
            if (nativeFault != null) { lifecycle!.Lock(nativeFault); return; }
            portal!.BorrowEngineSession((tia, project, session, state, ticks, lease, owns) => {
                processStartTicks = ticks; processLease = lease; ownedPortal = owns;
                foundation!.AdoptSharedSession(tia, project, session, state, owns, ticks);
            });
            RefreshFromFoundation();
        }

        private ChannelBinding Observe()
        {
            var state = foundationDispatch?.Observe();
            string after = InvocationJournal.BindingSnapshot?.Invoke()?.ToJsonString() ?? "null";
            // Foundation supplies the binding epoch for both namespaces. A fault can
            // clear the cached binding without rebinding or making a second native call.
            if (state != null) epoch = state.Epoch;
            binding = after;
            return new ChannelBinding(epoch, state?.Bound ?? binding != "null");
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void ReleaseFoundation()
        {
            if (foundation == null) return;
            try
            {
                // An uncertain worker exiting without a recovery acknowledgement
                // abandons its handles and ACTIVE lease; it never retries disposal.
                if (nativeFault != null) foundation.InvalidateSharedSession();
                foundation.Dispose(); if (nativeFault == null) processLease?.ReleaseCleanly();
            }
            finally { processLease?.Dispose(); portal?.ClearFoundationSession(); }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private ChannelResponse DispatchFoundation(ChannelRequest request)
        {
            using var correlation = InvocationJournal.UseCorrelation(request.CorrelationId ?? InvocationJournal.CorrelationId);
            if (foundationDispatch == null) return ChannelResponse.Error(new ChannelFailure(
                OpennessReadiness.Cause + " " + OpennessReadiness.FixEn, -32603, ChannelOutcome.RejectedBeforeNative));
            bool disconnect = request.Method == "adapter.Disconnect";
            if (nativeFault != null && !disconnect) return ChannelResponse.Error(new ChannelFailure(
                TiaOpenness.Shared.SessionBehavior.Recovery, -32603, ChannelOutcome.RejectedBeforeNative));
            bool reserved = false, dispatched = false;
            try
            {
                using var document = JsonDocument.Parse(request.ArgumentsJson);
                var args = document.RootElement;
                int? pid = request.Method == "adapter.Attach" && args.TryGetProperty("processId", out var selected) && selected.TryGetInt32(out var id) ? id : (int?)null;
                if (request.Method == "adapter." + WorkerOperations.SessionCandidate && args.TryGetProperty("candidate", out var candidate)
                    && candidate.TryGetProperty("Check", out var check) && check.TryGetProperty("Request", out var planned)
                    && planned.TryGetProperty("Action", out var action) && action.GetString() == "attach"
                    && planned.TryGetProperty("ProcessId", out var candidatePid) && candidatePid.TryGetInt32(out var attachPid)) pid = attachPid;
                if (pid.HasValue && processLease == null)
                {
                    using var process = Process.GetProcessById(pid.Value);
                    if (process.HasExited) throw new InvalidOperationException("TIA process exited before attachment.");
                    processStartTicks = process.StartTime.ToUniversalTime().Ticks;
                    processLease = PortalProcessLease.Acquire(DataLocations.Current.LeasesDirectory, pid.Value, processStartTicks);
                    reserved = true;
                }
                dispatched = true;
                var response = disconnect ? lifecycle!.Disconnect(() => foundationDispatch.Dispatch(request))
                    : lifecycle!.Foundation(() => foundationDispatch.Dispatch(request));
                if (disconnect && response.Failure == null && foundationDispatch.Detached) RefreshFromFoundation();
                else if (nativeFault != null || foundationDispatch.SessionOutcome.RequiresReset || response.Failure?.Outcome == ChannelOutcome.Unknown)
                {
                    nativeFault ??= response.Failure?.Message ?? TiaOpenness.Shared.SessionBehavior.Recovery;
                    foundationDispatch.SessionOutcome.MarkUncertain();
                    lifecycle!.Lock(nativeFault);
                    // A typed family reply retains before/after evidence while the
                    // shared session is latched. Channel failures have no such reply.
                    if (response.Failure != null || !WorkerOperations.IsFamilyOperation(request.Method.Substring("adapter.".Length)))
                        response = ChannelResponse.Error(new ChannelFailure(nativeFault, -32603, ChannelOutcome.Unknown,
                            response.Failure?.EvidenceJson ?? "null"));
                }
                else if (response.Failure != null && reserved)
                {
                    processLease!.ReleaseCleanly(); processLease = null; processStartTicks = 0;
                    RefreshFromFoundation();
                }
                else RefreshFromFoundation();
                Observe();
                return response;
            }
            catch (Exception error)
            {
                // Reservation/adoption errors never retry a Foundation operation.
                if (nativeFault != null || lifecycle!.Fault != null || dispatched && foundationDispatch.State.IsAttached)
                { lifecycle!.Lock(error.Message); }
                else if (reserved) { processLease?.ReleaseCleanly(); processLease = null; processStartTicks = 0; RefreshFromFoundation(); }
                var failure = WorkerFailurePolicy.Classify(error, nativeFault != null, false);
                return ChannelResponse.Error(new ChannelFailure(WorkerFailurePolicy.DiagnosticCause(error).Message,
                    failure.Code, failure.Outcome, WorkerJson.Evidence(error)));
            }
        }

        private JsonObject Status() => new JsonObject {
            ["releaseKey"] = McpServer.ReleaseKey, ["behaviorCapabilities"] = McpServer.CatalogView.BehaviorCapabilities.DeepClone(),
            ["readiness"] = new JsonObject { ["ready"] = OpennessReadiness.Ready, ["cause"] = OpennessReadiness.Cause,
                ["groupOk"] = OpennessReadiness.GroupOk,
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
            return portal == null ? null : JsonSerializer.SerializeToNode(type.GetMethod("GetState")!.Invoke(portal, null), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }

        private ChannelResponse Dispatch(ChannelRequest request)
        {
            if (request.Method == "engine.status") { Observe(); return ChannelResponse.Success(Status().ToJsonString()); }
            if (request.Method == "host.observe")
            {
                try
                {
                    var observation = JsonNode.Parse(request.ArgumentsJson)!.AsObject();
                    return ChannelResponse.Success(HostObservations.Read((string)observation["operation"]!, observation["arguments"]!.AsObject())?.ToJsonString() ?? "null");
                }
                catch (Exception observationError)
                {
                    return ChannelResponse.Error(new ChannelFailure(observationError.Message, -32603, ChannelOutcome.ReadFailed));
                }
            }
            string? fixtureFault = WorkerFaultInjection.Mode(typeof(EngineWorkerHost).Assembly);
            WorkerFaultInjection.BeforeDispatch(fixtureFault, request);
            if (fixtureFault == "tia-lost") ProcessLost("Fixture TIA process lost.");
            if (request.Method.StartsWith("adapter.", StringComparison.Ordinal))
            {
                var response = DispatchFoundation(request);
                return response.Failure != null ? response : ChannelResponse.WithSpill(response.ResultJson,
                    () => WorkerReplySpill.Write(DataLocations.Current.WorkerSpillsDirectory, response.ResultJson));
            }
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
            else if (ToolUsageCatalog.ProfileEntries(McpServer.ReleaseKey).OfType<JsonObject>()
                .All(row => (string?)row["currentName"] != name || row["profiles"]!.AsArray().Any(p => (string?)p == "plc-foundation")))
                result = McpServer.V4Reject(name, new Error("Tool is not registered in this engine namespace.", new ToolNotFoundDetails(name)));
            else if (nativeFault != null || foundationDispatch?.Ended == true)
                result = McpServer.V4Reject(name, HostBehavior.SessionReset());
            else if (!OpennessReadiness.Ready && !ToolTaxonomy.IsSafeWithoutTia(name))
            {
                var environment = Status()["readiness"]!.DeepClone();
                result = McpServer.V4Reject(name, new Error(OpennessReadiness.Cause + " " + OpennessReadiness.FixEn,
                    new ResourceUnavailableDetails("tia-openness-environment")), new JsonObject { ["environment"] = environment });
            }
            else
            {
                using var progress = new WorkerProgressShim(request, args.TryGetProperty("progress", out var enabled) && enabled.ValueKind == JsonValueKind.True, EngineServices.Provider);
                progress.Bind(method!, call!);
                result = McpServer.ToolResult(McpServer.InvokeToolMethod(method!, call!));
            }
            if (TiaOpenness.Shared.SessionBehavior.LocksSession(nativeCalls.NativeCallIssued,
                (string?)result.StructuredContent?["meta"]?["outcome"] == "unknown", McpServer.IsWriteTool(name)
                    || ToolTaxonomy.OperationOf(name, null).Operation is "FILE" or "EXECUTE"))
            {
                lifecycle?.Lock("Engine native outcome is unknown.");
            }
            if (nativeFault != null) lifecycle?.Lock(nativeFault);
            Observe();
            var reply = Status();
            reply["nativeCallIssued"] = nativeCalls.NativeCallIssued;
            reply["result"] = JsonSerializer.SerializeToNode(result, global::ModelContextProtocol.McpJsonUtilities.DefaultOptions);
            string wire = reply.ToJsonString();
            return ChannelResponse.WithSpill(wire, () => WorkerReplySpill.Write(DataLocations.Current.WorkerSpillsDirectory, wire));
        }
    }
}
