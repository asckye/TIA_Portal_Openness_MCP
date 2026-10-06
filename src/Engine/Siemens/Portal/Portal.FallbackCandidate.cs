using System;
using System.Threading;
using TiaMcp.Adapters.Contracts.Candidates;
using TiaMcp.Logic.V4;

namespace TiaMcpServer.Siemens
{
    public partial class Portal
    {
        private readonly FallbackSession fallbackCandidates = new FallbackSession();
        private string? fallbackGeneration;
        private long fallbackEpoch;
        internal SessionState FallbackBinding()
        {
            if (Thread.CurrentThread.GetApartmentState() != ApartmentState.MTA) throw new InvalidOperationException("Fallback candidates require the owning MTA thread.");
            VerifyBinding("fallback-candidate");
            var value = GetBindingIdentity()["identity"]!.AsObject(); string generation = value["generation"]!.GetValue<string>();
            if (generation != fallbackGeneration) { fallbackGeneration = generation; fallbackEpoch++; }
            return new SessionState { ProcessId = _boundProcessId, ProcessStartUtc = new DateTimeOffset(new DateTime(_processStartTicks, DateTimeKind.Utc)),
                ProjectFile = CandidatePrimitives.CanonicalProject(_project!.Path.FullName), Epoch = fallbackEpoch, Ownership = _projectOpenedByUs ? "owned" : "borrowed" };
        }
        internal void FallbackUncertain() => _bindingFault = "Fallback write outcome is unknown; rebuild the session and do not replay.";
        internal Envelope RunFallbackCandidate(FallbackRequest request, Func<IFallbackAdapter> factory, string credential, string mode, bool confirm, string hash, string file)
        {
            string release = ModelContextProtocol.McpServer.ReleaseKey, id = Meta.Correlate(ModelContextProtocol.InvocationJournal.CorrelationId);
            if (_project == null || _portal == null) return FallbackSession.Result(release, request.Entry, id, null, new Error("No project is bound.", new ProjectNotBoundDetails()), Outcome.RejectedBeforeOperation, Execution.NotStarted);
            if (fallbackCandidates.RequiresSessionReset || _bindingFault != null) return FallbackSession.Result(release, request.Entry, id, null,
                new Error("The native session must be rebuilt.", new SessionResetRequiredDetails("fallback-unknown")), Outcome.RejectedBeforeOperation, Execution.NotStarted);
            try
            {
                FallbackBinding();
                var result = fallbackCandidates.Run(factory(), release, request.Entry, id, request, credential, mode, confirm, hash, file);
                if (result.Meta.RequiresSessionReset) FallbackUncertain();
                return result;
            }
            catch (Exception error)
            {
                var refusal = error is CandidateObservationException observed ? FallbackSession.Map(observed.Fault, hash)
                    : new Error("Fallback target or binding is unavailable.", new PreconditionFailedDetails("fallback-binding-and-target", null));
                return FallbackSession.Result(release, request.Entry, id, null, refusal, Outcome.RejectedBeforeOperation, Execution.NotStarted);
            }
        }
    }
}
