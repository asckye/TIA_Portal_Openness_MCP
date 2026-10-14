using System;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.WorkerChannel;
using TiaMcpServer.Siemens;

namespace TiaMcp.PlcWorker
{
    // Registered only on V20/V21. The fault retains native handles solely for an
    // acknowledged owner-thread Disconnect; exit after uncertainty abandons them.
    internal sealed class WorkerSessionRecovery
    {
        private readonly WorkerSessionOutcomeState _outcome;
        private readonly Func<bool> _detached;
        private readonly Action _refresh, _clear, _invalidate, _dispose;
        private readonly Action<string> _lock;
        private readonly Func<string?>? _bindingFault;
        internal string? Fault { get; private set; }

        internal WorkerSessionRecovery(WorkerSessionOutcomeState outcome, Func<bool> detached,
            Action refresh, Action clear, Action<string> lockBinding, Action invalidate, Action dispose,
            Func<string?>? bindingFault = null)
        {
            _outcome = outcome; _detached = detached; _refresh = refresh; _clear = clear;
            _lock = lockBinding; _invalidate = invalidate; _dispose = dispose;
            _bindingFault = bindingFault;
        }

        internal void ObserveFailure(Exception error)
        {
            if (TiaOpenness.Shared.ProcessLossPolicy.Reason(error) != null) Fault ??= error.Message;
        }

        internal ChannelResponse Dispatch(ChannelRequest request, Func<ChannelRequest, ChannelResponse> dispatch)
        {
            if (Fault != null && request.Method != "adapter.Disconnect")
                return ChannelResponse.Error(new ChannelFailureException(TiaOpenness.Shared.SessionBehavior.Recovery,
                    -32603, ChannelOutcome.RejectedBeforeNative));
            return dispatch(request);
        }

        private void Lock(string reason)
        {
            Fault ??= reason;
            _outcome.MarkUncertain();
            _lock(Fault);
        }

        internal ChannelResponse Synchronize(ChannelRequest request, ChannelResponse response)
        {
            if (_detached()) { _clear(); return response; }
            Fault ??= _bindingFault?.Invoke();
            // A later, refused request must keep its rejected-before-native outcome.
            if (Fault != null && response.Failure?.Outcome == ChannelOutcome.RejectedBeforeNative
                && response.Failure.Message == TiaOpenness.Shared.SessionBehavior.Recovery) return response;
            if (Fault != null || _outcome.RequiresReset || response.Failure?.Outcome == ChannelOutcome.Unknown)
            {
                Lock(response.Failure?.Message ?? TiaOpenness.Shared.SessionBehavior.Recovery);
                if (response.Failure != null || !WorkerOperations.IsFamilyOperation(request.Method.Substring("adapter.".Length)))
                    return ChannelResponse.Error(new ChannelFailureException(Fault!, -32603, ChannelOutcome.Unknown,
                        response.Failure?.EvidenceJson ?? "null"));
                return response;
            }
            try { _refresh(); }
            catch (Exception error) { Lock(error.Message); throw; }
            return response;
        }

