namespace TiaOpenness.Shared
{
    internal static class SessionBehavior
    {
        internal const string Recovery = "A previous native write has an unknown outcome. Inspect TIA, then DisconnectPortal and establish a new explicit ConnectPortal/AttachOpenProject session. Never replay the failed request.";
        internal static bool LocksSession(bool nativeIssued, bool unknown, bool mutation)
            => nativeIssued && unknown && mutation;
        internal static bool RequiresReset(bool poisoned, bool usesNativeLane) => poisoned && usesNativeLane;
    }
}
