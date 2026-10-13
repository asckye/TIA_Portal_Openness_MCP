using System;
using TiaMcp.Adapters.Contracts.Candidates;

namespace TiaMcp.Adapters.Contracts;

public static class OnlineOfflinePolicy
{
    public static T WithOfflineRequirement<T>(Func<T> operation, Func<string> state, Action? uncertain = null)
    {
        string observed = state();
        if (observed == "online") CandidatePrimitives.Fail("offline", "selected-PLC");
        if (observed != "offline") CandidatePrimitives.Fail("precondition", "positive-offline-evidence");
        try { return operation(); }
        catch { uncertain?.Invoke(); throw; }
    }
}
