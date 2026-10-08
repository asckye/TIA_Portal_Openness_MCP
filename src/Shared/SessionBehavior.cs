namespace TiaOpenness.Shared
{
    internal static class SessionBehavior
    {
        internal const string Recovery = "A previous native operation has an unknown outcome. Inspect TIA, then call DisconnectPortal. An idle responsive worker detaches cleanly; a timed-out, busy or lost worker is terminated and TIA must be restarted before reconnecting. Start a new MCP session, then explicitly call ConnectPortal and AttachOpenProject. Never replay the failed request.";
        internal const string TiaRestartRequired = "The worker could not acknowledge a clean detach and was terminated. Native outcome remains unknown. Inspect TIA and restart that TIA instance before reconnecting; never replay the failed request.";
        internal const string DetachedAfterUnknown = "The idle worker acknowledged a non-owning detach. Inspect the prior unknown outcome, then start a new MCP session and explicitly call ConnectPortal and AttachOpenProject; never replay the failed request.";
        // The process lease refusals are authored admission text: the host passes them through to the caller.
        internal const string LeaseNotReleased = "The prior MCP owner did not release this TIA instance cleanly. Native outcome is unknown. Inspect diagnostics and restart that TIA instance before reconnecting; do not erase the lease to bypass this guard.";
        internal const string LeaseReserved = "TIA instance is already reserved by another MCP, or its lease is inaccessible. Use a separate TIA instance; no attachment attempted.";
        internal static bool IsRecoveryTool(string name) => name == "DisconnectPortal" || name == "RestartOpennessWorker";
        internal static bool LocksSession(bool nativeIssued, bool unknown, bool mutation)
            => nativeIssued && unknown && mutation;
        internal static bool RequiresReset(bool poisoned, bool usesNativeLane) => poisoned && usesNativeLane;
    }
}
