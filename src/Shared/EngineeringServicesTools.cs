using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.Logic.V4;
using TiaMcp.Logic.V4.Inputs;
using TiaMcpServer.Siemens;
using TiaMcpServer.Siemens.Services;
using static TiaMcpServer.ModelContextProtocol.McpServer;

namespace TiaMcpServer.ModelContextProtocol
{
    [McpServerToolType]
    internal sealed class EngineeringServicesTools
    {
        private readonly EngineeringServicesPortService _service;
        public EngineeringServicesTools(EngineeringServicesPortService service) => _service = service;
        [McpServerTool(Name = "ManageProjectLanguage"), Description("[L2][Project][WRITE] Read/activate/deactivate/setEditing/setReference project language by exact culture. Default preview. Editing/reference must be active, cannot deactivate either; readback verified; no save/compile/download. Returns a V4 envelope; inspect outcome, execution and completeness.")]
        public CallToolResult ManageProjectLanguageV4(
            [Description("action: read | activate | deactivate | setEditing | setReference.")] string action="read",
            [Description("culture: exact language tag, e.g. 'en-US', 'zh-CN' (required for every action except read).")] string culture="",
            [Description("dryRun: true (default) previews; false executes.")] bool dryRun=true)
        {
            return PlcExchangeContract.Run("ManageProjectLanguage", () => ManageProjectLanguage(action, culture, dryRun), write: !dryRun && action != "read", current: false);
        }

        [McpServerTool(Name = "ExportProjectTexts"), Description("[L2][Project][FILE] Native project text export for explicit source/target cultures to a new absolute file. Returns API completion and file hash; default preview." + " Returns a V4 envelope; inspect outcome, execution and completeness. Native policy remains current pending family acceptance.")]
        public CallToolResult ExportProjectTextsV4(
            [Description("filePath: new absolute XLSX file in an existing directory.")] string filePath,
            [Description("sourceCulture: source language tag, e.g. 'en-US'.")] string sourceCulture,
            [Description("targetCulture: target language tag, e.g. 'zh-CN'.")] string targetCulture,
            bool dryRun=true)
        {
            return PlcExchangeContract.Run("ExportProjectTexts", () => ExportProjectTexts(filePath, sourceCulture, targetCulture, dryRun), write: !dryRun, current: true);
        }

        [McpServerTool(Name = "ImportProjectTexts"), Description("[L2][Project][WRITE] Native project text import. updateSourceLanguage explicitly controls updating SOURCE language, not a generic overwrite switch. Default preview; returns native diagnostics; no save/compile/download." + " Returns a V4 envelope; inspect outcome, execution and completeness. Native policy remains current pending family acceptance.")]
        public CallToolResult ImportProjectTextsV4(
            [Description("filePath: existing absolute XLSX file exported by ExportProjectTexts.")] string filePath,
            [Description("updateSourceLanguage: true also writes the source-language texts.")] bool updateSourceLanguage,
            bool dryRun=true)
        {
            return PlcExchangeContract.Run("ImportProjectTexts", () => ImportProjectTexts(filePath, updateSourceLanguage, dryRun), write: !dryRun, current: true);
        }

        [McpServerTool(Name="GetObjectIdentifier"), Description("[L2][Project][READ] ObjectIdentifierProvider (project service): GetIdentifier of the exact object (kind=device/deviceItem via devicePath/itemPath, kind=plcBlock/plcType/plcTagTable via softwarePath + objectPath) - a cross-session stable identifier - or, with identifier given, Find(identifier) and describe the object it resolves to (class, name, owner path, ISystemObject flag). Official support: Device, DeviceItem, code/data blocks, PLC tags, software units, TechnologicalInstanceDB, PlcStruct. Read-only.")]
        public CallToolResult GetObjectIdentifierV4(
            [Description("kind: device | deviceItem | plcBlock | plcType | plcTagTable.")] string kind="device",
            string[]? devicePath = null,
            string[]? itemPath = null,
            string softwarePath="",
            string objectPath="",
            [Description("identifier: exact identifier of the object as the read action lists it.")] string identifier="")
            => SessionToolContract.Run("GetObjectIdentifier", false, false, () => ReadObjectIdentifier(kind, V4Json.Serialize(devicePath ?? Array.Empty<string>()), V4Json.Serialize(itemPath ?? Array.Empty<string>()), softwarePath, objectPath, identifier));

