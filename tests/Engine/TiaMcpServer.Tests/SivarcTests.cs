using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using TiaMcpServer.Siemens;

namespace TiaMcpServer.Tests
{
    // Phase 6 ⑥-① (2.7.38) pure logic: SiVArc rule families, rule folder / table requests, rule / group requests with typed
    // properties, references and device columns, block definition requests, expression / layout requests and generation options.
    internal static class SivarcTests
    {
        private static bool Fails<T>(Action a) where T : Exception { try { a(); return false; } catch (T) { return true; } }

        internal static void Run(Action<bool, string> check)
        {
            var temp = Path.GetTempPath();                       // rooted on every OS (offline-checks runs on ubuntu)
            string yml = Path.Combine(temp, "layouts.yml");

            // ---- families / enums ----
            check(SivarcLogic.Categories.Length == 6 && SivarcLogic.AnchorProperty("screens") == "ScreenRules" && SivarcLogic.AnchorProperty("textLists") == "TextlistRules" && SivarcLogic.AnchorProperty("advancedTags") == "AdvancedTagRules", "sivarc: six families map to the Sivarc anchor properties");
            check(Fails<ArgumentException>(() => SivarcLogic.AnchorProperty("faceplates")), "sivarc: unknown category refused");
            check(SivarcLogic.ParseGenerationOptions("AllTags|FullGeneration").SequenceEqual(new[] { "AllTags", "FullGeneration" }) && SivarcLogic.ParseGenerationOptions("").SequenceEqual(new[] { "None" }) && SivarcLogic.ParseGenerationOptions("AllRules, AllRules").Length == 1, "sivarc: generation flags parsed and de-duplicated");
            check(Fails<ArgumentException>(() => SivarcLogic.ParseGenerationOptions("AllTags|Everything")), "sivarc: unknown generation flag refused");
            check(SivarcLogic.ConditionOperators.Length == 8 && SivarcLogic.CreateOptionNames.SequenceEqual(new[] { "Replace", "Rename" }) && SivarcLogic.AcquisitionModes.Length == 4, "sivarc: enum catalogs");

            // ---- tree ----
            SivarcLogic.ValidateTreeRequest("screens", "", "", 4, 0, 200); SivarcLogic.ValidateTreeRequest("tags", "Folder/Sub", "", 16, 0, 1); SivarcLogic.ValidateTreeRequest("alarms", "", "Folder/Table", 1, 10, 500);
            check(true, "sivarc tree: requests accepted");
            check(Fails<ArgumentException>(() => SivarcLogic.ValidateTreeRequest("screens", "F", "F/T", 4, 0, 200)), "sivarc tree: folderPath and tablePath together refused");
            check(Fails<ArgumentException>(() => SivarcLogic.ValidateTreeRequest("screens", "", "", 0, 0, 200)), "sivarc tree: maxDepth 0 refused");
            check(Fails<ArgumentException>(() => SivarcLogic.ValidateTreeRequest("screens", "", "", 4, 0, 501)), "sivarc tree: limit 501 refused");
            check(Fails<ArgumentException>(() => SivarcLogic.ValidateTreeRequest("screens", "a/../b", "", 4, 0, 200)), "sivarc tree: dotted folderPath refused");

            // ---- containers ----
            bool C(string kind, string path, string action, string typePath = "", string typeVersion = "", bool confirm = false, bool dryRun = true) => SivarcLogic.ValidateContainerRequest("screens", kind, path, action, typePath, typeVersion, confirm, dryRun);
            check(!C("folder", "F", "read") && !C("table", "F/T", "create") && C("table", "T", "create", dryRun: false), "sivarc containers: read / preview never write, real create writes");
            check(C("table", "T", "createFromType", "SiVArc/Screen rule table", "0.0.2", dryRun: false), "sivarc containers: createFromType with type path and version");
            check(C("folder", "F", "delete", confirm: true, dryRun: false) && !C("folder", "F", "delete"), "sivarc containers: delete writes only for real");
            check(Fails<ArgumentException>(() => C("folder", "F", "createFromType", "T", "1.0.0")), "sivarc containers: createFromType on a folder refused");
            check(Fails<ArgumentException>(() => C("table", "T", "createFromType", "T")), "sivarc containers: createFromType without version refused");
            check(Fails<ArgumentException>(() => C("table", "T", "create", "T")), "sivarc containers: typePath on create refused");
            check(Fails<ArgumentException>(() => C("table", "T", "delete", dryRun: false)), "sivarc containers: real delete without confirmDelete refused");
            check(Fails<ArgumentException>(() => C("group", "T", "read")), "sivarc containers: unknown kind refused");
            check(Fails<ArgumentException>(() => C("table", "", "read")), "sivarc containers: empty path refused");

            // ---- rules / groups ----
            bool R(string category, string rulePath, string kind, string action, string props = "{}", string refs = "{}", string devices = "{}", string masterCopy = "", string option = "Replace", bool confirm = false, bool dryRun = true)
                => SivarcLogic.ValidateRuleRequest(category, "Default screen rule table", rulePath, kind, action, props, refs, devices, masterCopy, option, confirm, dryRun);
            check(!R("screens", "Rule_1", "rule", "read") && !R("screens", "G/Rule_1", "rule", "update", "{\"Condition\":\"StrComp(Block.Name, \\\"X\\\")\",\"Enabled\":true}"), "sivarc rules: read / preview never write");
            check(R("screens", "G/New", "rule", "create", "{\"LayoutField\":\"Main\",\"LoopCount\":\"3\",\"ConditionOperator\":\"GreaterThan\"}", dryRun: false), "sivarc rules: real create with screen properties writes");
            check(R("screens", "G", "group", "createFromMasterCopy", masterCopy: "Rules/Screen rule_1", option: "Rename", dryRun: false) && R("tags", "", "rule", "createFromMasterCopy", masterCopy: "Tag rule_1", dryRun: false), "sivarc rules: createFromMasterCopy into a group or the table");
            check(R("tags", "Tag rule_2", "rule", "update", "{\"TagGroupHierarchy\":\"HmiTag.DB.FolderPath\",\"TagTable\":\"Hmitag.DB.SymbolicName\"}", dryRun: false), "sivarc rules: tag rule properties");
            check(R("copies", "Copy_1", "rule", "update", "{\"FolderStructure\":\"Block.Name\"}", "{\"LibraryObject\":{\"kind\":\"masterCopy\",\"path\":\"Screens/Button_Lib\"}}", dryRun: false), "sivarc rules: copy rule property and library reference");
            check(R("screens", "Rule_1", "rule", "update", refs: "{\"ProgramBlock\":{\"kind\":\"plcBlock\",\"softwarePath\":\"PLC_1\",\"path\":\"Folder/Block_2\"},\"LibraryScreen\":{\"kind\":\"masterCopyFolder\",\"path\":\"LegacyItems\"},\"ScreenObjectLibraryItem\":null}", devices: "{\"PLC_1\":true,\"HMI_RT_1\":false}", dryRun: false), "sivarc rules: block reference, folder reference, cleared reference and device columns");
            check(R("screens", "G", "group", "update", "{\"LayoutField\":\"Main\"}", "{\"LibraryScreen\":{\"kind\":\"libraryType\",\"path\":\"Screens/Screen_Type\",\"libraryName\":\"GlobalLib\"}}", dryRun: false), "sivarc rules: screen rule group takes layout field and library screen");
            check(R("screens", "Rule_1", "rule", "delete", confirm: true, dryRun: false) && !R("screens", "Rule_1", "rule", "delete", confirm: true), "sivarc rules: delete writes only for real");
            check(Fails<ArgumentException>(() => R("screens", "Rule_1", "rule", "delete", dryRun: false)), "sivarc rules: real delete without confirmDelete refused");
            check(Fails<ArgumentException>(() => R("screens", "Rule_1", "rule", "read", "{\"Enabled\":true}")), "sivarc rules: properties on read refused");
            check(Fails<ArgumentException>(() => R("screens", "New", "rule", "create", "{\"Name\":\"Other\"}")), "sivarc rules: Name on create refused (name is the rulePath)");
            check(Fails<ArgumentException>(() => R("tags", "Rule_1", "rule", "update", "{\"LayoutField\":\"Main\"}")), "sivarc rules: screen-only property on a tag rule refused");
            check(Fails<ArgumentException>(() => R("tags", "G", "group", "update", "{\"TagTable\":\"X\"}")), "sivarc rules: rule-only property on a tag rule group refused");
            check(Fails<ArgumentException>(() => R("screens", "Rule_1", "rule", "update", "{\"ConditionOperator\":\"Between\"}")), "sivarc rules: unknown ConditionOperator refused");
            check(Fails<ArgumentException>(() => R("screens", "Rule_1", "rule", "update", "{\"Condition\":\"" + new string('x', 501) + "\"}")), "sivarc rules: condition over 500 chars refused");
            check(Fails<ArgumentException>(() => R("tags", "Rule_1", "rule", "update", refs: "{\"ProgramBlock\":{\"kind\":\"plcBlock\",\"softwarePath\":\"PLC_1\",\"path\":\"B\"}}")), "sivarc rules: reference on a tag rule refused (no reference properties)");
            check(Fails<ArgumentException>(() => R("alarms", "Rule_1", "rule", "update", refs: "{\"AlarmLibraryItem\":{\"kind\":\"plcBlock\",\"path\":\"B\"}}")), "sivarc rules: plcBlock reference without softwarePath refused");
            check(Fails<ArgumentException>(() => R("alarms", "Rule_1", "rule", "update", refs: "{\"AlarmLibraryItem\":{\"kind\":\"masterCopy\",\"softwarePath\":\"PLC_1\",\"path\":\"B\"}}")), "sivarc rules: softwarePath on a library reference refused");
            check(Fails<ArgumentException>(() => R("alarms", "Rule_1", "rule", "update", refs: "{\"AlarmLibraryItem\":{\"kind\":\"screen\",\"path\":\"B\"}}")), "sivarc rules: unknown reference kind refused");
            check(Fails<ArgumentException>(() => R("tags", "Rule_1", "rule", "update", devices: "{\"PLC_1\":true}")), "sivarc rules: device columns on a tag rule refused (SiVArc raises there)");
            check(Fails<ArgumentException>(() => R("screens", "Rule_1", "rule", "update", devices: "{\"PLC_1\":\"yes\"}")), "sivarc rules: non-boolean device selection refused");
            check(Fails<ArgumentException>(() => R("screens", "Rule_1", "rule", "createFromMasterCopy", masterCopy: "")), "sivarc rules: createFromMasterCopy without masterCopyPath refused");
            check(Fails<ArgumentException>(() => R("screens", "Rule_1", "rule", "create", masterCopy: "MC")), "sivarc rules: masterCopyPath on create refused");
            check(Fails<ArgumentException>(() => R("screens", "Rule_1", "rule", "create", option: "Merge")), "sivarc rules: unknown createOption refused");
            check(Fails<ArgumentException>(() => R("screens", "", "rule", "read")), "sivarc rules: empty rulePath on read refused");
            check(SivarcLogic.ScalarProperties("screens", "group").Contains("LoopCount") && !SivarcLogic.ScalarProperties("copies", "group").Contains("FolderStructure") && SivarcLogic.ReferenceNames("screens", "group").Length == 3 && SivarcLogic.ReferenceNames("alarms", "group").Length == 0, "sivarc rules: property catalogs per family and kind");
            var reference = SivarcLogic.ParseReference(JsonNode.Parse("{\"kind\":\"libraryType\",\"path\":\"F/T\",\"libraryName\":\"Lib\"}")!, "x");
            check(reference.Kind == "libraryType" && reference.Path == "F/T" && reference.LibraryName == "Lib" && reference.SoftwarePath == "", "sivarc rules: reference descriptor parsed");
            check(Fails<ArgumentException>(() => SivarcLogic.ParseNames("[\"A\",\"A\"]", "x")) && SivarcLogic.ParseNames("[\"A\",\"B\"]", "x").Length == 2 && SivarcLogic.ParseNames("", "x").Length == 0, "sivarc: name lists distinct and nonempty");

            // ---- block definitions ----
            bool D(string kind, string name, string action, string props = "{}", string texts = "{}", bool confirm = false, bool dryRun = true) => SivarcLogic.ValidateDefinitionRequest("Folder/Block_1", kind, name, action, props, texts, confirm, dryRun);
            check(!D("tagDefinition", "Tag_definition_1", "read") && D("tagDefinition", "Tag_definition_1", "create", "{\"Value\":\"V\",\"Comment\":\"C\"}", dryRun: false), "sivarc definitions: tag definition create writes");
            check(D("textDefinition", "Text_definition", "update", "{\"Expression\":\"Block.DB.SymbolicName\"}", "{\"de-DE\":\"GermanText\"}", dryRun: false), "sivarc definitions: text definition with multilingual texts");
            check(D("tagDefinition", "T", "delete", confirm: true, dryRun: false) && !D("textDefinition", "T", "delete", confirm: true), "sivarc definitions: delete writes only for real");
            check(!D("tagMemberSettings", "", "read") && D("tagMemberSettings", "", "update", "{\"UseCommonConfiguration\":true}", dryRun: false), "sivarc definitions: tag member settings update");
            check(D("commonParameters", "", "update", "{\"AcquisitionCycle\":\"T1s\",\"AcquisitionMode\":\"CyclicContinuous\"}", dryRun: false) && D("blockParameter", "Input_1", "update", "{\"Comment\":\"c\"}", dryRun: false), "sivarc definitions: common parameters and block parameter update");
            check(Fails<ArgumentException>(() => D("tagMemberSettings", "X", "read")), "sivarc definitions: name on tag member settings refused");
            check(Fails<ArgumentException>(() => D("blockParameter", "", "read")), "sivarc definitions: block parameter without name refused");
            check(Fails<ArgumentException>(() => D("blockParameter", "Input_1", "create")), "sivarc definitions: create of a block parameter refused");
            check(Fails<ArgumentException>(() => D("tagMemberSettings", "", "delete", confirm: true, dryRun: false)), "sivarc definitions: delete of tag member settings refused");
            check(Fails<ArgumentException>(() => D("tagDefinition", "T", "update", "{\"Expression\":\"x\"}")), "sivarc definitions: text property on a tag definition refused");
            check(Fails<ArgumentException>(() => D("tagDefinition", "T", "update", "{\"Value\":\"x\"}", "{\"de-DE\":\"t\"}")), "sivarc definitions: textsJson on a tag definition refused");
            check(Fails<ArgumentException>(() => D("commonParameters", "", "update", "{\"AcquisitionMode\":\"Always\"}")), "sivarc definitions: unknown AcquisitionMode refused");
            check(Fails<ArgumentException>(() => D("tagDefinition", "New", "create", "{\"Name\":\"Other\"}")), "sivarc definitions: Name on create refused");
            check(Fails<ArgumentException>(() => D("tagDefinition", "T", "delete", dryRun: false)), "sivarc definitions: real delete without confirmDelete refused");
            check(Fails<ArgumentException>(() => D("tagDefinition", "T", "read", "{\"Value\":\"x\"}")), "sivarc definitions: properties on read refused");
            check(Fails<ArgumentException>(() => D("cycles", "T", "read")), "sivarc definitions: unknown kind refused");

            // ---- expression resolver ----
            SivarcLogic.ValidateExpressionRequest("Block_2", "[\"HMI_Basic\"]", "[\"HMI_RT_1\"]", "masterCopy", "Buttons/Button_Nested_2", "Block.DB.SymbolicName & \"_\" & HmiApplication.Type", 500);
            check(true, "sivarc expression: request accepted");
            check(Fails<ArgumentException>(() => SivarcLogic.ValidateExpressionRequest("Block_2", "[]", "[\"HMI_RT_1\"]", "masterCopy", "MC", "x", 500)), "sivarc expression: empty device path refused");
            check(Fails<ArgumentException>(() => SivarcLogic.ValidateExpressionRequest("Block_2", "[\"D\"]", "[\"I\"]", "screen", "MC", "x", 500)), "sivarc expression: unknown library item kind refused");
            check(Fails<ArgumentException>(() => SivarcLogic.ValidateExpressionRequest("Block_2", "[\"D\"]", "[\"I\"]", "libraryType", "T", "", 500)), "sivarc expression: empty expression refused");
            check(Fails<ArgumentException>(() => SivarcLogic.ValidateExpressionRequest("Block_2", "[\"D\"]", "[\"I\"]", "libraryType", "T", "x", 0)), "sivarc expression: maxResults 0 refused");

            // ---- layout data ----
            check(!SivarcLogic.ValidateLayoutRequest("Screen_1", "export", yml, false) && !SivarcLogic.ValidateLayoutRequest("/Group/Screen_2", "import", yml, true) && SivarcLogic.ValidateLayoutRequest("Screen_2", "import", yml, false), "sivarc layout: export never writes the project, import only for real");
            check(Fails<ArgumentException>(() => SivarcLogic.ValidateLayoutRequest("Screen_1", "copy", yml, true)), "sivarc layout: unknown action refused");
            check(Fails<ArgumentException>(() => SivarcLogic.ValidateLayoutRequest("Screen_1", "export", "layouts.yml", true)), "sivarc layout: relative file refused");
            check(Fails<ArgumentException>(() => SivarcLogic.ValidateLayoutRequest("", "export", yml, true)), "sivarc layout: empty screen refused");
        }
    }
}
