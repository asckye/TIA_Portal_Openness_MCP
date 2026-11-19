using System;
using System.Linq;
namespace TiaMcp.Adapters.Contracts
{
    public static class UnifiedCapabilities
    {
        public static bool WritesRuntimeSettings(UnifiedRequest request)
            => (request.Operation == "SetUnifiedObjectProperties" || request.Operation == "SetUnifiedMultilingualProperty")
                && request.Inputs.TryGetValue(request.ObjectPathJson, out var address) && address.Error == null
                && address.Items.FirstOrDefault()?.Properties.TryGetValue("property", out var first) == true
                && first.Text == "RuntimeSettings";

        public static bool UsesPreview(string tool) => new[] {
            "SetUnifiedObjectProperties", "SetUnifiedPlantObject", "SetUnifiedMultilingualProperty", "ExportUnifiedEngineeringList",
            "ManageUnifiedPlantNode", "ManageUnifiedLoggingTag", "ManageUnifiedOpcUaAlarmType", "SetUnifiedLogDuration",
            "ImportUnifiedEngineeringList", "ManageUnifiedEngineeringObject", "ManageUnifiedEvent", "ManageUnifiedDynamization",
            "ManageUnifiedObjectParts", "ManageUnifiedScreenLayout", "ManageUnifiedListEntries", "ExchangeUnifiedTags",
            "ExchangeUnifiedScriptModules", "ImportUnifiedOpcUaAlarms", "SetUnifiedGlobalScript", "SetUnifiedRuntimeSettings"
        }.Contains(tool, StringComparer.Ordinal);
        public static string? InvalidRuntimeSettings(UnifiedRequest request)
            => InvalidRuntimeSettings(request, out _);

        public static string? InvalidRuntimeSettings(UnifiedRequest request, out string[] unknownNames)
        {
            unknownNames = Array.Empty<string>();
            if (request.Operation != "GetUnifiedRuntimeSettings" && request.Operation != "SetUnifiedRuntimeSettings") return null;
            var raw = request.Operation == "GetUnifiedRuntimeSettings" ? request.FieldsJson : request.ChangesJson;
            if (!request.Inputs.TryGetValue(raw, out var input)) return null;
            if (input.Error != null) return input.Error;
            return UnifiedRuntimeSettingsFields.Invalid(input.Kind == "array" ? input.Items.Select(v => v.Text) : input.Properties.Keys, out unknownNames);
        }
        public static string? Unsupported(string release, UnifiedRequest request)
        {
            if (!OpennessApi.UnifiedHmi.Includes(release))
                return "WinCC Unified Openness requires V19 or later.";
            if (request.Operation == "SetUnifiedGlobalScript" && request.Action == "create" && !request.DryRun)
                return "Script module creation is not yet verified on real TIA Portal. Create the module in TIA, then use action=update.";
            bool list = request.Operation == "ExportUnifiedEngineeringList" || request.Operation == "ImportUnifiedEngineeringList"
                || request.Operation == "ManageUnifiedListEntries" || request.Operation == "ListUnifiedEngineeringObjects"
                || request.Operation == "ManageUnifiedEngineeringObject";
            if (list && request.Category == "graphicLists" && !OpennessApi.UnifiedGraphicLists.Includes(release))
                return "HmiSoftware.HmiGraphicLists is available in the V21 PublicAPI only.";
            if (request.Operation == "ManageUnifiedEngineeringObject")
            {
                if ((request.Category == "connections" || request.Category == "tagTables") && request.Action != "delete")
                    return "Unified connections/tagTables support delete only; create/update is not supported by this tool.";
                if ((request.Category == "graphicLists" || request.Category == "textLists") && request.Action == "create")
                    return "HmiGraphicListComposition/HmiTextListComposition expose no Create; import a list document with ImportUnifiedEngineeringList.";
            }
            if (!OpennessApi.UnifiedTextLists.Includes(release))
            {
                if (request.Operation == "ExportUnifiedEngineeringList" || request.Operation == "ImportUnifiedEngineeringList"
                    || request.Operation == "ManageUnifiedListEntries")
                    return "HmiUnified.TextGraphicList is not exposed by the V19 PublicAPI.";
                if (list && (request.Category == "textLists" || request.Category == "systemTextLists"))
                    return "HmiTextLists/HmiSystemTextLists require the V20 PublicAPI.";
            }
            if (request.Operation == "GetUnifiedAuditSettings" && request.Category == "alarmAuditClasses" && !OpennessApi.UnifiedAlarmAudit.Includes(release))
                return "HmiSoftware.HmiAlarmAuditClass requires the V20 PublicAPI.";
            if (request.Operation == "ManageUnifiedDynamization" && !OpennessApi.UnifiedExpressionDynamization.Includes(release)
                && (request.DynamizationKind == "Expression" || request.DynamizationKind == "TagParameter"))
                return "ExpressionDynamization and TagParameterDynamization require the V20 PublicAPI.";
            if (request.Operation == "ManageUnifiedListEntries" && request.Action != "read")
                return "Unified list entry Create/Update/Delete is not exposed by the public API; use native file exchange.";
            if (request.Operation == "GetUnifiedRuntimeSettings" || request.Operation == "SetUnifiedRuntimeSettings")
            {
                var raw = request.Operation == "GetUnifiedRuntimeSettings" ? request.FieldsJson : request.ChangesJson;
                if (request.Inputs.TryGetValue(raw, out var input) && input.Error == null)
                {
                    var fields = (input.Kind == "array" ? input.Items.Select(v => v.Text) : input.Properties.Keys).ToArray();
                    foreach (var field in fields)
                        if (UnifiedRuntimeSettingsFields.Unsupported(release, field) is string reason) return reason;
                }
            }
            if (WritesRuntimeSettings(request))
                return "Use SetUnifiedRuntimeSettings for RuntimeSettings and its preview, confirmation and readback flow.";
            foreach (var input in request.Inputs.Values.Where(v => v.Kind == "array" && v.Error == null))
                foreach (var step in input.Items.Where(v => v.Kind == "object" && v.Properties.ContainsKey("property")))
                {
                    var property = step.Properties["property"].Text;
                    if (property == "HmiGraphicLists" && !OpennessApi.UnifiedGraphicLists.Includes(release)
                        || (property == "HmiTextLists" || property == "HmiSystemTextLists") && !OpennessApi.UnifiedTextLists.Includes(release)
                        || property == "HmiAlarmAuditClass" && !OpennessApi.UnifiedAlarmAudit.Includes(release))
                        return "HmiSoftware." + property + " is not exposed by the V" + release + " PublicAPI.";
                }
            return null;
        }
    }
}