        public ResponseMessage ReadObjectIdentifier(
            [Description("kind: device | deviceItem | plcBlock | plcType | plcTagTable.")] string kind="device",
            string devicePathJson="[]",
            string itemPathJson="[]",
            string softwarePath="",
            string objectPath="",
            [Description("identifier: exact identifier of the object as the read action lists it.")] string identifier="")
            => _service.ReadObjectIdentifier(kind,devicePathJson,itemPathJson,softwarePath,objectPath,identifier);

        [McpServerTool(Name="ShowObjectInEditor"), Description("[L2][Project][WRITE] IShowable.ShowInEditor on the exact object (kind=device via devicePath, kind=plcBlock/plcType/plcTagTable via softwarePath + objectPath): opens it in the TIA Portal editor for the engineer. UI only - no project data changes; needs a Portal started with user interface. Default dryRun=true.")]
        public CallToolResult ShowObjectInEditorV4(
            [Description("kind: device | deviceItem | plcBlock | plcType | plcTagTable.")] string kind="device",
            string[]? devicePath = null,
            string[]? itemPath = null,
            string softwarePath="",
            string objectPath="",
            bool dryRun=true)
            => SessionToolContract.Run("ShowObjectInEditor", !dryRun, false, () => ShowObjectInEditor(kind, V4Json.Serialize(devicePath ?? Array.Empty<string>()), V4Json.Serialize(itemPath ?? Array.Empty<string>()), softwarePath, objectPath, dryRun));

        public ResponseMessage ShowObjectInEditor(
            [Description("kind: device | deviceItem | plcBlock | plcType | plcTagTable.")] string kind="device",
            string devicePathJson="[]",
            string itemPathJson="[]",
            string softwarePath="",
            string objectPath="",
            bool dryRun=true)
            => _service.ShowObjectInEditor(kind,devicePathJson,itemPathJson,softwarePath,objectPath,dryRun);

        [McpServerTool(Name="ManageProjectCompilationSettings"), Description("[L2][Project][WRITE] Project simulation / virtual PLC support during block compilation (official 'Updating project properties'): read or update properties {IsSimulationDuringBlockCompilationEnabled, IsVirtualPlcDuringBlockCompilationEnabled} (booleans). V20 reads / writes the two Project properties; V21 removed them and goes through the project's PlcSimulationSettingsProvider / VirtualPlcSettingsProvider services; every write is read back. Default dryRun=true; no save. Current native policy; V4 safety behavior is not yet accepted.")]
        public CallToolResult ManageProjectCompilationSettingsV4([Description("read | update. ")] string action="read", AttributeMap<Scalar> properties = null!, bool dryRun=true)
            => EngineeringToolContract.Run("ManageProjectCompilationSettings", !dryRun && action != "read" && action != "list", () => ManageProjectCompilationSettings(action, EngineeringToolContract.Json(properties), dryRun));

        public ResponseMessage ManageProjectCompilationSettings(string action="read", string propertiesJson="{}", bool dryRun=true)
            => _service.ManageProjectCompilationSettings(action,propertiesJson,dryRun);

        [McpServerTool(Name="CompareLibraries"), Description("[L2][Library][READ] Native CompareToLibrary between two libraries: empty name = project library, otherwise exact open global library name. Result tree flattened (path/depth/state), summary counts all states, records exclude identical elements unless includeIdentical=true; paginated. Read-only, no library opened or modified.")]
        public CallToolResult CompareLibraries(
            [Description("leftLibraryName: global library on the left side of the comparison ('' = the project library).")] string leftLibraryName="",
            [Description("rightLibraryName: global library on the right side of the comparison ('' = the project library).")] string rightLibraryName="",
            bool includeIdentical=false,
            int maxDepth=8,
            int offset=0,
            int limit=100)
            => SecurityToolContract.Invoke("CompareLibraries", true, false,
                () => _service.CompareLibraries(leftLibraryName,rightLibraryName,includeIdentical,maxDepth,offset,limit));

