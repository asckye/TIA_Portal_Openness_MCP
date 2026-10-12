using System;

namespace TiaMcp.Adapters.Contracts
{
    public static class UnifiedHmiCapabilities
    {
        // Official V19/V20 metadata lacks these concrete screen items. Their
        // Create<T> overloads and the common button event enum exist on all three releases.
        private static readonly string[] V21ScreenItems = {
            "Widgets.HmiAlarmIndicator", "Controls.HmiAlarmLineControl",
            "Controls.HmiProcessDiagnosisCriteriaAnalysisControl", "Controls.HmiProcessDiagnosisGraphOverviewControl",
            "Controls.HmiProcessDiagnosisOverviewControl", "Controls.HmiProcessDiagnosisPlcCodeViewerControl"
        };

        public static string? Unsupported(string release, string operation, string action, string itemType, string dynamizationType = "")
        {
            if (release == "19" && operation == "EnsureUnifiedHmiDynamization")
                foreach (var name in new[] { "ExpressionDynamization", "TagParameterDynamization" })
                    if (string.Equals(dynamizationType, name, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(dynamizationType, name.Replace("Dynamization", ""), StringComparison.OrdinalIgnoreCase)
                        || string.Equals(dynamizationType, "Siemens.Engineering.HmiUnified.UI.Dynamization." + name, StringComparison.OrdinalIgnoreCase))
                        return "The official V19 Unified API has no " + name + " dynamization type.";
            if (release != "19" && release != "20") return null;
            if (operation != "EnsureUnifiedHmiScreenItem" && operation != "DescribeUnifiedScreenItemType"
                && operation != "ApplyUnifiedHmiScreenDesign" && operation != "ApplyUnifiedHmiLayout"
                && (operation != "ManageUnifiedScreenItem" || action != "create")) return null;
            var key = (itemType ?? "").Trim();
            foreach (var name in V21ScreenItems)
            {
                var shortName = name.Substring(name.LastIndexOf('.') + 1);
                if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(key, "Siemens.Engineering.HmiUnified.UI." + name, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(key, shortName, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(key, shortName.Substring(3), StringComparison.OrdinalIgnoreCase))
                    return "The official V" + release + " Unified API has no " + name + " screen item type.";
            }
            return null;
        }
    }
}
