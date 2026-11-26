using System;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.WorkerChannel;
using TiaMcpServer.ModelContextProtocol;
using TiaMcpServer.Siemens;
using TiaOpenness.Shared;

namespace TiaMcp.PlcWorker
{
    // Registered on all eight releases. The fault retains native handles solely for an
    // acknowledged owner-thread Disconnect; exit after uncertainty abandons them.
    internal sealed class WorkerSessionRecovery
    {
        private readonly WorkerSessionOutcomeState _outcome;
        private readonly Func<bool> _detached;
        private readonly Action _refresh, _clear, _invalidate, _dispose;
        private readonly Action? _detachOnShutdown;
        private readonly Func<bool> _canDetachOnShutdown;
        private string? portalLost;
        private int dispatchDepth;
        private long revisionAtEntry;
        internal JsonArray ShutdownEvidence { get; } = new JsonArray();

        internal WorkerSessionRecovery(WorkerSessionOutcomeState outcome, Func<bool> detached,
            Action refresh, Action clear, Action invalidate, Action dispose,
            Action? detachOnShutdown = null, Func<bool>? canDetachOnShutdown = null)
        {
            _outcome = outcome; _detached = detached; _refresh = refresh; _clear = clear;
            revisionAtEntry = outcome.Revision;
            _invalidate = invalidate; _dispose = dispose;
            _detachOnShutdown = detachOnShutdown;
            _canDetachOnShutdown = canDetachOnShutdown ?? (() => true);
        }

        internal void ObserveFailure(Exception error)
        {
            if (ProcessLossPolicy.Reason(error) is string reason)
                ObserveLoss(reason);
        }

        internal void InstallProcessLossObserver() => PortalFailureClassifier.SetProcessLostObserver(ObserveLoss);

        private void ObserveLoss(string reason)
        {
            if (portalLost == null || ProcessLossPolicy.IsComException(portalLost)) portalLost = reason;
        }

        internal void ObserveProcessExit() => portalLost = "tia-process-exited";

        internal ChannelResponse Dispatch(ChannelRequest request, Func<ChannelRequest, ChannelResponse> dispatch)
        {
            if (dispatchDepth == 0)
            {
                portalLost = null;
                revisionAtEntry = _outcome.Revision;
            }
            dispatchDepth++;
            try { return dispatch(request); }
            finally { dispatchDepth--; }
        }

        internal ChannelResponse Synchronize(ChannelRequest request, ChannelResponse response)
        {
            if (_detached()) { _clear(); return response; }
            string operation = request.Method.Substring("adapter.".Length);
            bool observation = operation is "ReadState" or "HostObserve";
            bool write = !observation && !WorkerOperations.IsReadOnly(operation)
                && !(JsonNode.Parse(request.ArgumentsJson) is JsonObject args && args["dryRun"] is JsonValue dryRun
                    && dryRun.TryGetValue<bool>(out var preview) && preview);
            var failure = response.Failure;
            bool confirmedLoss = !observation && ProcessLossPolicy.IsConfirmedLoss(portalLost);
            if (confirmedLoss)
            {
                failure = new ChannelFailureException("The TIA process exited during this call. The session is lost.", failure?.Code ?? -32603,
                    write ? ChannelOutcome.Unknown : ChannelOutcome.ReadFailed, failure == null ? response.ResultJson : failure.EvidenceJson);
            }
            else if (failure == null && _outcome.Revision != revisionAtEntry && write)
                failure = new ChannelFailureException("The write outcome is unknown.", -32603, ChannelOutcome.Unknown, response.ResultJson);
            if (failure?.Outcome == ChannelOutcome.Unknown && write) _outcome.MarkUncertain();
            if (portalLost != null && failure != null && (confirmedLoss || response.Failure != null))
            {
                var original = JsonNode.Parse(failure.EvidenceJson ?? "{}");
                var evidence = original as JsonObject ?? new JsonObject { ["result"] = original };
                evidence["portalLost"] = portalLost;
                failure = new ChannelFailureException(failure.Message, failure.Code, failure.Outcome, evidence.ToJsonString());
            }
            _refresh();
            return failure == null ? response : ChannelResponse.Error(failure);
        }