        [McpServerTool(Name="CompareProjects"), Description("[L2][Project][READ] Native OFFLINE comparison: kind=software (exact softwarePath vs targetSoftwarePath in the bound project, or vs the CPU at targetDevicePath/targetItemPath in another open project targetProjectName), softwareToLibrary (softwarePath vs project library or exact targetLibraryName), hardware (devicePath/itemPath vs target device, same or other open project). Flattened result tree, summary of all states, non-identical records paginated. No online access; nothing modified.")]
        public CallToolResult CompareProjects(
            string kind,
            string softwarePath="",
            string[] devicePath=null!,
            string[] itemPath=null!,
            [Description("targetProjectName: exact name of the project to compare with.")] string targetProjectName="",
            string targetSoftwarePath="",
            [Description("targetDevicePath: JSON array naming the target station.")] string[] targetDevicePath=null!,
            [Description("targetItemPath: array of device-item names on the target.")] string[] targetItemPath=null!,
            string targetLibraryName="",
            bool includeIdentical=false,
            int maxDepth=8,
            int offset=0,
            int limit=100)
            => SecurityToolContract.Invoke("CompareProjects", true, false,
                () => _service.CompareProjects(kind,softwarePath,SecurityToolContract.Path(devicePath),SecurityToolContract.Path(itemPath),targetProjectName,targetSoftwarePath,SecurityToolContract.Path(targetDevicePath),SecurityToolContract.Path(targetItemPath),targetLibraryName,includeIdentical,maxDepth,offset,limit));

        [McpServerTool(Name="GetProjectSettings"), Description("[L2][Project][READ] Read TIA Portal settings folders (exact slash-separated folderPath; empty lists root folders) with scalar setting values, paginated; optional customIdentityKey reads the project-root CustomIdentityProvider value. Read-only; nothing modified.")]
        public CallToolResult GetProjectSettings(
            string folderPath="",
            [Description("customIdentityKey: key of the custom identity to read ('' = all).")] string customIdentityKey="",
            int offset=0,
            int limit=100)
            => SecurityToolContract.Invoke("GetProjectSettings", true, false,
                () => _service.ReadProjectSettings(folderPath,customIdentityKey,offset,limit));

        public ResponseMessage ManageProjectLanguage(
            [Description("action: read | activate | deactivate | setEditing | setReference.")] string action="read",
            [Description("culture: exact language tag, e.g. 'en-US', 'zh-CN' (required for every action except read).")] string culture="",
            [Description("dryRun: true (default) previews; false executes.")] bool dryRun=true)
        => _service.ManageProjectLanguage(action,culture,dryRun);

        public ResponseMessage ExportProjectTexts(
            string filePath,
            [Description("sourceCulture: source language tag, e.g. 'en-US'.")] string sourceCulture,
            [Description("targetCulture: target language tag, e.g. 'zh-CN'.")] string targetCulture,
            bool dryRun=true)
        => _service.ExportProjectTexts(filePath,sourceCulture,targetCulture,dryRun);

        public ResponseMessage ImportProjectTexts(
            string filePath,
            [Description("updateSourceLanguage: true also writes the source-language texts.")] bool updateSourceLanguage,
            bool dryRun=true)
        => _service.ImportProjectTexts(filePath,updateSourceLanguage,dryRun);

        [McpServerTool(Name="RunToolTransaction"), Description("[L2][Project][WRITE] Run 1..20 supported synchronous project edits inside one ExclusiveAccess + Transaction(project, text). calls is [{name,arguments:{...}}]. Supported: CreatePlcTypeGroup, DeleteEmptyPlcBlockGroup, ManagePlcUserGroup, ManageDeviceUserGroup, ManageUnifiedHmiGroup, DeleteEmptyUnifiedHmiScreenGroup, SetUnifiedObjectProperties, SetUnifiedMultilingualProperty. Rejects all other tools, including compile, online, session, save, external files and nested orchestration. Forces inner dryRun=false; preflights every call before starting. Commits only with explicit operation success, CanCommit and CommitRequested, and successful disposal. dryRun=true validates arguments only, not native semantics. Real execution needs confirmChange. No save.")]
        public CallToolResult RunToolTransactionV4(
            [Description("calls: Array of {name, arguments:{...}} supported tool calls to run inside one transaction.")] ToolCall[] calls,
            [Description("text: transaction text shown in TIA's undo history.")] string text,
            bool confirmChange=false,
            bool dryRun=true)
            => RunTransaction(calls, text, confirmChange, dryRun);

