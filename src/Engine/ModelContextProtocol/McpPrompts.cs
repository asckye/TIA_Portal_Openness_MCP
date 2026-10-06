using ModelContextProtocol.Server;
using System.ComponentModel;

namespace TiaMcpServer.ModelContextProtocol
{
    [McpServerPromptType]
    public static class McpPrompts
    {
        private static string WithV4Rules(string task)
        {
            return task + @"

V4 operating rules:
- Use only tools registered by this release. Read GetToolUsage for the selected tool and copy its typed arguments; pass arrays, objects, numbers, booleans and enums as their schema types, never JSON-encode typed values into strings.
- For a new or different TIA process, call ListPortalProcessProjects and choose the exact row with the user. On V20/V21 call ConnectProject with that row's processId, processStartUtc and projectPath; on Foundation call ConnectPortal with the selected row's processId. Do not launch or select a process implicitly.
- Read every result as the schemaVersion 4 envelope: ok, data, error and meta. Inspect meta.outcome, meta.execution and meta.completeness, then operation data and warnings. Follow meta.paging only within the same release/session/binding/query snapshot; meta.paging.complete=true describes that snapshot's pages, not full project observation.
- On meta.outcome=unknown or error.code=OUTCOME_UNKNOWN, stop, inspect the target and reset the session when required. Never replay a write automatically.
- D1 native behavior remains current for all applicable families and releases; L5 is NOT RUN. Treat UNVERIFIED_BEHAVIOR as a warning. Do not infer safe-v4 behavior or use candidate parameters.
- Do not save or close a project unless the user explicitly requested that action. Perform save and close as separate, explicit calls.
- MCP WRITE and ONLINE-WRITE calls require Workbench approval before dispatch. Approval is per request; a denial, timeout or unavailable Workbench rejects the operation before it starts. MCP clients cannot approve their own calls. Audit events are in data/logs/audit; verify them with the Workbench audit view or `""tia audit verify`"". A valid hash chain cannot prove that the complete log was retained.";
        }

        [McpServerPrompt(Name = "Connect"), Description("Select and connect to a specific TIA Portal project process")]
        public static string Connect()
        {
            return WithV4Rules(@"Connect to the intended project. First call ListPortalProcessProjects and have the user select the exact process and project row. On V20/V21, call ConnectProject with that row's processId, processStartUtc and projectPath. On a Foundation host, call ConnectPortal with the selected row's processId. Call GetSessionState and confirm the connected process and project. Do not start TIA Portal or choose a process by name alone.");
        }

