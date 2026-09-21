using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TiaMcpServer.ModelContextProtocol
{
    // 2.7.57: one worked call per frequently used tool. The maintainer's complaint was that an AI caller, once a
    // call is refused, tries variants against TIA instead of re-planning; a concrete example next to the signature
    // removes most of the guessing. The table is the single source: it is appended to the protocol description of
    // every listed tool (McpServer.Profile.cs), printed by FindTools / PreflightToolCall / CallTool refusals, and
    // validated against the real signatures by the tools-list build gate (Generate-ToolsListFromAssembly.ps1 calls
    // ValidateAgainst) so an example can never drift away from its tool. Zero dependencies: linked into the offline suite.
    public static class ToolExamples
    {
        public sealed class Example
        {
            public string Tool { get; }
            public string ArgumentsJson { get; }
            public string Note { get; }
            public Example(string tool, string argumentsJson, string note) { Tool = tool; ArgumentsJson = argumentsJson; Note = note; }
        }

        // Single quotes become double quotes so the JSON stays readable in source; a JSON string that itself carries
        // JSON (the string-typed *Json / json / spec parameters of directly callable tools) keeps its escaped quotes.
        private static Example E(string tool, string json, string note = "") => new Example(tool, json.Replace('\'', '"'), note);

        private static readonly Example[] Rows =
        {
            // ---- session / meta
            E("FindTools", @"{'query':'watch table','limit':8}", "capability words, not exact names"),
            E("CallTool", @"{'name':'ExportPlcWatchTable','argumentsJson':{'softwarePath':'PLC_1','watchTableName':'WT1','exportPath':'C:\\Temp\\WT1.xml'}}", "argumentsJson may be the object itself or that object as a JSON string"),
            E("PreflightToolCall", @"{'name':'DownloadToPlc','argumentsJson':{'softwarePath':'PLC_1','pgPcInterface':'PLCSIM','targetIpAddress':'192.168.0.3'}}", "nothing is executed"),
            E("Doctor", @"{'fix':false}", "fix=false is a read-only diagnosis"),
            E("GetAuthoringGuide", @"{'topic':'scl'}", "topics: workflow | scl | lad | db | hmi | errors"),
            E("GenerateAcceptanceReport", @"{'outputDirectory':'C:\\Temp\\mcp-report','includeProjectTree':true}"),
            E("GenerateErrorReport", @"{'errorCode':'CompileError','summary':'FB_Pump: 2 errors','detail':'<compiler output>','severity':'error'}"),
            E("RunCapabilitySelfTest", @"{'expectedPlcSoftwarePath':'PLC_1','expectedHmiSoftwarePath':'HMI_RT_1'}"),
            E("Connect", @"{'projectName':'项目1'}", "attaches to the TIA process holding that project; never starts a new instance while one runs"),
            E("OpenProject", @"{'path':'C:\\Projects\\Demo\\Demo.ap21'}"),
            E("AttachToOpenProject", @"{'projectName':'项目1'}"),
            E("CreateProject", @"{'directoryPath':'C:\\Projects','projectName':'Demo'}"),
            E("ValidateAutomationContext", @"{'expectedPlcSoftwarePath':'PLC_1','expectedHmiSoftwarePath':''}", "empty HMI path skips the HMI check"),
            E("ReadPortalInfo", @"{'includeProcesses':true,'includeSessions':true,'includeProducts':false}"),
            E("CheckForUpdate", @"{}", "read-only; the update itself is scripts/operations/Update-Engine.ps1 with the engine stopped"),
            // ---- project / software reading
            E("GetSoftwareInfo", @"{'softwarePath':'PLC_1'}"),
            E("GetSoftwareTree", @"{'softwarePath':'PLC_1'}"),
            E("GetPlcTagTables", @"{'softwarePath':'PLC_1'}"),
            E("GetBlocksWithHierarchy", @"{'softwarePath':'PLC_1'}"),
            E("GetBlocks", @"{'softwarePath':'PLC_1','regexName':'FB_.*'}", "empty regexName lists every block"),
            E("GetBlockInfo", @"{'softwarePath':'PLC_1','blockPath':'Main'}", "blockPath is Group/Subgroup/Name from GetSoftwareTree"),
            E("DescribeBlockLogic", @"{'softwarePath':'PLC_1','blockPath':'Main'}"),
            E("GetCrossReferences", @"{'softwarePath':'PLC_1','objectPath':'Main','objectKind':'Block','filter':'AllObjects'}"),
            E("GetPlcExternalSources", @"{'softwarePath':'PLC_1'}"),
            E("GetPlcWatchTables", @"{'softwarePath':'PLC_1'}"),
            E("GetHmiTagTables", @"{'softwarePath':'HMI_RT_1'}"),
            // ---- PLC authoring / import / export
            E("WritePlcSclSourceFile", @"{'sclContent':'FUNCTION \""FC_Add\"" : INT\nVAR_INPUT\n  a : INT;\n  b : INT;\nEND_VAR\nBEGIN\n  #FC_Add := #a + #b;\nEND_FUNCTION','outputPath':'C:\\Temp\\FC_Add.scl'}", "then ManagePlcExternalSources createFromFile + GenerateBlocksFromExternalSource"),
            E("ManagePlcExternalSources", @"{'softwarePath':'PLC_1','action':'createFromFile','name':'FC_Add','filePath':'C:\\Temp\\FC_Add.scl','dryRun':true}", "dryRun=false to create; generateBlocks afterwards"),
            E("GenerateBlocksFromExternalSource", @"{'softwarePath':'PLC_1','externalSourceName':'FC_Add'}"),
            E("ImportFromDocuments", @"{'softwarePath':'PLC_1','groupPath':'','importPath':'C:\\Temp\\docs','fileNameWithoutExtension':'FB_Pump','importOption':'Override'}", "importPath is the folder holding FB_Pump.s7dcl (+ .s7res)"),
            E("ImportBlocksFromDocuments", @"{'softwarePath':'PLC_1','groupPath':'','importPath':'C:\\Temp\\docs','regexName':''}"),
            E("ExportBlocksAsDocuments", @"{'softwarePath':'PLC_1','exportPath':'C:\\Temp\\docs','regexName':'FB_.*'}"),
            E("ExportAsDocuments", @"{'softwarePath':'PLC_1','blockPath':'Main','exportPath':'C:\\Temp\\docs'}"),
            E("ImportBlock", @"{'softwarePath':'PLC_1','groupPath':'','importPath':'C:\\Temp\\FB_Pump.xml'}", "SimaticML from ExportBlock; groupPath '' = program root"),
            E("ImportType", @"{'softwarePath':'PLC_1','groupPath':'','importPath':'C:\\Temp\\UDT_Motor.xml'}"),
            E("ImportPlcTagTable", @"{'softwarePath':'PLC_1','folderPath':'','importPath':'C:\\Temp\\Tags.xml'}"),
            E("ExportBlock", @"{'softwarePath':'PLC_1','blockPath':'Main','exportPath':'C:\\Temp'}", "Meta.exportedFile names the XML written"),
            E("ExportBlocks", @"{'softwarePath':'PLC_1','exportPath':'C:\\Temp\\blocks','regexName':''}"),
            E("ExportType", @"{'softwarePath':'PLC_1','typePath':'UDT_Motor','exportPath':'C:\\Temp'}"),
            E("ExportPlcTagTable", @"{'softwarePath':'PLC_1','tagTableName':'Default tag table','exportPath':'C:\\Temp\\tags.xml'}"),
            E("ManagePlcTagDefinition", @"{'softwarePath':'PLC_1','tablePath':'Default tag table','name':'Start','kind':'tag','action':'create','dataType':'Bool','addressOrValue':'%M0.0','dryRun':true}", "kind: tag | constant"),
            E("PlcBuildAndImport", @"{'softwarePath':'PLC_1','kind':'fc','json':'{\""blockName\"":\""FC_DryRun\"",\""blockNumber\"":12,\""inputs\"":[{\""name\"":\""Start\"",\""datatype\"":\""Bool\""}],\""outputs\"":[{\""name\"":\""Run\"",\""datatype\"":\""Bool\""}],\""structuredText\"":{\""operations\"":[{\""op\"":\""if\"",\""condition\"":\""Start\""},{\""op\"":\""assignment\"",\""target\"":\""Run\"",\""value\"":\""TRUE\"",\""indent\"":2},{\""op\"":\""endif\""}]}}','dryRun':true}", "kind: udt | tagtable | globaldb | fc | fb; json is the BuildPlc* structure as a JSON string"),
            E("ScaffoldProject", @"{'spec':'{\""projectName\"":\""Demo\"",\""directoryPath\"":\""C:\\\\Projects\"",\""plcName\"":\""PLC_1\"",\""plcFamily\"":\""S7-1500\""}','dryRun':true}", "full spec: templates/project-blueprints/scaffold_spec_start_stop.json; dryRun=false creates"),
            E("CompileSoftware", @"{'softwarePath':'PLC_1'}"),
            E("CompileAndDiagnosePlc", @"{'softwarePath':'PLC_1'}"),
            E("CompileAndDiagnoseHmi", @"{'softwarePath':'HMI_RT_1'}"),
            // ---- watch tables
            E("ManagePlcTableEntries", @"{'softwarePath':'PLC_1','tableKind':'watch','tablePath':'MCP_W/MCP_WT','action':'read'}", "tableKind: watch | force; tablePath is Group/Table"),
            E("SetWatchTableModifyValue", @"{'softwarePath':'PLC_1','tableName':'MCP_W/MCP_WT','address':'%M0.0','modifyValue':'TRUE','trigger':'OnceOnlyAtStart'}"),
            E("ExportPlcWatchTable", @"{'softwarePath':'PLC_1','watchTableName':'WT1','exportPath':'C:\\Temp\\WT1.xml'}"),
            // ---- hardware
            E("SearchHardwareCatalog", @"{'keyword':'1515-2 PN','limit':10}"),
            E("AddDeviceWithFallback", @"{'preferredMlfb':'6ES7 515-2AM02-0AB0','preferredVersion':'V2.9','deviceName':'PLC_2','family':'S7-1500'}"),
            E("AddDevice", @"{'orderNumber':'6ES7 515-2AM02-0AB0','version':'V2.9','deviceName':'PLC_2'}"),
            E("PlugDeviceItem", @"{'deviceItemPath':'PLC_1/导轨_0','orderNumber':'6ES7 521-1BH00-0AB0','version':'V2.2','positionNumber':2,'dryRun':true}", "S7-1500 modules plug into the rail item; dryRun=true only checks CanPlugNew"),
            E("ManageHardwareObject", @"{'devicePathJson':['PLC_2'],'action':'deleteDevice','dryRun':true}", "action: deleteDevice | deleteItem | moveItem | copyItem"),
            E("ConnectDeviceNodesToProfinetSubnet", @"{'firstRootPath':'PLC_1','secondRootPath':'HMI_KTP700_1/HMI_KTP700_1.IE_CP_1','subnetName':'PN_IE_1'}"),
            E("ManagePlcProtection", @"{'devicePathJson':['PLC_1'],'action':'read'}", "actions: read | setAccessLevel | setAccessPassword | resetAccessPassword | protectMasterSecret | changeMasterSecret | unprotectMasterSecret | resetMasterSecret | protectAllConfiguration | unprotectAllConfiguration; changes need dryRun=false + confirmChange=true"),
            E("CompileDevice", @"{'devicePathJson':['PLC_1']}", "hardware compile of one station (ICompilable)"),
            E("ManageOpcUaInterface", @"{'softwarePath':'PLC_1','interfaceName':'Server interface_1','action':'read'}"),
            // ---- online / transfer (PLCSIM Advanced only in the maintainer's environment)
            E("ReadTransferRoutes", @"{'softwarePath':'PLC_1'}", "pick pgPcInterface / targetIpAddress from the route tree"),
            E("ScanAccessibleDevices", @"{'pgPcInterface':'PLCSIM','limit':20}"),
            E("CheckDownloadReadiness", @"{'softwarePath':'PLC_1'}"),
            E("DownloadToPlc", @"{'softwarePath':'PLC_1','pgPcInterface':'PLCSIM','targetIpAddress':'192.168.0.3'}", "CPU protection (ManagePlcProtection) and a hardware compile must pass first; F-CPUs are refused by Openness"),
            E("GoOnline", @"{'softwarePath':'PLC_1','ipAddress':'192.168.0.3','pgPcInterface':'PLCSIM'}"),
            E("GetOnlineState", @"{'softwarePath':'PLC_1'}"),
            E("CompareSoftwareToOnline", @"{'softwarePath':'PLC_1','maxDepth':4}"),
            E("GoOffline", @"{'softwarePath':'PLC_1'}"),
            E("UploadStationFromPlc", @"{'targetIpAddress':'192.168.0.3','pgPcInterface':'PLCSIM','dryRun':true}", "execution needs confirmUpload=true; PLCSIM Advanced instances cannot be uploaded from (TIA)"),
            // ---- PLCSIM Advanced
            E("ReadPlcSimAdvancedInstances", @"{'includeState':true}"),
            E("ManagePlcSimAdvancedInstance", @"{'instanceName':'MCP_SIM','action':'register','cpuType':'CPU1500_Unspecified','communicationInterface':'Softbus'}", "action: register | powerOn | run | stop | powerOff | memoryReset | unregister; execution needs dryRun=false + confirmInstanceChange=true"),
            E("ReadPlcSimAdvancedTags", @"{'instanceName':'MCP_SIM','namesJson':['MCP_SimDB.Cycles','MCP_SimDB.Running']}", "empty namesJson lists the tags"),
            E("WritePlcSimAdvancedTags", @"{'instanceName':'MCP_SIM','valuesJson':{'MCP_SimDB.Start':true},'dryRun':false,'confirmWrite':true}"),
            E("RunPlcSimAdvancedTestScenario", @"{'scenarioJson':{'instance':'MCP_SIM','mode':'default','steps':[{'write':{'MCP_SimDB.Start':true}},{'waitMs':300},{'assert':{'MCP_SimDB.Running':true}}]},'dryRun':true}", "steps: write | waitMs | cycles (singleStep mode) | assert; execution needs dryRun=false + confirmRun=true"),
            // ---- HMI
            E("ExportHmiScreen", @"{'softwarePath':'HMI_RT_1','screenName':'Screen_1','exportPath':'C:\\Temp\\Screen_1.xml'}"),
            E("ImportHmiScreen", @"{'softwarePath':'HMI_RT_1','folderPath':'','importPath':'C:\\Temp\\Screen_1.xml'}"),
            E("ImportHmiTagTable", @"{'softwarePath':'HMI_RT_1','folderPath':'','importPath':'C:\\Temp\\HmiTags.xml'}"),
            // ---- export store (large responses)
            E("GetExport", @"{'exportId':'ex_20260902103000_0001','offset':0}", "offset = the previous page's nextOffset"),
            E("ListExports", @"{'tool':'GetBlocks','limit':10}"),
            E("SaveExport", @"{'exportId':'ex_20260902103000_0001','outputPath':'C:\\Temp\\blocks.json'}", "overwrite=true only after the user agreed"),
            E("DeleteExport", @"{'exportId':'ex_20260902103000_0001'}"),
            E("ClearExports", @"{'olderThanHours':0}", "0 drops every handle"),
        };

        private static readonly Dictionary<string, Example> Index = Rows.ToDictionary(r => r.Tool, r => r, StringComparer.OrdinalIgnoreCase);

        public static IReadOnlyList<Example> All => Rows;

        public static Example? Find(string? tool)
            => tool != null && Index.TryGetValue(tool.Trim(), out var e) ? e : null;

        /// <summary>The sentence appended to a tool description: "Example: {...} (note)."</summary>
        public static string Render(Example e)
            => "Example: " + e.ArgumentsJson + (e.Note.Length > 0 ? " (" + e.Note + ")" : "") + ".";

        /// <summary>Description plus the example sentence when one exists; unchanged otherwise.</summary>
        public static string Decorate(string tool, string description)
        {
            var e = Find(tool);
            if (e == null) return description;
            var d = description ?? "";
            return (d.Length == 0 || d.EndsWith(" ") ? d : d + " ") + Render(e);
        }

        /// <summary>
        /// Every example must parse and must fit its tool: exact parameter spelling, every required parameter present.
        /// The caller supplies the real signatures (reflection over the built engine in the tools-list gate; the linked
        /// tools in the offline suite). Unknown tools are reported too - an example for a renamed tool is dead weight.
        /// </summary>
        public static IReadOnlyList<string> ValidateAgainst(Func<string, IReadOnlyList<KeyValuePair<string, bool>>?> parametersOf)
        {
            var problems = new List<string>();
            foreach (var e in Rows)
            {
                JsonObject? args;
                try { args = JsonNode.Parse(e.ArgumentsJson) as JsonObject; }
                catch (JsonException jx) { problems.Add(e.Tool + ": example is not valid JSON (" + jx.Message + ")"); continue; }
                if (args == null) { problems.Add(e.Tool + ": example must be a JSON object"); continue; }
                var specs = parametersOf(e.Tool);
                if (specs == null) { problems.Add(e.Tool + ": no tool of that name (example is stale)"); continue; }
                foreach (var key in args.Select(kv => kv.Key))
                    if (!specs.Any(s => string.Equals(s.Key, key, StringComparison.Ordinal)))
                        problems.Add(e.Tool + ": example uses '" + key + "' which is not a parameter (exact spelling required)");
                foreach (var s in specs)
                    if (s.Value && args[s.Key] == null)
                        problems.Add(e.Tool + ": example lacks the required parameter '" + s.Key + "'");
            }
            return problems;
        }
    }
}