        private CallToolResult RunTransaction(ToolCall[] calls, string text, bool confirmChange, bool dryRun)
        {
            const string tool = "RunToolTransaction";
            if (calls == null || calls.Length == 0) return V4Reject(tool, InvalidInput("calls"));
            if (calls.Length > 20) return V4Reject(tool, new Error("Transaction count exceeds its limit.", new LimitExceededDetails("calls", 20, calls.Length)));
            if (string.IsNullOrWhiteSpace(text) || text.Length > 200) return V4Reject(tool, InvalidInput("text"));
            if (!dryRun && !confirmChange) return V4Reject(tool, new Error("Transaction execution requires confirmation.", new ConfirmationRequiredDetails(null)));
            var plan = new JsonArray();
            var prepared = new List<ToolCall>();
            foreach (var call in calls)
            {
                if (call == null) return V4Reject(tool, InvalidInput("calls"));
                // Validate the supplied call before applying the transaction's explicit
                // dryRun=false policy, so an invalid supplied value is never erased.
                var error = ToolInvoker.Bind(call.Name, call.Arguments, out _);
                if (error != null) return V4Reject(tool, error);
                if (!TransactionExecution.SupportedTools.Contains(call.Name, StringComparer.OrdinalIgnoreCase))
                    return V4Reject(tool, InvalidInput("calls"));
                var arguments = JsonNode.Parse(call.Arguments.Json.GetRawText())!.AsObject();
                if (CatalogView.Find(call.Name, true)!.DryRun.Present) arguments["dryRun"] = false;
                var preflight = PreflightToolCall(call.Name, arguments.ToJsonString());
                if (preflight.Meta?["ok"]?.GetValue<bool?>() != true)
                    return V4Reject(tool, new Error("Transaction preflight prerequisites are not satisfied.", new PreconditionFailedDetails("transaction-preflight", call.Name)));
                prepared.Add(new ToolCall(call.Name, new ToolArguments(JsonSerializer.SerializeToElement(arguments))));
                plan.Add(new JsonObject { ["name"] = call.Name, ["arguments"] = arguments });
            }
            var meta = ResponseMeta.Basic(DateTime.Now, false, ("dryRun", dryRun), ("mayHaveChanged", false), ("calls", plan), ("text", text));
            if (dryRun)
            {
                meta["success"] = true;
                return SessionToolContract.Map(tool, new ResponseMessage { Message = "Transaction preview: " + calls.Length + " call(s) validated, nothing executed.", Meta = meta }, false, false);
            }
            var results = new JsonArray(); meta["results"] = results;
            try
            {
                bool committed = TransactionExecution.Run(prepared.Count, () => _service.BeginTransaction(text), index =>
                {
                    var call = prepared[index];
                    var payload = ResultBody(DispatchNestedTool(call.Name, call.Arguments));
                    bool ok = payload?["ok"]?.GetValue<bool?>() == true;
                    results.Add(new JsonObject { ["name"] = call.Name, ["ok"] = ok, ["result"] = payload });
                    if (!ok) meta["stoppedAt"] = call.Name;
                    return ok;
                }, meta);
                meta["success"] = committed; meta["operationSuccess"] = committed; meta["apiCallSuccess"] = true;
                return SessionToolContract.Map(tool, new ResponseMessage { Message = committed ? "Transaction committed as one undo unit (" + calls.Length + " call(s)). No save." : "Transaction not committed; project transaction disposed with rollback. Inspect results and commit/cancellation state.", Meta = meta }, true, false);
            }
            catch (Exception ex)
            {
                meta["exceptionType"] = ex.GetType().Name; meta["operationSuccess"] = false; meta["apiCallSuccess"] = false;
                // A throwing begin/commit/dispose cannot prove rollback, even if no
                // inner call returned. Retain all earlier results and do not replay.
                meta["outcome"] = "unknown";
                return SessionToolContract.Map(tool, new ResponseMessage { Message = "Transaction outcome is unconfirmed.", Meta = meta }, true, false);
            }
        }
    }
}
