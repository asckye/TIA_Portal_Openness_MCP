using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;

namespace TiaMcpServer.Siemens
{
    // Pure validation/parsing for the Safety tool family (Siemens.Engineering.Safety); no Siemens.Engineering
    // dependency so it is testable offline. Member names below are the official ones from the V20/V21 PublicAPI
    // XML and the "F-related Openness" chapter of the online help.
    internal static class SafetyLogic
    {
        internal static readonly string[] Actions =
        {
            "read", "createRuntimeGroup", "deleteRuntimeGroup", "updateRuntimeGroup", "updateSettings",
            "generateGlobalFIOStatusBlock", "cleanSystemGeneratedObjects", "generateBaseId",
            "login", "logoff", "setPassword", "revokePassword"
        };
        // Native effects that cannot be verified by a readback of this tool and are not undone by it.
        internal static readonly string[] ConfirmedActions =
            { "deleteRuntimeGroup", "generateGlobalFIOStatusBlock", "cleanSystemGeneratedObjects", "generateBaseId", "setPassword", "revokePassword" };
        internal static readonly string[] RuntimeGroupActions = { "createRuntimeGroup", "deleteRuntimeGroup", "updateRuntimeGroup", "generateGlobalFIOStatusBlock" };
        internal static readonly string[] PasswordActions = { "login", "setPassword", "revokePassword" };
        // Dynamic attributes documented for RuntimeGroup ("F-OB Number/Cycle Time/Phase shift/Priority") and
        // SafetySettings ("Enable of consistent upload", "Enable F-Communication ID tag"); not CLR properties.
        internal static readonly string[] RuntimeGroupAttributes = { "FOBNumber", "FOBCycleTime", "FOBPhaseShift", "FOBPriority" };
        internal static readonly string[] SettingsAttributes = { "EnableConsistentUploadFromFCpu", "EnableFCommunicationIdTag" };
        internal static readonly string[] BlockNumberProperties = { "ManagementMode", "FromFB", "ToFB", "FromFC", "ToFC", "FromDB", "ToDB" };
        internal static readonly string[] GlobalSettingNames =
            { "SafetyModificationsPossible", "GenerationOfDefaultFailsafeProgram", "ManagementOfFailsafeInSoftwareUnitsEnvironment", "UsernameForFChangeHistory" };
        internal static readonly string[] Printers = { "MicrosoftPrintToPdf", "MicrosoftXpsDocumentWriter" };
        internal static readonly string[] PrintoutOptions = { "All", "Compact" };
        internal const string DefaultDocumentLayout = "DocuInfo_ISO_A4_Portrait";

        // Returns true when the request performs a real native call (not read, not preview).
        internal static bool ValidateRequest(string action, string runtimeGroup, string password, bool confirmSafetyChange, bool dryRun)
        {
            if (!Actions.Contains(action)) throw new ArgumentException("action must be one of: " + string.Join("/", Actions) + ".");
            if (RuntimeGroupActions.Contains(action) && string.IsNullOrWhiteSpace(runtimeGroup)) throw new ArgumentException("Exact runtimeGroup required for " + action + ".");
            if (!RuntimeGroupActions.Contains(action) && !string.IsNullOrEmpty(runtimeGroup)) throw new ArgumentException("runtimeGroup is only used by runtime-group actions.");
            if (PasswordActions.Contains(action))
            {
                if (string.IsNullOrEmpty(password)) throw new ArgumentException(action + " requires a nonempty password; it is passed to TIA only and never stored or logged.");
                if (password.Length > 256) throw new ArgumentException("Password exceeds 256 characters.");
            }
            else if (!string.IsNullOrEmpty(password)) throw new ArgumentException("password is only used by login/setPassword/revokePassword.");
            if (action == "read" || dryRun) return false;
            if (ConfirmedActions.Contains(action) && !confirmSafetyChange)
                throw new ArgumentException("Real " + action + " requires confirmSafetyChange=true besides dryRun=false.");
            return true;
        }