        internal void DisposeSession()
        {
            if (_outcome.RequiresReset) _invalidate();
            _dispose();
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
        private PortalProcessLease? lease;
        private bool uncertain, disposed;
        internal long ProcessStartTicks { get; private set; }
        internal bool Reserved => lease != null;

        internal FoundationProcessLease(Func<string> root, Func<ChannelRequest, ChannelResponse> dispatch,
            Func<bool> attached, Func<bool> detached, Func<bool> requiresReset, Action disposeEngine,
            Func<ChannelRequest, ChannelResponse, ChannelResponse>? afterDispatch = null)
        {
            this.root = root; this.dispatch = dispatch; this.attached = attached;
            this.detached = detached; this.requiresReset = requiresReset; this.disposeEngine = disposeEngine;
            this.afterDispatch = afterDispatch;
        }

        internal long ReserveCreatedProcess(int processId)
        {
            if (lease != null) throw new InvalidOperationException("A TIA process lease is already reserved.");
            using var process = Process.GetProcessById(processId);
            if (process.HasExited) throw new InvalidOperationException("TIA process exited before attachment.");
            long ticks = process.StartTime.ToUniversalTime().Ticks;
            lease = PortalProcessLease.Acquire(root(), processId, ticks);
            ProcessStartTicks = ticks;
            lease.BeginRequest();
            return ticks;
        }

        private static int? AttachProcessId(ChannelRequest request)
        {
            if (request.Method != "adapter.Attach" && request.Method != "adapter." + WorkerOperations.SessionCandidate) return null;
            using var document = JsonDocument.Parse(request.ArgumentsJson);
            var args = document.RootElement;
            if (args.ValueKind != JsonValueKind.Object) return null;
            if (request.Method == "adapter.Attach")
                return args.TryGetProperty("processId", out var pid) && pid.ValueKind == JsonValueKind.Number && pid.TryGetInt32(out var id) ? id : (int?)null;
            if (args.TryGetProperty("mode", out var mode) && mode.ValueKind == JsonValueKind.String && mode.GetString() == "apply"
                && args.TryGetProperty("candidate", out var candidate) && candidate.ValueKind == JsonValueKind.Object
                && candidate.TryGetProperty("Action", out var action) && action.ValueKind == JsonValueKind.String && action.GetString() == "execute"
                && candidate.TryGetProperty("Check", out var check) && check.ValueKind == JsonValueKind.Object
                && check.TryGetProperty("Request", out var planned) && planned.ValueKind == JsonValueKind.Object
                && planned.TryGetProperty("Action", out var plannedAction) && plannedAction.ValueKind == JsonValueKind.String && plannedAction.GetString() == "attach"
                && planned.TryGetProperty("ProcessId", out var selected) && selected.ValueKind == JsonValueKind.Number && selected.TryGetInt32(out var attachPid)) return attachPid;
            return null;
        }

        internal ChannelResponse Dispatch(ChannelRequest request)
        {
            bool reserved = false, entered = false;
            try
            {
                var pid = AttachProcessId(request);
                if (pid.HasValue && lease == null)
                {
                    using var process = Process.GetProcessById(pid.Value);
                    if (process.HasExited) throw new InvalidOperationException("TIA process exited before attachment.");
                    ProcessStartTicks = process.StartTime.ToUniversalTime().Ticks;
                    lease = PortalProcessLease.Acquire(root(), pid.Value, ProcessStartTicks);
                    reserved = true;
                }
                if (request.Method != "adapter.ReadState") lease?.BeginRequest();
                entered = true;
                var response = dispatch(request);
                if (afterDispatch != null) response = afterDispatch(request, response);
                if (detached()) { ReleaseCleanly(); uncertain = false; }
                else
                {
                    uncertain |= response.Failure?.Outcome == ChannelOutcome.Unknown || requiresReset();
                    // A rejected legacy attach or a candidate that never issued its
                    // attach leaves no native connection and can return its reservation.
                    if (reserved && !attached() && !uncertain) ReleaseCleanly();
                    else lease?.CompleteRequest(uncertain);
                    if (reserved && lease?.PreviousOwnerEndedIdle == true && attached() && response.Failure == null)
                    {
                        var result = JsonNode.Parse(response.ResultJson)!.AsObject();
                        result["PreviousOwnerEndedIdle"] = true;
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
        }

        private void ReleaseCleanly() { lease?.ReleaseCleanly(); lease = null; ProcessStartTicks = 0; }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            try
            {
                // Preserve the existing native teardown boundary. Only an acknowledged
                // clean exit releases; a job-object kill never reaches this finally.
                lease?.BeginRequest();
                disposeEngine();
                if (!uncertain) ReleaseCleanly();
            }
            catch { lease?.CompleteRequest(true); throw; }
            finally { lease?.Dispose(); lease = null; }
        }
    }
}