        internal void DisposeSession()
        {
            try
            {
                if (_outcome.Uncertain)
                {
                    // EOF runs on the native owner lane. Disposing an owned/unknown-lifetime
                    // portal could close TIA while the write outcome is still unknown.
                    try
                    {
                        if (_canDetachOnShutdown()) _detachOnShutdown?.Invoke();
                        else RecordShutdown("retained-uncertain-portal", null);
                    }
                    catch (TiaMcp.Adapters.Contracts.AdapterPreconditionException error) { RecordShutdown("detach", error); }
                    catch (NotSupportedException error) { RecordShutdown("detach", error); }
                    catch (InvalidOperationException error) { RecordShutdown("detach", error); }
                    finally { if (!_detached()) _invalidate(); }
                }
            }
            catch (Exception error) /* swallow(teardown): retain shutdown evidence and always finish managed cleanup */
            { RecordShutdown("invalidate", error); }
            finally
            {
                try { _dispose(); }
                catch (Exception error) /* swallow(teardown): Dispose must not escape the worker's shutdown boundary */
                { RecordShutdown("dispose", error); }
            }
        }

        private void RecordShutdown(string phase, Exception? error)
        {
            if (error != null) _outcome.MarkUncertain();
            string? message = error?.Message;
            if (message != null)
            {
                int line = message.IndexOfAny(new[] { '\r', '\n' });
                if (line >= 0) message = message.Substring(0, line);
                if (message.Length > 1024) message = message.Substring(0, 1024);
            }
            ShutdownEvidence.Add(new JsonObject { ["phase"] = phase, ["exceptionType"] = error?.GetType().Name,
                ["message"] = message, ["outcomeUnknown"] = _outcome.Uncertain, ["writeOutcomeUnknown"] = _outcome.Uncertain });
            Console.Error.WriteLine("PlcWorker shutdown: " + ShutdownEvidence[ShutdownEvidence.Count - 1]!.ToJsonString());
        }
    }

    // Admission and teardown surround the existing owner-thread dispatcher. This
    // boundary uses only OS process identity and the shared persistent file lease.
    internal sealed class FoundationProcessLease : IDisposable
    {
        private readonly Func<string> root;
        private readonly Func<ChannelRequest, ChannelResponse> dispatch;
        private readonly Func<bool> attached, detached, requiresReset;
        private readonly Action disposeEngine;
        private readonly Func<ChannelRequest, ChannelResponse, ChannelResponse>? afterDispatch;
        private readonly int? hostProcessId;
        private readonly string? releaseKey;
        private readonly Action? processExited;
        private readonly Func<int, long, bool?> processAlive;
        private int leasedProcessId;
        private PortalProcessLease? lease;
        private string? currentRequestId;
        private bool uncertain, disposed;
        internal long ProcessStartTicks { get; private set; }
        internal bool Reserved => lease != null;

        internal FoundationProcessLease(Func<string> root, Func<ChannelRequest, ChannelResponse> dispatch,
            Func<bool> attached, Func<bool> detached, Func<bool> requiresReset, Action disposeEngine,
            Func<ChannelRequest, ChannelResponse, ChannelResponse>? afterDispatch = null, int? hostProcessId = null, string? releaseKey = null,
            Action? processExited = null, Func<int, long, bool?>? processAlive = null)
        {
            this.root = root; this.dispatch = dispatch; this.attached = attached;
            this.detached = detached; this.requiresReset = requiresReset; this.disposeEngine = disposeEngine;
            this.afterDispatch = afterDispatch;
            this.hostProcessId = hostProcessId;
            this.releaseKey = releaseKey;
            this.processExited = processExited;
            this.processAlive = processAlive ?? IsProcessAlive;
        }

