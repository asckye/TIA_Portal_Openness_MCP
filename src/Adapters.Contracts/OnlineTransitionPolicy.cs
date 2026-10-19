using System;
using System.Collections.Generic;
using System.Linq;

namespace TiaMcp.Adapters.Contracts;

// Pure decisions for online transitions, route application and online compare. The native adapter supplies the observed
// Openness values (states, ApplyConfiguration results, compare states); nothing here touches a Siemens object.
public static class OnlineTransitionPolicy
{
    public const int ConnectWaitMilliseconds = 10000;
    public const int ConnectPollMilliseconds = 250;

    // OnlineProvider.GoOnline returns before the connection is established (Siemens online-and-download skill: poll State until
    // Online). The state is polled while it reads Connecting, bounded by timeoutMs; the last observed state is returned.
    public static string WaitWhileConnecting(string state, Func<string> readState, Action<int> sleep,
        int timeoutMs = ConnectWaitMilliseconds, int pollMs = ConnectPollMilliseconds)
    {
        if (pollMs < 1) throw new ArgumentOutOfRangeException(nameof(pollMs));
        int waited = 0;
        while (string.Equals(state, "Connecting", StringComparison.Ordinal) && waited < timeoutMs)
        {
            sleep(pollMs);
            waited += pollMs;
            state = readState();
        }
        return state;
    }

    // The official sample calls GoOnline only when ConnectionConfiguration.IsConfigured is true. Without a requested route the
    // call is not attempted on an unconfigured connection.
    public static string? ConnectRefusal(bool routeRequested, bool isConfigured)
        => routeRequested || isConfigured ? null
            : "no online route is configured for this PLC in this session (ConnectionConfiguration.IsConfigured=false); "
              + "pass pgPcInterface and ipAddress so the route is applied first.";

    // OnlineProvider.GoOffline returns void, so the state read back after the call decides the result.
    public static (bool Success, string Message) GoOfflineOutcome(string softwarePath, string before, string after)
        => string.Equals(after, "Offline", StringComparison.Ordinal)
            ? (true, string.Equals(before, "Offline", StringComparison.Ordinal)
                ? $"'{softwarePath}' was already offline; the state reads Offline after GoOffline."
                : $"'{softwarePath}' is now offline (state before: {before}).")
            : (false, $"GoOffline returned but '{softwarePath}' reports state {after} (before: {before}); the offline transition is not confirmed.");

    // ConnectionConfiguration.ApplyConfiguration returns a bool and throws while the device is online. A route the caller named
    // (pgPcInterface or target IP address) that is not applied is refused; only an automatic pick may continue unconfirmed.
    public static string? RouteApplyRefusal(bool explicitRoute, bool applied, string? error, string route)
        => applied || !explicitRoute ? null
            : "The requested route was not applied (" + route + "): " + (string.IsNullOrEmpty(error) ? "ConnectionConfiguration.ApplyConfiguration returned false" : error)
              + ". Nothing was sent to the PLC. Check pgPcInterface / the target address; ApplyConfiguration throws while the device is online, so call DisconnectOnlinePlc first in that case.";

    // DownloadPlc applies its route with ConnectionConfiguration.ApplyConfiguration, which throws while the device is online, and
    // DownloadProvider connects by itself. The download therefore starts from Offline (standard CPU: OnlineProvider.State; R/H
    // system: both CPUs). Returns null when every observed state is exactly Offline.
    public static string? DownloadStateRefusal(string softwarePath, IReadOnlyList<string?> states)
    {
        if (states.Count == 0 || states.Any(s => s == null))
            return $"DownloadPlc refused: the online state of '{softwarePath}' could not be observed (no OnlineProvider state), so the route cannot be applied safely.";
        if (states.All(s => string.Equals(s, "Offline", StringComparison.Ordinal))) return null;
        return $"DownloadPlc needs '{softwarePath}' offline (state: {string.Join("/", states)}). DownloadPlc applies its route with "
            + "ConnectionConfiguration.ApplyConfiguration, which throws while the device is online, and connects by itself; call DisconnectOnlinePlc first.";
    }

    // Siemens.Engineering.Compare.CompareResultState: ObjectsIdentical, FolderContentsIdentical and CompareIrrelevant ("Comparison
    // of these 2 objects is irrelevant", 15.1+) are no difference. "Unknown" marks an element whose state could not be read. Every
    // other name, including FolderContentEqualOwnStateDifferent and names a later release may add, is reported as a difference.
    public static bool IsCompareDifference(string state)
        => !(state is "ObjectsIdentical" or "FolderContentsIdentical" or "CompareIrrelevant" or "Unknown");
}
