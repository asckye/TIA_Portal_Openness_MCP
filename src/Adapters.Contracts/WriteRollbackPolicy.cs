namespace TiaMcp.Adapters.Contracts
{
    public static class WriteRollbackPolicy
    {
        public static string Outcome(bool restored, bool disconnected, bool uncertainWrite, bool unrestoredWrite,
            bool refused, bool reachable)
        {
            if (disconnected || uncertainWrite || unrestoredWrite) return "unknown";
            if (restored) return "restored";
            return refused && reachable ? "partial" : "unknown";
        }
    }
}