        private static bool? IsProcessAlive(int pid, long startTicks)
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                return !process.HasExited && process.StartTime.ToUniversalTime().Ticks == startTicks;
            }
            catch (ArgumentException) { /* swallow(env-probe): the PID no longer exists */ return false; }
            catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or System.Security.SecurityException)
            { /* swallow(env-probe): an unreadable process cannot prove exit */ return null; }
        }

        internal long ReserveCreatedProcess(int processId)
        {
            if (lease != null) throw new InvalidOperationException("A TIA process lease is already reserved.");
            using var process = Process.GetProcessById(processId);
            if (process.HasExited) throw new InvalidOperationException("TIA process exited before attachment.");
            long ticks = process.StartTime.ToUniversalTime().Ticks;
            lease = PortalProcessLease.Acquire(root(), processId, ticks, hostProcessId, releaseKey);
            leasedProcessId = processId;
            ProcessStartTicks = ticks;
            lease.BeginRequest(currentRequestId);
            return ticks;
        }

        private static int? AttachProcessId(ChannelRequest request)
        {
            if (request.Method != "adapter.Attach") return null;
            using var document = JsonDocument.Parse(request.ArgumentsJson);
            var args = document.RootElement;
            if (args.ValueKind != JsonValueKind.Object) return null;
            return args.TryGetProperty("processId", out var pid) && pid.ValueKind == JsonValueKind.Number && pid.TryGetInt32(out var id) ? id : (int?)null;
        }

        internal ChannelResponse Dispatch(ChannelRequest request)
        {
            currentRequestId = request.CorrelationId;
            bool reserved = false, entered = false;
            try
            {
                var pid = AttachProcessId(request);
                if (pid.HasValue && lease == null)
                {
                    using var process = Process.GetProcessById(pid.Value);
                    if (process.HasExited) throw new InvalidOperationException("TIA process exited before attachment.");
                    ProcessStartTicks = process.StartTime.ToUniversalTime().Ticks;
                    lease = PortalProcessLease.Acquire(root(), pid.Value, ProcessStartTicks, hostProcessId, releaseKey);
                    leasedProcessId = pid.Value;
                    reserved = true;
                }
                // Post-call probes precede the host's outcome recording. Retain new unknown IDs
                // until the next operation supplies its updated unsettled snapshot.
                if (!reserved && request.Method is not ("adapter.ReadState" or "adapter.HostObserve"))
                    lease?.SetUnsettledRequests(request.UnsettledRequests);
                if (request.Method != "adapter.ReadState") lease?.BeginRequest(request.CorrelationId);
                int entryPid = leasedProcessId;
                long entryTicks = ProcessStartTicks;
                bool checkExit = processExited != null && lease != null && request.Method != "adapter.HostObserve"
                    && WorkerOperations.RequiresPortal(request.Method.Substring("adapter.".Length)) && processAlive(entryPid, entryTicks) == true;
                entered = true;
                var response = dispatch(request);
                if (checkExit && processAlive(entryPid, entryTicks) == false) processExited!();
                if (afterDispatch != null) response = afterDispatch(request, response);
                if (detached()) { ReleaseCleanly(); uncertain = false; }
                else
                {
                    bool callUnknown = response.Failure?.Outcome == ChannelOutcome.Unknown || requiresReset() && !uncertain;
                    uncertain |= callUnknown;
                    // A rejected attach leaves no native connection and can return its reservation.
                    if (reserved && !attached() && !uncertain) ReleaseCleanly();
                    else lease?.CompleteRequest(callUnknown);
                    if (reserved && lease?.PreviousOwnerState is string previous && attached() && response.Failure == null)
                    {
                        var result = JsonNode.Parse(response.ResultJson)!.AsObject();
                        result["PreviousOwnerState"] = previous;
                        result["PreviousOwnerRequests"] = JsonSerializer.SerializeToNode(lease.PreviousOwnerRequests);
                        response = ChannelResponse.Success(result.ToJsonString());
                    }
                }
                return response;
            }
            catch (Exception error)
            {
                uncertain |= entered;
                if (reserved && !uncertain) ReleaseCleanly();
                else lease?.CompleteRequest(uncertain);
                var failure = WorkerFailurePolicy.Classify(error, entered, false);
                return ChannelResponse.Error(new ChannelFailureException(WorkerFailurePolicy.DiagnosticCause(error).Message,
                    failure.Code, failure.Outcome, WorkerJson.Evidence(error)));
            }
            finally { currentRequestId = null; }
        }

        private void ReleaseCleanly() { lease?.ReleaseCleanly(); lease = null; leasedProcessId = 0; ProcessStartTicks = 0; }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            try
            {
                // Preserve the existing native teardown boundary. Only an acknowledged
                // clean exit releases; a job-object kill never reaches this finally.
                try { lease?.BeginRequest(); }
                catch (Exception error) /* swallow(teardown): a lease write failure must not skip native cleanup */
                { uncertain = true; Console.Error.WriteLine("PlcWorker lease shutdown begin: " + error.GetType().Name); }
                disposeEngine();
                uncertain |= requiresReset();
                if (!uncertain || detached()) ReleaseCleanly();
                else lease?.CompleteRequest(true);
            }
            catch (Exception error) /* swallow(teardown): preserve an uncertain lease and finish shutdown */
            {
                try { lease?.CompleteRequest(true); }
                catch (Exception markerError) /* swallow(teardown): a failed uncertainty marker cannot prevent releasing the file handle */
                { Console.Error.WriteLine("PlcWorker lease shutdown marker: " + markerError.GetType().Name); }
                Console.Error.WriteLine("PlcWorker lease shutdown: " + error.GetType().Name);
            }
            finally
            {
                try { lease?.Dispose(); }
                catch (Exception error) /* swallow(teardown): shutdown records cleanup failure without escaping Dispose */
                { Console.Error.WriteLine("PlcWorker lease shutdown dispose: " + error.GetType().Name); }
                lease = null;
            }
        }
    }
}
