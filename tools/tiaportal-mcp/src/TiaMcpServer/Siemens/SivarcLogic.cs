using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;

namespace TiaMcpServer.Siemens
{
    // Phase 6 ⑥-① (2.7.38): pure logic (no Siemens dependency) for the SiVArc option package - rule families (screen / tag /
    // advanced tag / alarm / copy / text list), rule folders / tables / groups / rules, block tag / text definitions and tag member
    // settings, the expression resolver, screen layout data and the definitions upgrader.
    internal static class SivarcLogic
    {
        internal static string RequireOneOf(string value, string[] allowed, string parameter) => HardwareServicesLogic.RequireOneOf(value, allowed, parameter);
        private static void RequireName(string value, string parameter, int max = 128)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > max || value.Trim() != value) throw new ArgumentException("Exact nonempty " + parameter + " required (max " + max + " chars, no surrounding whitespace).");
        }
        private static void Refuse(string value, string parameter, string reason)
        {
            if (!string.IsNullOrEmpty(value)) throw new ArgumentException(parameter + " " + reason);
        }
        internal static JsonObject ParseObject(string json, string parameter)
            => (string.IsNullOrWhiteSpace(json) ? new JsonObject() : JsonNode.Parse(json) as JsonObject) ?? throw new ArgumentException(parameter + " must be a JSON object.");
        internal static string[] ParseNames(string json, string parameter)
        {
            var array = (string.IsNullOrWhiteSpace(json) ? new JsonArray() : JsonNode.Parse(json) as JsonArray) ?? throw new ArgumentException(parameter + " must be a JSON array of names.");
            var names = array.Select(n => n?.GetValue<string>() ?? "").ToArray();
            if (names.Any(string.IsNullOrWhiteSpace) || names.Distinct(StringComparer.Ordinal).Count() != names.Length) throw new ArgumentException(parameter + " needs distinct nonempty names.");
            return names;
        }

        // ---- rule families -------------------------------------------------------------------------------------------------------
        // category -> Sivarc anchor property; the same six names the reflection-based ReadSiVArcRules / ManageSiVArcRule tools use.
        internal static readonly string[] Categories = { "screens", "tags", "advancedTags", "alarms", "copies", "textLists" };
        internal static string AnchorProperty(string category) => RequireOneOf(category, Categories, "category") switch
        {
            "screens" => "ScreenRules", "tags" => "TagRules", "advancedTags" => "AdvancedTagRules", "alarms" => "AlarmRules", "copies" => "CopyRules", _ => "TextlistRules"
        };
        internal static readonly string[] ConditionOperators = { "None", "And", "Equal", "NotEqual", "GreaterThan", "GreaterThanOrEqual", "LessThan", "LessThanOrEqual" };
        internal static readonly string[] CreateOptionNames = { "Replace", "Rename" };
        internal static readonly string[] GenerationOptionNames = { "None", "AllTags", "UsedHmiTags", "FullGeneration", "UserCreatedRules", "EnergySuiteRules", "AllRules", "AdvancedTags" };
        // "AllTags|FullGeneration" (or comma separated) -> distinct official flag names in the given order.
        internal static string[] ParseGenerationOptions(string value)
        {
            var names = (value ?? "").Split(new[] { '|', ',', ' ' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).Distinct(StringComparer.Ordinal).ToArray();
            if (names.Length == 0) return new[] { "None" };
            foreach (var name in names) RequireOneOf(name, GenerationOptionNames, "generationOptions");
            return names;
        }

        // ---- rule tree -----------------------------------------------------------------------------------------------------------
        internal static void ValidateTreeRequest(string category, string folderPath, string tablePath, int maxDepth, int offset, int limit)
        {
            AnchorProperty(category);
            if (maxDepth < 1 || maxDepth > 16) throw new ArgumentException("maxDepth 1..16 required.");
            HardwareServicesLogic.ValidatePagination(offset, limit);
            if (!string.IsNullOrEmpty(folderPath)) EngineeringGroupOperations.Parts(folderPath);
            if (!string.IsNullOrEmpty(tablePath)) EngineeringGroupOperations.Parts(tablePath);
            if (!string.IsNullOrEmpty(folderPath) && !string.IsNullOrEmpty(tablePath)) throw new ArgumentException("Give folderPath (folder subtree) or tablePath (one table's groups and rules), not both.");
        }

        // ---- folders / tables ------------------------------------------------------------------------------------------------------
        internal static readonly string[] ContainerKinds = { "folder", "table" };
        internal static readonly string[] ContainerActions = { "read", "create", "createFromType", "delete" };
        // Returns true when the request writes the project (real create / createFromType / delete).
        internal static bool ValidateContainerRequest(string category, string kind, string path, string action, string typePath, string typeVersion, bool confirmDelete, bool dryRun)
        {
            AnchorProperty(category); RequireOneOf(kind, ContainerKinds, "kind"); RequireOneOf(action, ContainerActions, "action");
            RequireName(path, "path", 1024); EngineeringGroupOperations.Parts(path);
            if (action == "createFromType")
            {
                if (kind != "table") throw new ArgumentException("createFromType applies to kind table (rule tables are instantiated from a *RuleTableType version).");
                RequireName(typePath, "typePath", 1024); EngineeringGroupOperations.Parts(typePath); RequireName(typeVersion, "typeVersion", 32);
            }
            else { Refuse(typePath, "typePath", "applies to action createFromType only."); Refuse(typeVersion, "typeVersion", "applies to action createFromType only."); }
            if (action == "delete") HardwareServicesLogic.RequireConfirmation(confirmDelete, "confirmDelete", dryRun);
            return action != "read" && !dryRun;
        }

        // ---- rules / groups --------------------------------------------------------------------------------------------------------
        internal static readonly string[] RuleKinds = { "rule", "group" };
        internal static readonly string[] RuleActions = { "read", "create", "createFromMasterCopy", "update", "delete" };
        // Scalar properties per family (rules and groups share the common five; screen rules / groups add layout and loop data).
        internal static readonly string[] CommonRuleProperties = { "Name", "Comment", "Condition", "ConditionOperator", "Enabled" };
        internal static readonly string[] ScreenRuleProperties = { "LayoutField", "LoopCount" };
        internal static readonly string[] TagRuleProperties = { "TagGroupHierarchy", "TagTable" };
        internal static readonly string[] CopyRuleProperties = { "FolderStructure" };
        // Object references per family (rule only unless noted): value is a reference descriptor (see ParseReference) or null to clear.
        internal static readonly Dictionary<string, string[]> ReferenceProperties = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["screens"] = new[] { "ProgramBlock", "LibraryScreen", "ScreenObjectLibraryItem" },   // also on ScreenRuleGroup
            ["tags"] = Array.Empty<string>(),
            ["advancedTags"] = new[] { "ProgramBlock", "TagLibraryItem" },
            ["alarms"] = new[] { "ProgramBlock", "AlarmLibraryItem" },
            ["copies"] = new[] { "LibraryObject" },
            ["textLists"] = new[] { "ProgramBlock", "TextlistLibraryItem" }
        };
        internal static string[] ScalarProperties(string category, string kind)
        {
            var names = new List<string>(CommonRuleProperties);
            if (category == "screens") names.AddRange(ScreenRuleProperties);
            if (kind == "rule" && (category == "tags" || category == "advancedTags")) names.AddRange(TagRuleProperties);
            if (kind == "rule" && category == "copies") names.AddRange(CopyRuleProperties);
            return names.ToArray();
        }
        internal static string[] ReferenceNames(string category, string kind)
            => kind == "rule" || category == "screens" ? ReferenceProperties[category] : Array.Empty<string>();

        internal static bool ValidateRuleRequest(string category, string tablePath, string rulePath, string kind, string action, string propertiesJson, string referencesJson,
            string deviceSelectionJson, string masterCopyPath, string createOption, bool confirmDelete, bool dryRun)
        {
            AnchorProperty(category); RequireOneOf(kind, RuleKinds, "kind"); RequireOneOf(action, RuleActions, "action");
            RequireName(tablePath, "tablePath", 1024); EngineeringGroupOperations.Parts(tablePath);
            RequireOneOf(createOption, CreateOptionNames, "createOption");
            if (action == "createFromMasterCopy")
            {
                // rulePath is the target group path (empty = the table itself); the master copy supplies the name
                if (!string.IsNullOrEmpty(rulePath)) EngineeringGroupOperations.Parts(rulePath);
                RequireName(masterCopyPath, "masterCopyPath", 1024); EngineeringGroupOperations.Parts(masterCopyPath);
            }
            else
            {
                RequireName(rulePath, "rulePath", 1024); EngineeringGroupOperations.Parts(rulePath);
                Refuse(masterCopyPath, "masterCopyPath", "applies to action createFromMasterCopy only.");
            }
            var properties = ParseObject(propertiesJson, "propertiesJson"); var references = ParseObject(referencesJson, "referencesJson"); var devices = ParseObject(deviceSelectionJson, "deviceSelectionJson");
            if (action == "read" || action == "delete" || action == "createFromMasterCopy")
            {
                if (properties.Count > 0 || references.Count > 0 || devices.Count > 0) throw new ArgumentException("propertiesJson / referencesJson / deviceSelectionJson apply to create and update only.");
            }
            else
            {
                var scalars = ScalarProperties(category, kind); var refs = ReferenceNames(category, kind);
                foreach (var pair in properties)
                {
                    if (!scalars.Contains(pair.Key, StringComparer.Ordinal)) throw new ArgumentException("Unknown " + category + " " + kind + " property '" + pair.Key + "'. Allowed: " + string.Join(", ", scalars) + ".");
                    if (pair.Key == "Name" && action == "create") throw new ArgumentException("The new name is the last rulePath segment; do not repeat it in propertiesJson.");
                    if (pair.Key == "ConditionOperator") RequireOneOf(pair.Value?.GetValue<string>() ?? "", ConditionOperators, "ConditionOperator");
                    if ((pair.Key == "Condition" || pair.Key == "Comment") && (pair.Value?.GetValue<string>() ?? "").Length > 500) throw new ArgumentException(pair.Key + " exceeds the 500 character limit SiVArc enforces.");
                    if (pair.Key == "Name" && (pair.Value?.GetValue<string>() ?? "").Length > 128) throw new ArgumentException("Name exceeds the 128 character limit SiVArc enforces.");
                }
                foreach (var pair in references)
                {
                    if (!refs.Contains(pair.Key, StringComparer.Ordinal)) throw new ArgumentException("Unknown " + category + " " + kind + " reference '" + pair.Key + "'. Allowed: " + (refs.Length == 0 ? "(none)" : string.Join(", ", refs)) + ".");
                    if (pair.Value != null) ParseReference(pair.Value, pair.Key);
                }
                foreach (var pair in devices)
                {
                    if (string.IsNullOrWhiteSpace(pair.Key) || !(pair.Value is JsonValue flag && flag.TryGetValue<bool>(out _)))
                        throw new ArgumentException("deviceSelectionJson maps exact PLC / HMI runtime names to true / false.");
                }
                if (devices.Count > 0 && (category == "tags" || category == "textLists")) throw new ArgumentException("Device columns are not supported on tag and text list rules (SiVArc raises a recoverable exception).");
            }
            if (action == "delete") HardwareServicesLogic.RequireConfirmation(confirmDelete, "confirmDelete", dryRun);
            return action != "read" && !dryRun;
        }

        // A reference descriptor: {"kind":"plcBlock","softwarePath":"PLC_1","path":"Folder/Block"} | {"kind":"masterCopy","path":"F/Item","libraryName":""}
        // | {"kind":"libraryType",...} | {"kind":"masterCopyFolder","path":"F"} | {"kind":"typeFolder","path":"F"}; libraryName empty = project library.
        internal static readonly string[] ReferenceKinds = { "plcBlock", "masterCopy", "libraryType", "masterCopyFolder", "typeFolder" };
        internal sealed class Reference
        {
            public string Kind = ""; public string SoftwarePath = ""; public string Path = ""; public string LibraryName = "";
        }
        internal static Reference ParseReference(JsonNode node, string parameter)
        {
            var obj = node as JsonObject ?? throw new ArgumentException(parameter + " must be a reference object or null.");
            var reference = new Reference
            {
                Kind = RequireOneOf(obj["kind"]?.GetValue<string>() ?? "", ReferenceKinds, parameter + ".kind"),
                SoftwarePath = obj["softwarePath"]?.GetValue<string>() ?? "", Path = obj["path"]?.GetValue<string>() ?? "", LibraryName = obj["libraryName"]?.GetValue<string>() ?? ""
            };
            if (reference.Kind == "plcBlock") { RequireName(reference.SoftwarePath, parameter + ".softwarePath", 1024); Refuse(reference.LibraryName, parameter + ".libraryName", "does not apply to plcBlock."); }
            else Refuse(reference.SoftwarePath, parameter + ".softwarePath", "applies to kind plcBlock only.");
            RequireName(reference.Path, parameter + ".path", 1024); EngineeringGroupOperations.Parts(reference.Path);
            return reference;
        }

        // ---- block definitions ---------------------------------------------------------------------------------------------------
        internal static readonly string[] DefinitionKinds = { "tagDefinition", "textDefinition", "tagMemberSettings", "commonParameters", "blockParameter" };
        internal static readonly string[] DefinitionActions = { "read", "create", "update", "delete" };
        internal static readonly string[] TagDefinitionProperties = { "Name", "Value", "Comment" };
        internal static readonly string[] TextDefinitionProperties = { "Name", "Expression", "Comment" };
        internal static readonly string[] TagMemberProperties = { "AcquisitionCycle", "AcquisitionMode", "Comment" };
        internal static readonly string[] TagMemberSettingsProperties = { "UseCommonConfiguration" };
        internal static readonly string[] AcquisitionModes = { "None", "CyclicContinuous", "CyclicInOperation", "OnDemand" };
        internal static string[] DefinitionProperties(string kind) => kind switch
        {
            "tagDefinition" => TagDefinitionProperties, "textDefinition" => TextDefinitionProperties, "tagMemberSettings" => TagMemberSettingsProperties, _ => TagMemberProperties
        };
        internal static bool ValidateDefinitionRequest(string blockPath, string kind, string name, string action, string propertiesJson, string textsJson, bool confirmDelete, bool dryRun)
        {
            RequireName(blockPath, "blockPath", 1024); EngineeringGroupOperations.Parts(blockPath);
            RequireOneOf(kind, DefinitionKinds, "kind"); RequireOneOf(action, DefinitionActions, "action");
            bool named = kind == "tagDefinition" || kind == "textDefinition" || kind == "blockParameter";
            if (named) { RequireName(name, "name", 128); }
            else Refuse(name, "name", "does not apply to " + kind + " (one per block).");
            if ((action == "create" || action == "delete") && (kind != "tagDefinition" && kind != "textDefinition")) throw new ArgumentException(kind + " supports read / update only (" + (kind == "blockParameter" ? "block parameters mirror the block interface" : "one settings object per block") + ").");
            var properties = ParseObject(propertiesJson, "propertiesJson"); var texts = ParseObject(textsJson, "textsJson");
            if (action == "read" || action == "delete") { if (properties.Count > 0 || texts.Count > 0) throw new ArgumentException("propertiesJson / textsJson apply to create and update only."); }
            else
            {
                var allowed = DefinitionProperties(kind);
                foreach (var pair in properties)
                {
                    if (!allowed.Contains(pair.Key, StringComparer.Ordinal)) throw new ArgumentException("Unknown " + kind + " property '" + pair.Key + "'. Allowed: " + string.Join(", ", allowed) + ".");
                    if (pair.Key == "Name" && action == "create") throw new ArgumentException("The new name is the name parameter; do not repeat it in propertiesJson.");
                    if (pair.Key == "AcquisitionMode") RequireOneOf(pair.Value?.GetValue<string>() ?? "", AcquisitionModes, "AcquisitionMode");
                }
                if (texts.Count > 0 && kind != "textDefinition") throw new ArgumentException("textsJson (culture -> text) applies to textDefinition only.");
                foreach (var pair in texts) if (string.IsNullOrWhiteSpace(pair.Key) || !(pair.Value is JsonValue text && text.TryGetValue<string>(out _))) throw new ArgumentException("textsJson maps culture names (de-DE) to strings.");
            }
            if (action == "delete") HardwareServicesLogic.RequireConfirmation(confirmDelete, "confirmDelete", dryRun);
            return action != "read" && !dryRun;
        }

        // ---- expression resolver -------------------------------------------------------------------------------------------------
        internal static readonly string[] LibraryItemKinds = { "masterCopy", "libraryType" };
        internal static void ValidateExpressionRequest(string blockPath, string devicePathJson, string itemPathJson, string libraryItemKind, string libraryItemPath, string expression, int maxResults)
        {
            RequireName(blockPath, "blockPath", 1024); EngineeringGroupOperations.Parts(blockPath);
            if (ParseNames(devicePathJson, "devicePathJson").Length == 0 || ParseNames(itemPathJson, "itemPathJson").Length == 0) throw new ArgumentException("devicePathJson and itemPathJson identify the HMI device item (the PNV HMI device).");
            RequireOneOf(libraryItemKind, LibraryItemKinds, "libraryItemKind"); RequireName(libraryItemPath, "libraryItemPath", 1024); EngineeringGroupOperations.Parts(libraryItemPath);
            if (string.IsNullOrWhiteSpace(expression) || expression.Length > 4000) throw new ArgumentException("expression (SiVArc expression text, max 4000 chars) required.");
            if (maxResults < 1 || maxResults > 5000) throw new ArgumentException("maxResults 1..5000 required.");
        }

        // ---- screen layout data ----------------------------------------------------------------------------------------------------
        internal static readonly string[] LayoutActions = { "export", "import" };
        internal static bool ValidateLayoutRequest(string screenName, string action, string filePath, bool dryRun)
        {
            RequireName(screenName, "screenName", 1024); RequireOneOf(action, LayoutActions, "action");
            if (string.IsNullOrWhiteSpace(filePath) || !Path.IsPathRooted(filePath)) throw new ArgumentException("Absolute filePath (.yml layout file on the TIA Portal machine) required.");
            return action == "import" && !dryRun;
        }
    }
}
