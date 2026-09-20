using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace TiaMcpServer.Siemens
{
    // Phase 6 ⑥-③ (2.7.42): pure logic (no Siemens dependency) for the Safety Validation Assistant option package
    // (V21 Siemens.Engineering.SafetyValidation.dll; absent on V20): activation tests and their user groups, safety functions
    // (test cases) with conditions and trace configuration, validity checks, reports and XML exchange.
    internal static class SafetyValidationLogic
    {
        internal static string RequireOneOf(string value, string[] allowed, string parameter) => HardwareServicesLogic.RequireOneOf(value, allowed, parameter);
        private static void RequireText(string value, string parameter, int max = 256)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > max || value.Trim() != value) throw new ArgumentException("Exact nonempty " + parameter + " required (max " + max + " chars, no surrounding whitespace).");
        }
        private static void Refuse(string value, string parameter, string reason)
        {
            if (!string.IsNullOrEmpty(value)) throw new ArgumentException(parameter + " " + reason);
        }
        internal static JsonObject ParseObject(string json, string parameter) => SivarcLogic.ParseObject(json, parameter);
        internal static string[] ParseGroupPath(string json)
        {
            var array = (string.IsNullOrWhiteSpace(json) ? new JsonArray() : JsonNode.Parse(json) as JsonArray) ?? throw new ArgumentException("groupPathJson must be a JSON array of activation test group names (outermost first).");
            var names = array.Select(n => n?.GetValue<string>() ?? "").ToArray();
            if (names.Any(n => string.IsNullOrWhiteSpace(n) || n.Trim() != n)) throw new ArgumentException("groupPathJson needs exact nonempty group names.");
            return names;
        }

        // ---- official enum names (pinned member by member by SafetyValidationShapeChecks) ---------------------------------------
        internal static readonly string[] ExportOptions = { "None", "WithDefaults", "WithReadOnly" };                                   // Siemens.Engineering.ExportOptions
        internal static readonly string[] DocumentInfoOptions = { "None", "ExportSetting", "InstalledProducts", "CreatedTimeStamp", "All" }; // Siemens.Engineering.DocumentInfoOptions
        internal static readonly string[] ImportOptions = { "None", "Override", "SkipInactiveCultures", "ActivateInactiveCultures" };   // Siemens.Engineering.ImportOptions (SafetyFunctionComposition.Import)
        internal static readonly string[] ActivationTestImportOptions = { "None", "Override", "Rename", "SkipInactiveCultures", "ActivateInactiveCultures" };
        internal static readonly string[] SignalUsages = { "OperatingMode", "InputCondition", "Response" };
        internal static readonly string[] TestStates = { "NotTested", "Succeeded", "Failed" };
        internal static readonly string[] ConditionValues = { "FALSE", "TRUE", "NotRelevant" };
        internal static readonly string[] ValidationStates = { "Okay", "Error" };

        internal static readonly string[] ActivationTestActions = { "read", "create", "createFromTest", "createFromMasterCopy", "rename", "setAuthor", "changeEvaluationDevice", "checkValidity", "generateReport", "export", "import", "delete" };
        internal static readonly string[] GroupActions = { "read", "create", "createFromMasterCopy", "rename", "delete" };
        internal static readonly string[] SafetyFunctionActions = { "read", "create", "createFrom", "update", "resetTestResult", "checkValidity", "setTrace", "checkTraceValidity", "export", "import", "delete" };
        internal static readonly string[] ConditionActions = { "read", "create", "update", "checkValidity", "delete" };

        // Flags may be combined with | (ExportOptions.WithDefaults | WithReadOnly; DocumentInfoOptions.ExportSetting | CreatedTimeStamp).
        internal static string[] ParseFlags(string value, string[] allowed, string parameter)
        {
            var parts = (string.IsNullOrWhiteSpace(value) ? "None" : value).Split(new[] { '|', ',' }, StringSplitOptions.RemoveEmptyEntries).Select(p => p.Trim()).ToArray();
            if (parts.Length == 0 || parts.Distinct(StringComparer.Ordinal).Count() != parts.Length) throw new ArgumentException(parameter + " must list distinct " + string.Join("/", allowed) + " names joined with |.");
            foreach (var part in parts) RequireOneOf(part, allowed, parameter);
            return parts;
        }

        // ---- activation tests ---------------------------------------------------------------------------------------------------
        internal sealed class ActivationTestRequest
        {
            public string Action = ""; public bool Writes; public bool WritesFile; public string[] ExportFlags = Array.Empty<string>(); public string[] DocumentFlags = Array.Empty<string>(); public string ImportOption = "None";
        }
        internal static ActivationTestRequest ValidateActivationTestRequest(string action, string name, string evaluationDeviceName, string sourceName, string masterCopyPath, string newValue, string filePath, string exportOptions, string documentInfoOptions, string importOptions, bool confirmDelete, bool dryRun)
        {
            RequireOneOf(action, ActivationTestActions, "action");
            var r = new ActivationTestRequest { Action = action };
            if (action != "import") RequireText(name, "name");
            else Refuse(name, "name", "is not used by import (the XML file names the activation tests).");
            if (action == "create" || action == "changeEvaluationDevice") RequireText(evaluationDeviceName, "evaluationDeviceName");
            else Refuse(evaluationDeviceName, "evaluationDeviceName", "applies to create / changeEvaluationDevice only.");
            if (action == "createFromTest") RequireText(sourceName, "sourceName"); else Refuse(sourceName, "sourceName", "applies to createFromTest only.");
            if (action == "createFromMasterCopy") RequireText(masterCopyPath, "masterCopyPath", 1024); else Refuse(masterCopyPath, "masterCopyPath", "applies to createFromMasterCopy only.");
            if (action == "rename" || action == "setAuthor") RequireText(newValue, "newValue"); else Refuse(newValue, "newValue", "applies to rename / setAuthor only.");
            if (action == "generateReport" || action == "export" || action == "import") RequireText(filePath, "filePath", 1024); else Refuse(filePath, "filePath", "applies to generateReport / export / import only.");
            if (action == "export") { r.ExportFlags = ParseFlags(exportOptions, ExportOptions, "exportOptions"); r.DocumentFlags = ParseFlags(documentInfoOptions, DocumentInfoOptions, "documentInfoOptions"); }
            else { Refuse(exportOptions, "exportOptions", "applies to export only."); Refuse(documentInfoOptions, "documentInfoOptions", "applies to export only."); }
            if (action == "import") r.ImportOption = RequireOneOf(string.IsNullOrEmpty(importOptions) ? "None" : importOptions, ActivationTestImportOptions, "importOptions");
            else Refuse(importOptions, "importOptions", "applies to import only.");
            if (action == "delete") HardwareServicesLogic.RequireConfirmation(confirmDelete, "confirmDelete", dryRun);
            r.WritesFile = (action == "generateReport" || action == "export") && !dryRun;
            r.Writes = action != "read" && action != "checkValidity" && action != "generateReport" && action != "export" && !dryRun;
            return r;
        }
        internal sealed class GroupRequest { public string Action = ""; public bool Writes; }
        internal static GroupRequest ValidateGroupRequest(string action, string name, string masterCopyPath, string newName, bool confirmDelete, bool dryRun)
        {
            RequireOneOf(action, GroupActions, "action");
            if (action != "read" || !string.IsNullOrEmpty(name)) RequireText(name, "name");
            if (action == "createFromMasterCopy") RequireText(masterCopyPath, "masterCopyPath", 1024); else Refuse(masterCopyPath, "masterCopyPath", "applies to createFromMasterCopy only.");
            if (action == "rename") RequireText(newName, "newName"); else Refuse(newName, "newName", "applies to rename only.");
            if (action == "delete") HardwareServicesLogic.RequireConfirmation(confirmDelete, "confirmDelete", dryRun);
            return new GroupRequest { Action = action, Writes = action != "read" && !dryRun };
        }

        // ---- safety functions ---------------------------------------------------------------------------------------------------
        internal static readonly string[] SafetyFunctionProperties = { "testName", "description" };              // SafetyFunction.TestName / Description (Name is generated, read-only)
        internal static readonly string[] TraceProperties = { "isTraced", "pretriggerTime", "recordingDuration", "signals" }; // TraceConfiguration.IsTraced + dynamic attributes (official example)
        internal sealed class SafetyFunctionRequest
        {
            public string Action = ""; public bool Writes; public bool WritesFile; public JsonObject Properties = new JsonObject(); public string[] ExportFlags = Array.Empty<string>(); public string[] DocumentFlags = Array.Empty<string>(); public string ImportOption = "None";
        }
        internal static SafetyFunctionRequest ValidateSafetyFunctionRequest(string action, string name, string sourceName, string propertiesJson, string filePath, string exportOptions, string documentInfoOptions, string importOptions, bool confirmDelete, bool dryRun)
        {
            RequireOneOf(action, SafetyFunctionActions, "action");
            var r = new SafetyFunctionRequest { Action = action, Properties = ParseObject(propertiesJson, "propertiesJson") };
            bool needsName = action != "read" && action != "create" && action != "createFrom" && action != "import";
            if (needsName) RequireText(name, "name"); else if (!string.IsNullOrEmpty(name)) RequireText(name, "name");
            if (action == "createFrom") RequireText(sourceName, "sourceName"); else Refuse(sourceName, "sourceName", "applies to createFrom only.");
            string[] allowedKeys = action == "setTrace" ? TraceProperties : action == "create" || action == "update" ? SafetyFunctionProperties : Array.Empty<string>();
            var unknown = r.Properties.Select(p => p.Key).Where(k => !allowedKeys.Contains(k, StringComparer.Ordinal)).ToArray();
            if (unknown.Length > 0) throw new ArgumentException("propertiesJson keys not allowed for " + action + ": " + string.Join(", ", unknown) + (allowedKeys.Length == 0 ? "" : " (allowed: " + string.Join(", ", allowedKeys) + ")."));
            if ((action == "update" || action == "setTrace") && r.Properties.Count == 0) throw new ArgumentException("propertiesJson must name at least one property for " + action + ".");
            if (action == "export" || action == "import") RequireText(filePath, "filePath", 1024); else Refuse(filePath, "filePath", "applies to export / import only.");
            if (action == "export") { r.ExportFlags = ParseFlags(exportOptions, ExportOptions, "exportOptions"); r.DocumentFlags = ParseFlags(documentInfoOptions, DocumentInfoOptions, "documentInfoOptions"); }
            else { Refuse(exportOptions, "exportOptions", "applies to export only."); Refuse(documentInfoOptions, "documentInfoOptions", "applies to export only."); }
            if (action == "import") r.ImportOption = RequireOneOf(string.IsNullOrEmpty(importOptions) ? "None" : importOptions, ImportOptions, "importOptions");
            else Refuse(importOptions, "importOptions", "applies to import only.");
            if (action == "delete") HardwareServicesLogic.RequireConfirmation(confirmDelete, "confirmDelete", dryRun);
            r.WritesFile = action == "export" && !dryRun;
            r.Writes = action != "read" && action != "checkValidity" && action != "checkTraceValidity" && action != "export" && !dryRun;
            return r;
        }

        // ---- conditions ---------------------------------------------------------------------------------------------------------
        internal static readonly string[] ConditionProperties = { "comment", "deviceName", "signalName", "signalUsage", "initialInput", "executedInput", "response" };
        internal sealed class ConditionRequest { public string Action = ""; public bool Writes; public JsonObject Properties = new JsonObject(); }
        internal static ConditionRequest ValidateConditionRequest(string action, int index, string deviceName, string signalUsage, string signalName, string propertiesJson, bool dryRun)
        {
            RequireOneOf(action, ConditionActions, "action");
            var r = new ConditionRequest { Action = action, Properties = ParseObject(propertiesJson, "propertiesJson") };
            if (index < -1) throw new ArgumentException("index must be -1 (unused) or the position in SafetyFunction.Conditions.");
            if (action == "create") { if (index >= 0) throw new ArgumentException("index does not apply to create."); RequireText(deviceName, "deviceName"); RequireOneOf(signalUsage, SignalUsages, "signalUsage"); RequireText(signalName, "signalName"); }
            else
            {
                if (action != "read" && index < 0) throw new ArgumentException("index (position in Conditions) is required for " + action + ".");
                Refuse(deviceName, "deviceName", "applies to create only (use propertiesJson.deviceName for update)."); Refuse(signalUsage, "signalUsage", "applies to create only (use propertiesJson.signalUsage for update)."); Refuse(signalName, "signalName", "applies to create only (use propertiesJson.signalName for update).");
            }
            var unknown = r.Properties.Select(p => p.Key).Where(k => !ConditionProperties.Contains(k, StringComparer.Ordinal)).ToArray();
            if (unknown.Length > 0) throw new ArgumentException("propertiesJson keys not allowed: " + string.Join(", ", unknown) + " (allowed: " + string.Join(", ", ConditionProperties) + ").");
            if (action != "create" && action != "update" && r.Properties.Count > 0) throw new ArgumentException("propertiesJson applies to create / update only.");
            if (action == "update" && r.Properties.Count == 0) throw new ArgumentException("propertiesJson must name at least one property for update.");
            if (r.Properties["signalUsage"] is JsonValue usage) RequireOneOf(usage.GetValue<string>(), SignalUsages, "propertiesJson.signalUsage");
            foreach (var key in new[] { "initialInput", "executedInput", "response" }) if (r.Properties.ContainsKey(key)) ParseConditionValue(r.Properties[key], key);
            if (action == "create") ValidateConditionValues(signalUsage, r.Properties);
            else if (r.Properties["signalUsage"] is JsonValue newUsage) ValidateConditionValues(newUsage.GetValue<string>(), r.Properties);   // update without signalUsage: checked against the current usage by the portal
            r.Writes = action != "read" && action != "checkValidity" && !dryRun;
            return r;
        }
        // Which of the three inputs a signal usage owns (official examples: OperatingMode -> ExecutedInput, InputCondition -> InitialInput +
        // ExecutedInput, Response -> Response). 2.7.42 real project: TIA refuses NotRelevant on an owned input ("'NotRelevant' is not
        // supported when the signal usage is 'InputCondition'") - and the refusal came after Comment / SignalName had already been written,
        // leaving the condition half updated; the following condition-level CheckValidity took TIA Portal V21 down. So the request is
        // validated here before anything is written.
        internal static string[] OwnedInputs(string signalUsage) => signalUsage switch { "OperatingMode" => new[] { "executedInput" }, "InputCondition" => new[] { "initialInput", "executedInput" }, "Response" => new[] { "response" }, _ => Array.Empty<string>() };
        internal static void ValidateConditionValues(string signalUsage, JsonObject properties)
        {
            foreach (var key in OwnedInputs(signalUsage))
                if (properties.ContainsKey(key) && ParseConditionValue(properties[key], key).EnumName == "NotRelevant")
                    throw new ArgumentException("propertiesJson." + key + " cannot be NotRelevant while the signal usage is " + signalUsage + " (TIA refuses it natively; use true/false).");
        }
        // Condition.InitialInput / ExecutedInput / Response are typed Object: the official examples assign bool, the API also defines
        // ConditionValue (FALSE / TRUE / NotRelevant). JSON true/false -> bool, "TRUE"/"FALSE"/"NotRelevant" -> enum name.
        internal static (bool? Flag, string? EnumName) ParseConditionValue(JsonNode? node, string key)
        {
            if (node is JsonValue v)
            {
                if (v.TryGetValue<bool>(out var flag)) return (flag, null);
                if (v.TryGetValue<string>(out var text)) return (null, RequireOneOf(text, ConditionValues, "propertiesJson." + key));
            }
            throw new ArgumentException("propertiesJson." + key + " must be true/false or one of " + string.Join("/", ConditionValues) + ".");
        }
    }
}