        [McpServerPrompt(Name = "OpenProject"), Description("Open a TIA Portal project")]
        public static string OpenProject(string projectPath)
        {
            return WithV4Rules($@"Open the requested project at path: {projectPath}

If the intended process is not connected, use ListPortalProcessProjects and the release-specific connector from the V4 rules with the exact selected row first. Call OpenProject with the typed path string. Then call GetProjectTree and use the exact returned software and object paths. Opening a project does not imply saving or closing another project.");
        }

        [McpServerPrompt(Name = "CloseProject"), Description("Close the currently open TIA Portal project when explicitly requested")]
        public static string CloseProject()
        {
            return WithV4Rules(@"The user explicitly requested project close. Inspect GetSessionState first and confirm the target project. Do not save implicitly. If the user separately requested saving, call SaveProject as a separate action and inspect its result. Then call CloseProject and verify with GetSessionState.");
        }

        [McpServerPrompt(Name = "Disconnect"), Description("Disconnect from the selected TIA Portal process when explicitly requested")]
        public static string Disconnect()
        {
            return WithV4Rules(@"The user explicitly requested disconnect. Inspect GetSessionState and identify the bound project/process. Do not save or close implicitly. If separately requested, perform SaveProject and CloseProject as separate calls and inspect each result. Then call DisconnectPortal and verify the session state.");
        }

        [McpServerPrompt(Name = "GetProjectTree"), Description("Get the current project structure")]
        public static string GetProjectTree()
        {
            return WithV4Rules(@"Read GetSessionState first. If the intended project is not bound, use ListPortalProcessProjects and ConnectProject with an explicitly selected row, then OpenProject if needed. Call GetProjectTree and retain exact device, PLC software and HMI software paths for later calls.");
        }

        [McpServerPrompt(Name = "CreateProjectWithDevices"), Description("Create a project with selected PLC and HMI devices")]
        public static string CreateProjectWithDevices(string projectDirectory, string projectName, string plcFamily, string plcDeviceName, string hmiKeyword, string hmiDeviceName)
        {
            return WithV4Rules($@"Create project {projectName} in {projectDirectory} with the requested PLC family {plcFamily} ({plcDeviceName}) and HMI {hmiKeyword} ({hmiDeviceName}). First bind the explicitly selected TIA process using ListPortalProcessProjects and the release-specific connector described in the V4 rules. Call CreateProject with object arguments directoryPath={projectDirectory} and projectName={projectName}. Use SearchHardwareCatalog to identify exact catalog choices, then GetToolUsage for CreateHardwareDevice or CreateHardwareCatalogDevice and supply their schema-typed arguments. Call GetProjectTree to obtain exact root paths, then ConnectDeviceNodesToProfinetSubnet with firstRootPath and secondRootPath strings. Report each result. Save only if separately requested.");
        }

        [McpServerPrompt(Name = "AddProfinetDevice"), Description("Add a selected hardware device and connect it to PROFINET")]
        public static string AddProfinetDevice(string keyword, string deviceName, string existingPlcRoot)
        {
            return WithV4Rules($@"Find the requested catalog item for {keyword}. Call SearchHardwareCatalog and select the exact catalog result with the user. Retrieve GetToolUsage for CreateHardwareCatalogDevice and pass keyword and deviceName as strings; do not invent catalog versions. Call GetProjectTree, then ConnectDeviceNodesToProfinetSubnet with firstRootPath={existingPlcRoot} and the exact new-device root path. Saving is a separate user-requested action.");
        }

        [McpServerPrompt(Name = "GetSoftwareTree"), Description("Get a PLC or HMI software tree")]
        public static string GetSoftwareTree(string softwarePath)
        {
            return WithV4Rules($@"Call GetSoftwareTree with softwarePath={softwarePath}. Use exact block, type, source, screen and group paths returned by this tree for subsequent operations. If the path is uncertain, call GetProjectTree first.");
        }

        [McpServerPrompt(Name = "ExportBlocks"), Description("Export PLC blocks")]
        public static string ExportBlocks(string softwarePath, string exportPath, string regexName, bool preservePath)
        {
            return WithV4Rules($@"Export from softwarePath={softwarePath} to exportPath={exportPath}, regexName={regexName}, preservePath={preservePath}. Retrieve GetToolUsage for ExportPlcBlocks and call it with those typed values. Compile only if requested. Review the V4 data and exported-file evidence; export does not modify or save the project.");
        }

        [McpServerPrompt(Name = "ExportTypes"), Description("Export PLC types")]
        public static string ExportTypes(string softwarePath, string exportPath, string regexName, bool preservePath)
        {
            return WithV4Rules($@"Call ExportPlcTypes with softwarePath={softwarePath}, exportPath={exportPath}, regexName={regexName} and preservePath={preservePath}. Read GetToolUsage first and pass the boolean and strings in their declared types. Inspect returned data and file evidence.");
        }

        [McpServerPrompt(Name = "ExportBlocksAsDocuments"), Description("Export blocks as SIMATIC SD documents")]
        public static string ExportBlocksAsDocuments(string softwarePath, string exportPath, string regexName, bool preservePath)
        {
            return WithV4Rules($@"For a supported release, call ExportPlcBlocksDocuments with softwarePath={softwarePath}, exportPath={exportPath}, regexName={regexName}, preservePath={preservePath}. Read its V4 schema first. Preserve the exported .s7dcl/.s7res evidence and verify the release supports this operation.");
        }

        [McpServerPrompt(Name = "ExportAllBlocksFlattened"), Description("Export blocks without preserving folders")]
        public static string ExportAllBlocksFlattened(string softwarePath, string exportPath)
            => ExportBlocks(softwarePath, exportPath, "", false);

        [McpServerPrompt(Name = "ExportAllBlocksStructured"), Description("Export blocks preserving folder paths")]
        public static string ExportAllBlocksStructured(string softwarePath, string exportPath)
            => ExportBlocks(softwarePath, exportPath, "", true);

        [McpServerPrompt(Name = "ExportAllTypesFlattened"), Description("Export all PLC types without preserving folders")]
        public static string ExportAllTypesFlattened(string softwarePath, string exportPath)
            => ExportTypes(softwarePath, exportPath, "", false);

        [McpServerPrompt(Name = "ExportAllTypesStructured"), Description("Export PLC types preserving folder paths")]
        public static string ExportAllTypesStructured(string softwarePath, string exportPath)
            => ExportTypes(softwarePath, exportPath, "", true);

        [McpServerPrompt(Name = "ExportAllBlocksAsDocumentsFlattened"), Description("Export all blocks as documents without folders")]
        public static string ExportAllBlocksAsDocumentsFlattened(string softwarePath, string exportPath)
            => ExportBlocksAsDocuments(softwarePath, exportPath, "", false);

        [McpServerPrompt(Name = "ExportAllBlocksAsDocumentsStructured"), Description("Export all blocks as documents preserving folders")]
        public static string ExportAllBlocksAsDocumentsStructured(string softwarePath, string exportPath)
            => ExportBlocksAsDocuments(softwarePath, exportPath, "", true);

        [McpServerPrompt(Name = "ImportFromDocuments"), Description("Import one block from SIMATIC SD documents")]
        public static string ImportFromDocuments(string softwarePath, string groupPath, string importPath, string fileNameWithoutExtension, string importOption)
        {
            return WithV4Rules($@"Import the selected block into softwarePath={softwarePath}, groupPath={groupPath}. Use GetToolUsage for ImportPlcBlockDocuments and pass softwarePath, groupPath, importPath={importPath}, fileNameWithoutExtension={fileNameWithoutExtension} and importOption={importOption} in their declared types (importOption is an enum). Match the artifact to the target TIA release; the tool does not rewrite its engineering version or BOM. Inspect import evidence before any separately requested compile or save.");
        }

        [McpServerPrompt(Name = "ImportBlocksFromDocuments"), Description("Import matching blocks from SIMATIC SD documents")]
        public static string ImportBlocksFromDocuments(string softwarePath, string groupPath, string importPath, string regexName, string importOption)
        {
            return WithV4Rules($@"Call ImportPlcBlocksDocuments with softwarePath={softwarePath}, groupPath={groupPath}, importPath={importPath}, regexName={regexName} and the schema enum importOption={importOption}. Check GetToolUsage for required/optional fields and match source documents to the target release. Inspect each V4 item result; compile or save only when separately requested.");
        }

        [McpServerPrompt(Name = "CreatePlcFunctionBlock"), Description("Create and import a PLC function block")]
        public static string CreatePlcFunctionBlock(string softwarePath, string fbName, int fbNumber, string description)
        {
            return WithV4Rules($@"Create an FB in softwarePath={softwarePath} named {fbName}, requested number {fbNumber}. Inspect GetSoftwareTree and GetToolUsage for BuildAndImportPlcArtifact. Construct its `spec` as a typed object matching the registered schema and the user's description: {description}. First call with dryRun=true and inspect generated data; after approval, call with dryRun=false and compileAfter=false. CompilePlcDiagnostics is a separate explicit step. Do not save implicitly.");
        }

        [McpServerPrompt(Name = "CreatePlcFunctionBlockWithLogic"), Description("Create a PLC function block with SCL logic")]
        public static string CreatePlcFunctionBlockWithLogic(string softwarePath, string fbName, int fbNumber)
        {
            return WithV4Rules($@"Inspect GetSoftwareTree and GetToolUsage for BuildAndImportPlcArtifact. Build kind=""fb"" and a typed `spec` object for FB {fbName} (requested number {fbNumber}), including structuredText fields validated by the schema. Do not serialize spec as JSON text. Run dryRun=true, inspect the V4 result, then request approval before dryRun=false; set compileAfter=false and run CompilePlcDiagnostics separately if requested.");
        }

        [McpServerPrompt(Name = "CreatePlcGlobalDb"), Description("Create a PLC global data block")]
        public static string CreatePlcGlobalDb(string softwarePath, string dbName, int dbNumber)
        {
            return WithV4Rules($@"Use GetToolUsage for BuildAndImportPlcArtifact and create kind=""globaldb"" with a typed `spec` object for {dbName} (number {dbNumber}). Follow the exact schema for members and start values. Run dryRun=true and inspect its V4 result before the approved dryRun=false call; use compileAfter=false and compile separately only if requested.");
        }

        [McpServerPrompt(Name = "CreatePlcTagTable"), Description("Create a PLC tag table")]
        public static string CreatePlcTagTable(string softwarePath, string tableName)
        {
            return WithV4Rules($@"Use GetToolUsage for BuildAndImportPlcArtifact. Build kind=""tagtable"" and a typed `spec` object for table {tableName}; pass addresses and tags in the schema-defined fields, not a JSON string. Run dryRun=true and inspect the plan before requesting approval for dryRun=false. Set compileAfter=false; compilation and saving remain separate explicit actions.");
        }

        [McpServerPrompt(Name = "CompileAndDiagnose"), Description("Compile PLC software and inspect diagnostics")]
        public static string CompileAndDiagnose(string softwarePath)
        {
            return WithV4Rules($@"Call CompilePlcDiagnostics with softwarePath={softwarePath}. Inspect the V4 result's data and meta diagnostics, effective state, incomplete flag, errors and warnings. Do not infer success from transport status; report nested messages and counts without summing overlapping subtree totals. Export or import a correction only if requested.");
        }

        [McpServerPrompt(Name = "CreateUnifiedHmiPage"), Description("Create a Unified HMI screen and bindings")]
        public static string CreateUnifiedHmiPage(string hmiSoftwarePath, string screenName, string plcName, string connectionName)
        {
            return WithV4Rules($@"For HMI software {hmiSoftwarePath}, use GetToolUsage to inspect schemas for EnsureUnifiedHmiConnection, EnsureUnifiedHmiScreen, EnsureUnifiedHmiTagTable, EnsureUnifiedHmiTag, EnsureUnifiedHmiScreenItem, ApplyUnifiedHmiScreenDesign and EnsureUnifiedHmiDynamization. Create connection {connectionName} to PLC {plcName} and screen {screenName}, using typed fields and a typed design object. Confirm exact PLC tag paths. Inspect each result before the next write; request Workbench approval when required. Compile or save only if separately requested.");
        }

        [McpServerPrompt(Name = "CreateStartStopHmi"), Description("Create a motor start/stop Unified HMI screen")]
        public static string CreateStartStopHmi(string hmiSoftwarePath, string screenName)
        {
            return WithV4Rules($@"Use GetToolUsage to inspect the schema for SetUnifiedHmiRuntimeState. Call it with hmiSoftwarePath={hmiSoftwarePath} and the typed fields needed to create or update screen {screenName}. Do not assume arguments from older shortcut tools. Inspect returned data; save only if separately requested.");
        }

        [McpServerPrompt(Name = "ApplyHmiLayout"), Description("Build and apply a Unified HMI screen layout")]
        public static string ApplyHmiLayout(string hmiSoftwarePath, string screenName)
        {
            return WithV4Rules($@"Use GetToolUsage for BuildUnifiedHmiLayoutDesign and ApplyUnifiedHmiLayout. Pass layout as an object to both tools, with schema-defined grid and item fields; never JSON-encode the object. Apply the resulting layout to hmiSoftwarePath={hmiSoftwarePath}, screenName={screenName} after inspecting the design result and obtaining approval. Save only if separately requested.");
        }

        [McpServerPrompt(Name = "CreateClassicHmiScreen"), Description("Build and import a Classic HMI screen package")]
        public static string CreateClassicHmiScreen(string hmiSoftwarePath, string screenName)
        {
            return WithV4Rules($@"Use GetToolUsage for BuildClassicHmiScreen, WriteClassicHmiMinimalPackageFiles, ValidateClassicHmiMinimalPackageFiles, ImportHmiScreen and ImportHmiTagTable. Pass design, package and screen definitions as typed objects. Write the package to an explicit output directory, validate it, then import with schema-typed paths into {hmiSoftwarePath}. Inspect each result; saving is separate and only on request.");
        }

        [McpServerPrompt(Name = "MonitorPlcValues"), Description("Read online PLC values from a watch table")]
        public static string MonitorPlcValues(string softwarePath)
        {
            return WithV4Rules($@"Read values only. Call PlanOnlineReadOnlyMonitoring with softwarePath={softwarePath} and a typed tagPaths array; inspect the plan. Then use ProbePlcMonitorOnlineCapabilities, ListPlcWatchTables and GetPlcWatchTableCurrentValuesReadOnly with schema-typed arguments. Do not write PLC values.");
        }

        [McpServerPrompt(Name = "RunPreRelease"), Description("Run the offline release validation suite")]
        public static string RunPreRelease(string workspaceRoot, string reportDirectory)
        {
            return WithV4Rules($@"Call RunOfflineReleaseValidationSuite with workspaceRoot={workspaceRoot} and reportDirectory={reportDirectory}. Inspect its V4 result and generated report path. Then call BuildReleaseDiagnosticReport and BuildReleaseRunbook with the typed offlineReleaseSuiteJsonPath from that result. Offline validation does not establish native TIA acceptance; retain NOT RUN status without a matching machine record.");
        }
    }
}
