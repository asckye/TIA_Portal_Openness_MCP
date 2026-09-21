using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TiaMcpServer.ModelContextProtocol
{
    // 2.7.58: verified multi-step sequences. Trial-and-error on the real machine mostly came from the ORDER of calls
    // (protection before hardware compile before download, PLC side before HMI side, dryRun before the real run), not
    // from single calls. Every sequence here was run on the maintainer's VM (docs/reference/real-machine-ledger.md and
    // the campaign plans); values are the placeholders of the curated examples. Validated at build time like the
    // examples (each step names a real tool and fits its signature). Zero dependencies; linked into the offline suite.
    public static class ToolRecipes
    {
        public sealed class Step
        {
            public string Tool { get; }
            public string ArgumentsJson { get; }
            public string Expect { get; }
            public Step(string tool, string argumentsJson, string expect) { Tool = tool; ArgumentsJson = argumentsJson; Expect = expect; }
        }

        public sealed class Recipe
        {
            public string Topic { get; }
            public string Purpose { get; }
            public string Preconditions { get; }
            public string Notes { get; }
            public IReadOnlyList<Step> Steps { get; }
            public Recipe(string topic, string purpose, string preconditions, string notes, params Step[] steps)
            { Topic = topic; Purpose = purpose; Preconditions = preconditions; Notes = notes; Steps = steps; }
        }

        private static Step S(string tool, string json, string expect = "") => new Step(tool, json.Replace('\'', '"'), expect);

        private static readonly Recipe[] Rows =
        {
            new Recipe("connect-project", "Attach to the TIA process holding the project and read the real names before any other call.",
                "TIA Portal is open with the project (the engine never starts an instance while one runs).",
                "Names are exact: use the PLC / HMI software paths GetProjectTree returns everywhere else.",
                S("Bootstrap", @"{}", "serverVersion, connected=false, recommendedNextTool Connect"),
                S("Connect", @"{'projectName':'<project name as shown in TIA>'}", "Meta.boundProcessId = the process holding the project, startedNew false"),
                S("GetState", @"{}", "project = the wanted project"),
                S("GetProjectTree", @"{}", "the PLC / HMI software paths used below")),

            new Recipe("plc-scl-block", "Create a block from SCL text: source file -> external source -> generated block -> compile -> save.",
                "connect-project done; the PLC software path is known.",
                "Counter is a reserved word in SCL. An SCL OB1 overwrites the default Main. The .scl file is UTF-8 without BOM unless it contains Chinese.",
                S("GetAuthoringGuide", @"{'topic':'scl'}", "verified syntax and encoding rules"),
                S("WritePlcSclSourceFile", @"{'sclContent':'<complete FUNCTION / FUNCTION_BLOCK / DATA_BLOCK text>','outputPath':'C:\\Temp\\<Block>.scl'}", "Meta reports the file written"),
                S("ManagePlcExternalSources", @"{'softwarePath':'PLC_1','action':'createFromFile','name':'<Block>','filePath':'C:\\Temp\\<Block>.scl','dryRun':true}", "preview clean"),
                S("ManagePlcExternalSources", @"{'softwarePath':'PLC_1','action':'createFromFile','name':'<Block>','filePath':'C:\\Temp\\<Block>.scl','dryRun':false}", "external source created"),
                S("GenerateBlocksFromExternalSource", @"{'softwarePath':'PLC_1','externalSourceName':'<Block>'}", "generated blocks listed"),
                S("CompileAndDiagnosePlc", @"{'softwarePath':'PLC_1'}", "ErrorCount 0"),
                S("SaveProject", @"{}", "IsModified false")),

            new Recipe("plc-s7dcl-import", "Add LAD / FBD logic from S7DCL text documents (preferred over hand-written FlgNet XML).",
                "connect-project done; the .s7dcl (+ .s7res) files are on the TIA machine, UTF-8 WITH BOM.",
                "importOption Override replaces a block of the same name; compile after every import.",
                S("GetAuthoringGuide", @"{'topic':'lad'}", "S7DCL syntax"),
                S("ImportFromDocuments", @"{'softwarePath':'PLC_1','groupPath':'','importPath':'C:\\Temp\\docs','fileNameWithoutExtension':'<Block>','importOption':'Override'}", "block imported"),
                S("CompileAndDiagnosePlc", @"{'softwarePath':'PLC_1'}", "ErrorCount 0"),
                S("SaveProject", @"{}", "saved")),

            new Recipe("plc-builder", "Build a UDT / tag table / global DB / FC / FB from structured JSON in one call (dry run first).",
                "connect-project done.",
                "kind: udt | tagtable | globaldb | fc | fb; the json shape is the BuildPlc* structure (see PlcBuildAndImport's example).",
                S("PlcBuildAndImport", @"{'softwarePath':'PLC_1','kind':'fc','json':'<BuildPlc* JSON as a string>','dryRun':true}", "WrittenFiles / Discovered* clean, Failed empty"),
                S("PlcBuildAndImport", @"{'softwarePath':'PLC_1','kind':'fc','json':'<same JSON>','dryRun':false,'compileAfter':true}", "Compile.ErrorCount 0"),
                S("SaveProject", @"{}", "saved")),

            new Recipe("watch-table", "Add a row to a watch table and read it back.",
                "connect-project done.",
                "tableName may be group-qualified ('MCP_W/MCP_WT'); the row is created through SimaticML, ModifyIntention is read-only (set by TIA).",
                S("GetPlcWatchTables", @"{'softwarePath':'PLC_1'}", "existing tables"),
                S("SetWatchTableModifyValue", @"{'softwarePath':'PLC_1','tableName':'<Group/Table>','address':'%M0.0','modifyValue':'TRUE','trigger':'OnceOnlyAtStart'}", "meta.readbackVerified true"),
                S("ManagePlcTableEntries", @"{'softwarePath':'PLC_1','tableKind':'watch','tablePath':'<Group/Table>','action':'read'}", "the new row in native order")),

            new Recipe("cpu-protection", "Make a new CPU downloadable: access level + confidential-configuration password + hardware compile.",
                "connect-project done; the station name is known (GetDevices).",
                "TIA V21 creates CPUs with NoAccess and 'protect confidential data' without a password - the hardware compile refuses a download until both are set. The password is a test value for the scratch project only.",
                S("ManagePlcProtection", @"{'devicePathJson':['<Station>'],'action':'read'}", "accessLevel / masterSecret as configured"),
                S("ManagePlcProtection", @"{'devicePathJson':['<Station>'],'action':'setAccessLevel','accessLevel':'FullAccess','dryRun':false,'confirmChange':true}", "accessLevel FullAccess (FullAccessIncludingFailsafe on an F-CPU)"),
                S("ManagePlcProtection", @"{'devicePathJson':['<Station>'],'action':'protectMasterSecret','password':'<test password>','dryRun':false,'confirmChange':true}", "masterSecret WithPassword"),
                S("CompileDevice", @"{'devicePathJson':['<Station>']}", "0 errors (warnings allowed)")),

            new Recipe("download-plcsim", "Download a standard CPU to a PLCSIM Advanced instance and go online (the maintainer's only permitted online target).",
                "cpu-protection done for that CPU; PLCSIM Advanced 8.0 installed on the TIA machine; the CPU is a standard one (F-CPUs are refused by Openness).",
                "Softbus makes TIA show a single 'PLCSIM' PG/PC interface; register with that when the instance is new. Downloads to FW >= 2.9 CPUs need trustDeviceCertificate (default true).",
                S("ReadPlcSimAdvancedInstances", @"{'includeState':true}", "instances and api.networkMode"),
                S("ManagePlcSimAdvancedInstance", @"{'instanceName':'<Instance>','action':'register','cpuType':'CPU1500_Unspecified','communicationInterface':'Softbus','dryRun':false,'confirmInstanceChange':true}", "registered (skip when it exists)"),
                S("ManagePlcSimAdvancedInstance", @"{'instanceName':'<Instance>','action':'powerOn','dryRun':false,'confirmInstanceChange':true}", "stateAfter Run/Stop"),
                S("ReadTransferRoutes", @"{'softwarePath':'PLC_1'}", "a 'PLCSIM' PC interface with the CPU's IP"),
                S("CompileSoftware", @"{'softwarePath':'PLC_1'}", "0 errors"),
                S("CheckDownloadReadiness", @"{'softwarePath':'PLC_1'}", "ready"),
                S("DownloadToPlc", @"{'softwarePath':'PLC_1','pgPcInterface':'PLCSIM','targetIpAddress':'<CPU IP>'}", "State Success / Warning"),
                S("GoOnline", @"{'softwarePath':'PLC_1','ipAddress':'<CPU IP>','pgPcInterface':'PLCSIM'}", "Online"),
                S("GetOnlineState", @"{'softwarePath':'PLC_1'}", "Online"),
                S("CompareSoftwareToOnline", @"{'softwarePath':'PLC_1','maxDepth':4}", "0 differences after a download"),
                S("GoOffline", @"{'softwarePath':'PLC_1'}", "Offline")),

            new Recipe("plcsim-test", "Read / write PLCSIM Advanced tags and run a closed-loop scenario against the downloaded program.",
                "download-plcsim done; the instance is in Run.",
                "Tags exist only after a download. Steps: write | waitMs | cycles (singleStep mode, one OB1 cycle each) | assert. Execution needs dryRun=false + confirmWrite / confirmRun.",
                S("ReadPlcSimAdvancedTags", @"{'instanceName':'<Instance>','namesJson':''}", "tag list"),
                S("ReadPlcSimAdvancedTags", @"{'instanceName':'<Instance>','namesJson':['<DB>.<Tag>']}", "current values"),
                S("WritePlcSimAdvancedTags", @"{'instanceName':'<Instance>','valuesJson':{'<DB>.<Tag>':true},'dryRun':true}", "preview"),
                S("WritePlcSimAdvancedTags", @"{'instanceName':'<Instance>','valuesJson':{'<DB>.<Tag>':true},'dryRun':false,'confirmWrite':true}", "written and read back"),
                S("RunPlcSimAdvancedTestScenario", @"{'scenarioJson':{'instance':'<Instance>','mode':'default','steps':[{'write':{'<DB>.Start':true}},{'waitMs':300},{'assert':{'<DB>.Running':true}}]},'dryRun':true}", "plan validated"),
                S("RunPlcSimAdvancedTestScenario", @"{'scenarioJson':{'instance':'<Instance>','mode':'default','steps':[{'write':{'<DB>.Start':true}},{'waitMs':300},{'assert':{'<DB>.Running':true}}]},'dryRun':false,'confirmRun':true}", "PASSED n/n")),

            new Recipe("hardware-device", "Add a device from the catalog, plug a module, and remove a temporary device again.",
                "connect-project done. The maintainer's rule: hardware tests only on temporary devices in the scratch project, never on existing ones.",
                "S7-1500 modules plug into the rail item ('<Station>/导轨_0'); AddDevice needs the exact catalog version from SearchHardwareCatalog.",
                S("SearchHardwareCatalog", @"{'keyword':'1515-2 PN','limit':10}", "order numbers and versions"),
                S("AddDevice", @"{'orderNumber':'6ES7 515-2AM02-0AB0','version':'V2.9','deviceName':'<Station>'}", "device created"),
                S("PlugDeviceItem", @"{'deviceItemPath':'<Station>/导轨_0','orderNumber':'6ES7 521-1BH00-0AB0','version':'V2.2','positionNumber':2,'dryRun':true}", "CanPlugNew true"),
                S("PlugDeviceItem", @"{'deviceItemPath':'<Station>/导轨_0','orderNumber':'6ES7 521-1BH00-0AB0','version':'V2.2','positionNumber':2,'dryRun':false}", "module plugged"),
                S("ManageHardwareObject", @"{'devicePathJson':['<Station>'],'action':'deleteDevice','dryRun':true}", "preview"),
                S("ManageHardwareObject", @"{'devicePathJson':['<Station>'],'action':'deleteDevice','dryRun':false}", "device removed")),

            new Recipe("hmi-unified-screen", "Unified HMI: connection -> tag bound to a PLC tag -> screen -> button -> compile.",
                "connect-project done; the PLC side (tags / DB) exists; the HMI is WinCC Unified (classic panels cannot get connections via Openness).",
                "PLC side first. Text labels use itemType Text; a Rectangle has no Text.",
                S("EnsureUnifiedHmiConnection", @"{'hmiSoftwarePath':'HMI_RT_1','connectionName':'HMI_Connection_1','plcName':'PLC_1'}", "connection present"),
                S("EnsureUnifiedHmiTag", @"{'hmiSoftwarePath':'HMI_RT_1','tagTableName':'Default tag table','tagName':'<Tag>','hmiDataType':'Bool','plcName':'PLC_1','plcTag':'<PLC tag>'}", "AbsoluteVerified true"),
                S("EnsureUnifiedHmiScreen", @"{'hmiSoftwarePath':'HMI_RT_1','screenName':'<Screen>'}", "screen present"),
                S("EnsureUnifiedHmiScreenItem", @"{'hmiSoftwarePath':'HMI_RT_1','screenName':'<Screen>','itemName':'btnStart','itemType':'Button','left':20,'top':20,'width':120,'height':40,'text':'Start'}", "item present"),
                S("EnsureUnifiedHmiButtonAction", @"{'hmiSoftwarePath':'HMI_RT_1','screenName':'<Screen>','buttonName':'btnStart','eventType':'Down','actionKind':'set-bit','targetTag':'<Tag>'}", "handler written"),
                S("CompileAndDiagnoseHmi", @"{'softwarePath':'HMI_RT_1'}", "ErrorCount 0")),

            new Recipe("export-import-block", "Round-trip one block through SimaticML (export, edit outside, import back).",
                "connect-project done.",
                "ExportBlock reports the file written in Meta.exportedFile; ImportBlock normalises the engineering version automatically.",
                S("ExportBlock", @"{'softwarePath':'PLC_1','blockPath':'<Group/Block>','exportPath':'C:\\Temp'}", "Meta.exportedFile"),
                S("ImportBlock", @"{'softwarePath':'PLC_1','groupPath':'<Group>','importPath':'C:\\Temp\\<Block>.xml'}", "imported"),
                S("CompileAndDiagnosePlc", @"{'softwarePath':'PLC_1'}", "ErrorCount 0")),

            new Recipe("large-response", "Read a response that was stored because it exceeded the size limit.",
                "A response came back with meta.truncated=true and an exportId.",
                "Pages are character slices: concatenate every page before parsing. SaveExport writes the whole payload to a file on the TIA machine.",
                S("GetExport", @"{'exportId':'<exportId>','offset':0}", "first page + nextOffset"),
                S("GetExport", @"{'exportId':'<exportId>','offset':20000}", "offset = the previous page's nextOffset; repeat until eof=true"),
                S("SaveExport", @"{'exportId':'<exportId>','outputPath':'C:\\Temp\\<name>.json'}", "path written")),
        };

        private static readonly Dictionary<string, Recipe> Index = Rows.ToDictionary(r => r.Topic, r => r, StringComparer.OrdinalIgnoreCase);

        public static IReadOnlyList<Recipe> All => Rows;

        public static Recipe? Find(string? topic) => topic != null && Index.TryGetValue(topic.Trim(), out var r) ? r : null;

        /// <summary>Every step must parse, name a real tool and use exact parameter names; required parameters must be present.</summary>
        public static IReadOnlyList<string> ValidateAgainst(Func<string, IReadOnlyList<KeyValuePair<string, bool>>?> parametersOf)
        {
            var problems = new List<string>();
            foreach (var r in Rows)
            {
                int n = 0;
                foreach (var step in r.Steps)
                {
                    n++;
                    string where = r.Topic + " step " + n + " (" + step.Tool + ")";
                    JsonObject? args;
                    try { args = JsonNode.Parse(step.ArgumentsJson) as JsonObject; }
                    catch (JsonException jx) { problems.Add(where + ": not valid JSON (" + jx.Message + ")"); continue; }
                    if (args == null) { problems.Add(where + ": arguments must be a JSON object"); continue; }
                    var specs = parametersOf(step.Tool);
                    if (specs == null) { problems.Add(where + ": no tool of that name"); continue; }
                    foreach (var key in args.Select(kv => kv.Key))
                        if (!specs.Any(sp => string.Equals(sp.Key, key, StringComparison.Ordinal)))
                            problems.Add(where + ": '" + key + "' is not a parameter");
                    foreach (var sp in specs)
                        if (sp.Value && args[sp.Key] == null)
                            problems.Add(where + ": required parameter '" + sp.Key + "' missing");
                }
            }
            return problems;
        }
    }
}