        internal sealed class SettingsChanges
        {
            public JsonObject Scalars = new JsonObject();          // SafetySettings CLR properties (SafetyModeCanBeDisabled, ...)
            public JsonObject BlockNumbers = new JsonObject();     // AssignmentOfBlockNumbers.*
            public JsonObject Attributes = new JsonObject();       // dynamic SetAttribute names
            public string? SafetySystemVersion;                    // exact SafetySystemVersion.Value, e.g. "V2.4"
            public int Count => Scalars.Count + BlockNumbers.Count + Attributes.Count + (SafetySystemVersion == null ? 0 : 1);
        }

        // Splits updateSettings input into the four native surfaces. Keys: plain SafetySettings property names,
        // "AssignmentOfBlockNumbers.X" or bare block-number names, the two documented attributes, "SafetySystemVersion".
        internal static SettingsChanges SplitSettingsChanges(string propertiesJson)
        {
            var changes = ParseObject(propertiesJson);
            var result = new SettingsChanges();
            foreach (var change in changes)
            {
                var key = change.Key;
                if (key == "Name") throw new ArgumentException("Name is not a Safety setting.");
                if (key == "SafetySystemVersion")
                {
                    var value = change.Value is JsonObject o ? o["Value"] : change.Value;
                    result.SafetySystemVersion = value is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s)
                        ? s : throw new ArgumentException("SafetySystemVersion must be the exact version string (see applicableSafetySystemVersions).");
                    continue;
                }
                if (key == "AssignmentOfBlockNumbers")
                {
                    var nested = change.Value as JsonObject ?? throw new ArgumentException("AssignmentOfBlockNumbers must be an object.");
                    foreach (var inner in nested) AddBlockNumber(result, inner.Key, inner.Value);
                    continue;
                }
                if (key.StartsWith("AssignmentOfBlockNumbers.", StringComparison.Ordinal)) { AddBlockNumber(result, key.Substring("AssignmentOfBlockNumbers.".Length), change.Value); continue; }
                if (BlockNumberProperties.Contains(key)) { AddBlockNumber(result, key, change.Value); continue; }
                if (SettingsAttributes.Contains(key))
                {
                    if (change.Value is not JsonValue b || !b.TryGetValue<bool>(out _)) throw new ArgumentException(key + " must be a boolean.");
                    result.Attributes[key] = change.Value?.DeepClone();
                    continue;
                }
                result.Scalars[key] = change.Value?.DeepClone();
            }
            if (result.Count == 0) throw new ArgumentException("propertiesJson contains no changes.");
            return result;
        }

        private static void AddBlockNumber(SettingsChanges result, string name, JsonNode? value)
        {
            if (!BlockNumberProperties.Contains(name)) throw new ArgumentException("Unknown AssignmentOfBlockNumbers property: " + name);
            if (name == "ManagementMode")
            {
                if (value is not JsonValue m || !m.TryGetValue<string>(out var mode) || (mode != "FSystemManaged" && mode != "FixedRange"))
                    throw new ArgumentException("ManagementMode must be FSystemManaged or FixedRange.");
            }
            else if (value is not JsonValue n || !n.TryGetValue<int>(out var number) || number < 1 || number > 65535)
                throw new ArgumentException(name + " must be an integer block number 1..65535.");
            result.BlockNumbers[name] = value?.DeepClone();
        }

        // updateRuntimeGroup: CLR scalar properties versus the documented FOB* attributes.
        internal static (JsonObject Scalars, JsonObject Attributes) SplitRuntimeGroupChanges(string propertiesJson)
        {
            var changes = ParseObject(propertiesJson);
            var scalars = new JsonObject(); var attributes = new JsonObject();
            foreach (var change in changes)
            {
                if (change.Key == "Name") throw new ArgumentException("Renaming a runtime group is outside this adapter.");
                if (RuntimeGroupAttributes.Contains(change.Key))
                {
                    if (change.Value is not JsonValue n || !n.TryGetValue<int>(out _)) throw new ArgumentException(change.Key + " must be an integer.");
                    attributes[change.Key] = change.Value?.DeepClone();
                }
                else scalars[change.Key] = change.Value?.DeepClone();
            }
            if (scalars.Count + attributes.Count == 0) throw new ArgumentException("propertiesJson contains no changes.");
            return (scalars, attributes);
        }

