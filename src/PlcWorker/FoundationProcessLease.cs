using System;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.WorkerChannel;
using TiaMcpServer.Siemens;

namespace TiaMcp.PlcWorker
{
    // Admission and teardown surround the existing owner-thread dispatcher. This
    // boundary uses only OS process identity and the shared persistent file lease.
    internal sealed class FoundationProcessLease : IDisposable
    {
        private readonly Func<string> root;
        private readonly Func<ChannelRequest, ChannelResponse> dispatch;
        private readonly Func<bool> attached, detached, requiresReset;
        private readonly Action disposeEngine;
        private PortalProcessLease? lease;
        private bool uncertain, disposed;

        internal FoundationProcessLease(Func<string> root, Func<ChannelRequest, ChannelResponse> dispatch,
            Func<bool> attached, Func<bool> detached, Func<bool> requiresReset, Action disposeEngine)
        {
            this.root = root; this.dispatch = dispatch; this.attached = attached;
            this.detached = detached; this.requiresReset = requiresReset; this.disposeEngine = disposeEngine;
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
                    lease = PortalProcessLease.Acquire(root(), pid.Value, process.StartTime.ToUniversalTime().Ticks);
                    reserved = true;
                }
                if (request.Method != "adapter.ReadState") lease?.BeginRequest();
                entered = true;
                var response = dispatch(request);
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
                return ChannelResponse.Error(new ChannelFailure(WorkerFailurePolicy.DiagnosticCause(error).Message,
                    failure.Code, failure.Outcome, WorkerJson.Evidence(error)));
            }
        }

        private void ReleaseCleanly() { lease?.ReleaseCleanly(); lease = null; }

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
