using TiaOpenness.Shared;
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
        internal static readonly string[] Actions = TiaOpenness.Shared.SafetyPolicy.Actions;
        internal static readonly string[] ConfirmedActions = TiaOpenness.Shared.SafetyPolicy.ConfirmedActions;
        internal static readonly string[] RuntimeGroupActions = TiaOpenness.Shared.SafetyPolicy.RuntimeGroupActions;
        internal static readonly string[] PasswordActions = TiaOpenness.Shared.SafetyPolicy.PasswordActions;
        internal static readonly string[] RuntimeGroupAttributes = TiaOpenness.Shared.SafetyPolicy.RuntimeGroupAttributes;
        internal static readonly string[] SettingsAttributes = TiaOpenness.Shared.SafetyPolicy.SettingsAttributes;
        internal static readonly string[] BlockNumberProperties = TiaOpenness.Shared.SafetyPolicy.BlockNumberProperties;
        internal static readonly string[] GlobalSettingNames = TiaOpenness.Shared.SafetyPolicy.GlobalSettingNames;
        internal static readonly string[] Printers = TiaOpenness.Shared.SafetyPolicy.Printers;
        internal static readonly string[] PrintoutOptions = TiaOpenness.Shared.SafetyPolicy.PrintoutOptions;
        internal const string DefaultDocumentLayout = TiaOpenness.Shared.SafetyPolicy.DefaultDocumentLayout;

        internal static bool ValidateRequest(string action, string runtimeGroup, string password, bool confirmSafetyChange, bool dryRun)
            => TiaOpenness.Shared.SafetyPolicy.ValidateRequest(action, runtimeGroup, password, confirmSafetyChange, dryRun);

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

        internal static TiaOpenness.Shared.SafetyPolicy.PrintoutRequest ValidatePrintoutRequest(string filePath, string printer, string option, string documentLayout)
            => TiaOpenness.Shared.SafetyPolicy.ValidatePrintoutRequest(filePath, printer, option, documentLayout);

        // One signature row; value 0 means "no valid signature" per the official SafetySignatureProvider page.
        internal static JsonObject SignatureRow(string type, ulong value) => new JsonObject
        {
            ["type"] = type, ["value"] = value, ["hex"] = value == 0 ? null : "0x" + value.ToString("X"),
            ["valid"] = value != 0
        };

        internal static string JoinBlockPath(IEnumerable<string> groupNames, string blockName)
            => TiaOpenness.Shared.SafetyPolicy.JoinBlockPath(groupNames, blockName);

        private static JsonObject ParseObject(string propertiesJson)
            => ArgumentRules.ParseObject(propertiesJson, "propertiesJson", ArgumentRules.ObjectRule.Safety);
    }
}
