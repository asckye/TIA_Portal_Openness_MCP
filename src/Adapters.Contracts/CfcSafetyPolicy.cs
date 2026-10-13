using System;

namespace TiaMcp.Adapters.Contracts;

public static class CfcSafetyPolicy
{
    public static void RequireSafeChartInventory(int tiaMajor, string purpose)
    {
        if (tiaMajor >= 18)
            throw new NotSupportedException(purpose + " is unavailable: the supplied SDK has no independent CFC chart inventory. CompleteExport preflight on a chartless PLC preceded the V20 finding 45 crash; unknown-chart GetChartProtection and ExportInstructionData crashed V21. This crash guard applies on V18 and later. skipChartPreflight cannot bypass this guard. CFC imports remain available.");
    }
}
