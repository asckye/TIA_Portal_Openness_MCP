using TiaMcp.PlcWorker;
using TiaMcp.Adapters.Contracts;
using TiaMcp.LegacyHost;
using TiaMcp.WorkerChannel;

internal static class WorkerSessionOutcomeTests
{
    internal static void Run(Action<bool, string> check)
    {
        var rejected=WorkerFailurePolicy.Classify(new AdapterPreconditionException("Export directory must already exist.","exportPath"),true,false);
        check(rejected.Code==-32602 && rejected.Outcome==ChannelOutcome.RejectedBeforeNative,"Typed argument precondition is rejected without poisoning after method entry");
        var stateRefusal=WorkerFailurePolicy.Classify(new AdapterPreconditionException("Borrowed projects are never closed.",isArgument:false),true,false);
        check(stateRefusal.Code==-32603 && stateRefusal.Outcome==ChannelOutcome.RejectedBeforeNative,"Typed state precondition is rejected without an argument code");
        var ordinaryArgument=WorkerFailurePolicy.Classify(new ArgumentException("untyped failure"),true,false);
        check(ordinaryArgument.Outcome==ChannelOutcome.Unknown,"An untyped ArgumentException after write dispatch remains uncertain");
        check(!WorkerProtocol.RequiresSessionReset(true,new WorkerOperationException("safe refusal",-32602,"rejected-before-operation")),"A worker rejection does not poison the host session");

        var state = new WorkerSessionOutcomeState();
        int dispatched = 0;
        void Dispatch(bool readOnly)
        {
            state.RequireUsable(readOnly);
            dispatched++;
        }
        void Refuse(bool readOnly, string label)
        {
            var before = dispatched;
            bool refused = false;
            try { Dispatch(readOnly); }
            catch (InvalidOperationException) { refused = true; }
            check(refused && dispatched == before, label + " rejected before callback");
        }

        Dispatch(true);
        Dispatch(false);
        check(dispatched == 2, "Healthy worker permits reads and mutations");

        // Every unknown native write locks the session, including inspection.
        state.MarkUncertain();
        Refuse(true, "XML batch uncertainty blocks native reads");
        Refuse(false, "XML batch uncertainty blocks further mutation");

        // An uncertain document batch locks the entire session, including previews.
        state.MarkUncertain(blockReads: true);
        Refuse(true, "Document batch uncertainty blocks native reads");
        Refuse(true, "Document batch uncertainty blocks dry-run previews");
        Refuse(false, "Document batch uncertainty blocks mutation and reconnect");
        state.MarkUncertain();
        Refuse(true, "A later mutation-only outcome cannot clear whole-session poison");

        state = new WorkerSessionOutcomeState();
        state.MarkUncertain(blockReads: true);
        Refuse(true, "Document batch poison also applies to a previously healthy session");
        Refuse(false, "Document batch poison also blocks the next write");

        state = new WorkerSessionOutcomeState();
        var beforeNewSession = dispatched;
        Dispatch(true);
        Dispatch(false);
        check(dispatched == beforeNewSession + 2, "Only a new explicit session starts usable");
    }
}
