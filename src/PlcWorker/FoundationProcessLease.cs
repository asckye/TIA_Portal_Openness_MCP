using System;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.WorkerChannel;
using TiaMcpServer.Siemens;

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
            if (TiaOpenness.Shared.ProcessLossPolicy.Reason(error) is string reason)
            {
                portalLost = reason;
                _outcome.MarkUncertain();
            }
        }

        internal ChannelResponse Dispatch(ChannelRequest request, Func<ChannelRequest, ChannelResponse> dispatch)
        {
            portalLost = null;
            revisionAtEntry = _outcome.Revision;
            return dispatch(request);
        }

        internal ChannelResponse Synchronize(ChannelRequest request, ChannelResponse response)
        {
            if (_detached()) { _clear(); return response; }
            string operation = request.Method.Substring("adapter.".Length);
            if (response.Failure == null && _outcome.Revision != revisionAtEntry && !WorkerOperations.IsReadOnly(operation)
                && (bool?)JsonNode.Parse(request.ArgumentsJson)?["dryRun"] != true)
                response = ChannelResponse.Error(new ChannelFailureException("The write outcome is unknown.", -32603,
                    ChannelOutcome.Unknown, response.ResultJson));
            if (response.Failure?.Outcome == ChannelOutcome.Unknown) _outcome.MarkUncertain();
            if (portalLost != null && response.Failure is { } failure)
            {
                var evidence = JsonNode.Parse(failure.EvidenceJson ?? "{}") as JsonObject ?? new JsonObject();
                evidence["portalLost"] = portalLost;
                response = ChannelResponse.Error(new ChannelFailureException(failure.Message, failure.Code, failure.Outcome, evidence.ToJsonString()));
            }
            _refresh();
            return response;
        }

        internal void DisposeSession()
        {
            try
            {
                if (_outcome.RequiresReset)
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
                ["message"] = message, ["requiresSessionReset"] = _outcome.RequiresReset });
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
            if (request.Method != "adapter.Attach") return null;
            using var document = JsonDocument.Parse(request.ArgumentsJson);
            var args = document.RootElement;
            if (args.ValueKind != JsonValueKind.Object) return null;
            return args.TryGetProperty("processId", out var pid) && pid.ValueKind == JsonValueKind.Number && pid.TryGetInt32(out var id) ? id : (int?)null;
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
                    // A rejected attach leaves no native connection and can return its reservation.
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
