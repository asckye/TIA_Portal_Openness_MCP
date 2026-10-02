using System;
using TiaMcp.PlcFoundation;

// Covers the EXISTING pure policy only. Does not claim to test native service discovery.
internal static class OfflineCoverageBoundaryTests
{
    internal static void Run(Action<bool, string> check)
    {
        void Reject(string?[] states, bool covered, Type expected, string label)
        {
            Exception? failure = null;
            try { PlcOfflinePolicy.RequireStates(states, covered, "R/H fake evidence"); }
            catch (Exception ex) { failure = ex; }
            check(failure != null && failure.GetType() == expected, label);
        }
        Reject(new[] { "Offline" }, false, typeof(NotSupportedException), "Ordinary Offline does not rescue explicitly incomplete R/H coverage");
        Reject(new[] { "Offline", "Offline" }, false, typeof(NotSupportedException), "Even two Offline strings cannot override incomplete coverage");
        Reject(Array.Empty<string?>(), true, typeof(NotSupportedException), "Empty state evidence is rejected");
        Reject(new string?[] { "Offline", null }, true, typeof(InvalidOperationException), "Unknown backup is rejected");
        Reject(new string?[] { null, "Offline" }, true, typeof(InvalidOperationException), "Unknown primary is rejected");
        Reject(new[] { "Offline", "Online" }, true, typeof(InvalidOperationException), "Online backup is rejected");
        Reject(new[] { "Online", "Offline" }, true, typeof(InvalidOperationException), "Online primary is rejected");
        PlcOfflinePolicy.RequireStates(new[] { "Offline", "Offline" }, true, "R/H fake evidence");
        check(true, "Complete two-sided Offline evidence accepted");
    }
}