        // ManageSafetyGlobalSettings update: the four documented GlobalSettings setters.
        internal static List<KeyValuePair<string, object>> ParseGlobalSettingChanges(string propertiesJson)
        {
            var changes = ParseObject(propertiesJson);
            var result = new List<KeyValuePair<string, object>>();
            foreach (var change in changes)
            {
                if (!GlobalSettingNames.Contains(change.Key)) throw new ArgumentException("Unknown GlobalSettings name: " + change.Key + ". Allowed: " + string.Join(", ", GlobalSettingNames));
                if (change.Key == "UsernameForFChangeHistory")
                {
                    var text = change.Value == null ? "" : change.Value is JsonValue v && v.TryGetValue<string>(out var s) ? s : throw new ArgumentException("UsernameForFChangeHistory must be a string (empty resets to default).");
                    if (text.Length > 256) throw new ArgumentException("UsernameForFChangeHistory is limited to 256 characters by TIA; longer input is refused rather than silently cut.");
                    result.Add(new KeyValuePair<string, object>(change.Key, text));
                }
                else
                {
                    if (change.Value is not JsonValue b || !b.TryGetValue<bool>(out var flag)) throw new ArgumentException(change.Key + " must be a boolean.");
                    result.Add(new KeyValuePair<string, object>(change.Key, flag));
                }
            }
            if (result.Count == 0) throw new ArgumentException("propertiesJson contains no changes.");
            return result;
        }

        internal sealed class PrintoutRequest { public string Printer = ""; public string Option = ""; public string Layout = ""; public string Extension = ""; }

        // ExportSafetyPrintout: printer/option are official enum names; the file extension must match the printer driver.
        internal static PrintoutRequest ValidatePrintoutRequest(string filePath, string printer, string option, string documentLayout)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !Path.IsPathRooted(filePath)) throw new ArgumentException("Absolute output filePath required (native Print overwrites; this tool refuses existing files).");
            if (!Printers.Contains(printer)) throw new ArgumentException("printer must be MicrosoftPrintToPdf or MicrosoftXpsDocumentWriter.");
            if (!PrintoutOptions.Contains(option)) throw new ArgumentException("option must be All or Compact.");
            var layout = string.IsNullOrWhiteSpace(documentLayout) ? DefaultDocumentLayout : documentLayout.Trim();
            if (layout.Length > 128 || layout.Any(c => char.IsControl(c) || c == '"')) throw new ArgumentException("documentLayout must be a plain layout name such as DocuInfo_ISO_A4_Portrait.");
            var extension = Path.GetExtension(filePath).ToLowerInvariant();
            bool ok = printer == "MicrosoftPrintToPdf" ? extension == ".pdf" : extension == ".xps" || extension == ".oxps";
            if (!ok) throw new ArgumentException(printer + " writes " + (printer == "MicrosoftPrintToPdf" ? ".pdf" : ".xps/.oxps") + "; filePath extension is '" + extension + "'.");
            return new PrintoutRequest { Printer = printer, Option = option, Layout = layout, Extension = extension };
        }

        // One signature row; value 0 means "no valid signature" per the official SafetySignatureProvider page.
        internal static JsonObject SignatureRow(string type, ulong value) => new JsonObject
        {
            ["type"] = type, ["value"] = value, ["hex"] = value == 0 ? null : "0x" + value.ToString("X"),
            ["valid"] = value != 0
        };

        // Relative block path segments joined with '/', root group omitted.
        internal static string JoinBlockPath(IEnumerable<string> groupNames, string blockName)
        {
            var parts = groupNames.ToList(); parts.Add(blockName);
            return string.Join("/", parts);
        }

        private static JsonObject ParseObject(string propertiesJson)
        {
            if (string.IsNullOrWhiteSpace(propertiesJson) || propertiesJson.Length > 16384) throw new ArgumentException("propertiesJson must be a JSON object (<= 16 KB).");
            var node = JsonNode.Parse(propertiesJson) as JsonObject ?? throw new ArgumentException("propertiesJson must be a JSON object.");
            if (node.Count > 50) throw new ArgumentException("At most 50 properties per request.");
            return node;
        }
    }
}
