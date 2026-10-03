using TiaMcp.PlcWorker;

internal static class WorkerSessionOutcomeTests
{
    internal static void Run(Action<bool, string> check)
    {
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

        // Older XML batch candidates explicitly retain read-only inspection.
        state.MarkUncertain();
        Dispatch(true);
        check(dispatched == 3, "XML batch uncertainty still permits read-only inspection");
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
