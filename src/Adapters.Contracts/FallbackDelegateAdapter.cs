using System;

namespace TiaMcp.Adapters.Contracts.Candidates
{
    public sealed class FallbackDelegateAdapter : IFallbackAdapter, IFallbackBoundary
    {
        public bool Writes { get; set; }
        public bool NeedsConfiguration { get; set; }
        public bool RequiresOffline { get; set; }
        public Func<FallbackObservation> Read { private get; set; } = null!;
        public Action Before { private get; set; } = () => { };
        public Func<string, bool> Configure { private get; set; } = _ => throw new NotSupportedException();
        public Func<FallbackRequest, FallbackAttempt, FallbackNativeResult> Operation { private get; set; } = null!;
        public Action Refresh { private get; set; } = () => throw new NotSupportedException();
        public Action Uncertain { private get; set; } = () => { };
        public FallbackObservation Observe() => Read();
        public void BeforeAction() => Before();
        public bool ApplyConfiguration(string route) => Configure(route);
        public FallbackNativeResult Execute(FallbackRequest request, FallbackAttempt evidence) => Operation(request, evidence);
        public void RefreshReadHandle() => Refresh();
        public void MarkUncertain() => Uncertain();
        public FallbackAttempt Execute(FallbackCheck check) => CandidateExecution.Fallback(this, check);
    }
}
