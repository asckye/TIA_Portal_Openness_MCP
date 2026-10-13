using System;
using System.Linq;
namespace TiaMcp.Adapters.Contracts
{
    public static class UnifiedCapabilities
    {
        public static bool UsesPreview(string tool) => new[] {
            "SetUnifiedObjectProperties", "SetUnifiedPlantObject", "SetUnifiedMultilingualProperty", "ExportUnifiedEngineeringList",
            "ManageUnifiedPlantNode", "ManageUnifiedLoggingTag", "ManageUnifiedOpcUaAlarmType", "SetUnifiedLogDuration",
            "ImportUnifiedEngineeringList", "ManageUnifiedEngineeringObject", "ManageUnifiedEvent", "ManageUnifiedDynamization",
            "ManageUnifiedObjectParts", "ManageUnifiedScreenLayout", "ManageUnifiedListEntries", "ExchangeUnifiedTags",
            "ExchangeUnifiedScriptModules", "ImportUnifiedOpcUaAlarms", "SetUnifiedGlobalScript", "SetUnifiedRuntimeSettings"
        }.Contains(tool, StringComparer.Ordinal);
        public static string? Unsupported(string release, UnifiedRequest request)
        {
            if (release != "19" && release != "20" && release != "21")
                return "WinCC Unified Openness requires V19 or later.";
            bool list = request.Operation == "ExportUnifiedEngineeringList" || request.Operation == "ImportUnifiedEngineeringList"
                || request.Operation == "ManageUnifiedListEntries" || request.Operation == "ListUnifiedEngineeringObjects"
                || request.Operation == "ManageUnifiedEngineeringObject";
            if (list && request.Category == "graphicLists" && release != "21")
                return "HmiSoftware.HmiGraphicLists is available in the V21 PublicAPI only.";
            if (release == "19")
            {
                if (request.Operation == "ExportUnifiedEngineeringList" || request.Operation == "ImportUnifiedEngineeringList"
                    || request.Operation == "ManageUnifiedListEntries")
                    return "HmiUnified.TextGraphicList is not exposed by the V19 PublicAPI.";
                if (list && (request.Category == "textLists" || request.Category == "systemTextLists"))
                    return "HmiTextLists/HmiSystemTextLists require the V20 PublicAPI.";
                if (request.Operation == "GetUnifiedAuditSettings" && request.Category == "alarmAuditClasses")
                    return "HmiSoftware.HmiAlarmAuditClass requires the V20 PublicAPI.";
                if (request.Operation == "ManageUnifiedDynamization" && (request.DynamizationKind == "Expression" || request.DynamizationKind == "TagParameter"))
                    return "ExpressionDynamization and TagParameterDynamization require the V20 PublicAPI.";
            }
            if (request.Operation == "ManageUnifiedListEntries" && request.Action != "read")
                return "Unified list entry Create/Update/Delete is not exposed by the public API; use native file exchange.";
            if (request.Operation == "GetUnifiedRuntimeSettings" || request.Operation == "SetUnifiedRuntimeSettings")
            {
                var raw = request.Operation == "GetUnifiedRuntimeSettings" ? request.FieldsJson : request.ChangesJson;
                if (request.Inputs.TryGetValue(raw, out var input) && input.Error == null)
                {
                    var fields = input.Kind == "array" ? input.Items.Select(v => v.Text) : input.Properties.Keys;
                    var missing = release == "19" ? new[] { "CentralInputHint", "CentralPanning", "CentralZooming", "EnableLanguageCompatibleFontFamilies", "GeneralESIGCommentsStrategy" }
                        : release == "20" ? new[] { "CentralInputHint", "CentralPanning", "CentralZooming" } : Array.Empty<string>();
                    var field = fields.FirstOrDefault(f => missing.Contains(f, StringComparer.Ordinal));
                    if (field != null) return "HmiRuntimeSetting." + field + " is not exposed by the V" + release + " PublicAPI.";
                }
            }
            foreach (var input in request.Inputs.Values.Where(v => v.Kind == "array" && v.Error == null))
                foreach (var step in input.Items.Where(v => v.Kind == "object" && v.Properties.ContainsKey("property")))
                {
                    var property = step.Properties["property"].Text;
                    if (property == "HmiGraphicLists" && release != "21" || release == "19" && (property == "HmiTextLists" || property == "HmiSystemTextLists" || property == "HmiAlarmAuditClass"))
                        return "HmiSoftware." + property + " is not exposed by the V" + release + " PublicAPI.";
                }
            return null;
        }
    }
}
