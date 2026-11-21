using System;

namespace TiaMcp.Adapters.Contracts;

public static class CfcSafetyPolicy
{
    public static void RequireSafeChartInventory(int tiaMajor, string purpose)
    {
        if (ChartInventoryProblem(tiaMajor, purpose) is string reason) throw new NotSupportedException(reason);
    }

    public static string? ChartInventoryProblem(int tiaMajor, string purpose) => tiaMajor < 18 ? null : purpose
        + " is unavailable: the supplied SDK has no independent CFC chart inventory. CompleteExport preflight on a chartless PLC preceded the " +
        "V20 finding 45 crash; unknown-chart GetChartProtection and ExportInstructionData crashed V21. This crash guard applies on V18 and " +
        "later. skipChartPreflight cannot bypass this guard. CFC imports remain available.";
}
