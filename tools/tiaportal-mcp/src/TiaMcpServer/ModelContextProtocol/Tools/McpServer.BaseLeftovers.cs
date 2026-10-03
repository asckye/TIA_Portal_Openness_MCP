using System;
using System.ComponentModel;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;
using TiaMcpServer.Siemens;
namespace TiaMcpServer.ModelContextProtocol
{
    public static partial class McpServer
    {
        [McpServerTool(Name="ReadPortalInfo"), Description("[L2][Portal][READ] Diagnostic snapshot of every running TIA Portal process (TiaPortalProcess: Id, Mode WithUserInterface/WithoutUserInterface, Path, ProjectPath, AcquisitionTime; AttachedSessions with Id/Version/IsActive/AttachTime/UtilizationTime/AccessLevel/TrustAuthority/ProcessPath/ProcessId; InstalledSoftware = TiaPortalProduct Name/Version/Options), the bound process, the bound project's TextCategories (Identifier/Name) and HwUtilities (Identifier, class), ObjectIdentifierProvider availability and the explicitly bound project name. Non-blocking, read-only; works without a project.")]
        public static ResponseMessage ReadPortalInfo(
            [Description("includeProcesses: list the TIA Portal processes on the machine.")] bool includeProcesses=true,
            [Description("includeSessions: list the sessions of the bound portal.")] bool includeSessions=true,
            [Description("includeProducts: list the installed TIA products / option packages (TiaPortalProduct).")] bool includeProducts=true)
            => Portal.ReadPortalInfo(includeProcesses,includeSessions,includeProducts);

        [McpServerTool(Name="ReadTransferRoutes"), Description("[L2][PLC-Online][READ] The PG/PC route tree of the exact PLC's DownloadProvider.Configuration: Modes (ConfigurationMode.Name) -> PcInterfaces (Name, Number, Addresses, Subnets with Addresses and Gateways, TargetInterfaces with Addresses), plus availability of RHDownloadProvider / RHOnlineProvider (R/H systems, with PrimaryState/BackupState), OnlineProvider (State) and CompileProvider. Nothing is applied or changed; use it to pick pgPcInterface/targetIpAddress for DownloadToPlc / GoOnline.")]
        public static ResponseMessage ReadTransferRoutes(
            [Description("softwarePath: PLC software path from GetProjectTree, e.g. 'PLC_1'.")] string softwarePath,
            [Description("maxItems: cap on route-tree rows returned.")] int maxItems=500)
            => Portal.ReadTransferRoutes(softwarePath,maxItems);

        [McpServerTool(Name="ManageHardwareUtilities"), Description("[L2][Hardware][FILE] Project.HwUtilities: list (Identifier + class), findModuleTypes / findContainerTypes / normalizeTypeIdentifier (ModuleInformationProvider on a typeIdentifier), exportOpcUa (OpcUaExportProvider.Export(PLC DeviceItem, new .xml file)), exportCardReaderPsc (CardReaderPscProvider.Export(Device, new .psc file[, password] - creates the card image; f-activated devices refuse on V18 and below, encryption needs CPU V40.0+). Exports refuse to overwrite; default dryRun=true; the password is never echoed.")]
        public static ResponseMessage ManageHardwareUtilities(
            [Description("action: the operation to perform - list | findModuleTypes | findContainerTypes | normalizeTypeIdentifier | exportOpcUa | exportCardReaderPsc.")] string action="list",
            [Description("typeIdentifier: catalog type identifier of the form 'OrderNumber:6ES7 ...' or 'OrderNumber:.../V2.9' (SearchHardwareCatalog / ManageHardwareUtilities normalizeTypeIdentifier).")] string typeIdentifier="",
            string devicePathJson="[]",
            string itemPathJson="[]",
            string filePath="",
            string password="",
            bool dryRun=true)
            => Portal.ManageHardwareUtilities(action,typeIdentifier,devicePathJson,itemPathJson,filePath,password,dryRun);

        [McpServerTool(Name="ReadObjectIdentifier"), Description("[L2][Project][READ] ObjectIdentifierProvider (project service): GetIdentifier of the exact object (kind=device/deviceItem via devicePathJson/itemPathJson, kind=plcBlock/plcType/plcTagTable via softwarePath + objectPath) - a cross-session stable identifier - or, with identifier given, Find(identifier) and describe the object it resolves to (class, name, owner path, ISystemObject flag). Official support: Device, DeviceItem, code/data blocks, PLC tags, software units, TechnologicalInstanceDB, PlcStruct. Read-only.")]
        public static ResponseMessage ReadObjectIdentifier(
            [Description("kind: device | deviceItem | plcBlock | plcType | plcTagTable.")] string kind="device",
            string devicePathJson="[]",
            string itemPathJson="[]",
            string softwarePath="",
            string objectPath="",
            [Description("identifier: exact identifier of the object as the read action lists it.")] string identifier="")
            => Portal.ReadObjectIdentifier(kind,devicePathJson,itemPathJson,softwarePath,objectPath,identifier);

        [McpServerTool(Name="ShowObjectInEditor"), Description("[L2][Project][WRITE] IShowable.ShowInEditor on the exact object (kind=device via devicePathJson, kind=plcBlock/plcType/plcTagTable via softwarePath + objectPath): opens it in the TIA Portal editor for the engineer. UI only - no project data changes; needs a Portal started with user interface. Default dryRun=true.")]
        public static ResponseMessage ShowObjectInEditor(
            [Description("kind: device | deviceItem | plcBlock | plcType | plcTagTable.")] string kind="device",
            string devicePathJson="[]",
            string itemPathJson="[]",
            string softwarePath="",
            string objectPath="",
            bool dryRun=true)
            => Portal.ShowObjectInEditor(kind,devicePathJson,itemPathJson,softwarePath,objectPath,dryRun);

        [McpServerTool(Name="RunToolsInTransaction"), Description("[L2][Project][WRITE] Run 1..20 supported synchronous project edits inside one ExclusiveAccess + Transaction(project, text). callsJson is [{name,arguments:{...}}]. Supported: CreatePlcTypeGroup, DeleteEmptyPlcBlockGroup, ManagePlcUserGroup, ManageDeviceUserGroup, ManageUnifiedHmiGroup, DeleteEmptyUnifiedHmiScreenGroup, UpdateUnifiedObjectProperties, UpdateUnifiedMultilingualProperty. Rejects all other tools, including compile, online, session, save, external files and nested orchestration. Forces inner dryRun=false; preflights every call before starting. Commits only with explicit operation success, CanCommit and CommitRequested, and successful disposal. dryRun=true validates arguments only, not native semantics. Real execution needs confirmChange. No save.")]
        public static ResponseMessage RunToolsInTransaction(
            [Description("callsJson: JSON array of {name, arguments:{...}} supported tool calls to run inside one transaction.")] string callsJson,
            [Description("text: transaction text shown in TIA's undo history.")] string text,
            bool confirmChange=false,
            bool dryRun=true)
        {
            var meta = new JsonObject { ["timestamp"] = DateTime.Now, ["tool"] = "RunToolsInTransaction", ["success"] = false, ["dryRun"] = dryRun, ["mayHaveChanged"] = false };
            try
            {
                var calls = BaseLeftoversLogic.ParseToolCalls(callsJson);
                BaseLeftoversLogic.ValidateTransactionRequest(text, calls, confirmChange, dryRun);
                var all = AllToolMethods(); var plan = new JsonArray(); meta["calls"] = plan;
                foreach (var call in calls)
                {
                    if (!all.ContainsKey(call.Name)) throw new ArgumentException("No tool named '" + call.Name + "'.");
                    TransactionExecution.RequireSupported(call.Name);
                    call.ArgumentsJson = BaseLeftoversLogic.ForceRealExecution(call.ArgumentsJson);
                    var preflight = PreflightToolCall(call.Name, call.ArgumentsJson);
                    if (preflight.Meta?["ok"]?.GetValue<bool?>() != true)
                        throw new ArgumentException("Preflight failed for " + call.Name + ": " + preflight.Message);
                    plan.Add(new JsonObject { ["name"] = call.Name, ["arguments"] = JsonNode.Parse(call.ArgumentsJson) });
                }
                meta["text"] = text;
                if (dryRun) { meta["success"] = true; meta["operationSuccess"] = true; return new ResponseMessage { Message = "Transaction preview: " + calls.Length + " call(s) validated, nothing executed.", Meta = meta }; }
                var results = new JsonArray(); meta["results"] = results;
                bool committed = TransactionExecution.Run(calls.Length, () => Portal.BeginTransaction(text), index =>
                {
                    var call = calls[index];
                    var response = CallTool(call.Name, call.ArgumentsJson);
                    bool ok = response.Meta?["operationSuccess"]?.GetValue<bool?>() == true;
                    JsonNode? payload; try { payload = JsonNode.Parse(response.Message); } catch { payload = response.Message; }
                    results.Add(new JsonObject { ["name"] = call.Name, ["ok"] = ok, ["result"] = payload });
                    if (!ok) meta["stoppedAt"] = call.Name;
                    return ok;
                }, meta);
                meta["success"] = committed; meta["operationSuccess"] = committed; meta["apiCallSuccess"] = true;
                return new ResponseMessage { Message = committed ? "Transaction committed as one undo unit (" + calls.Length + " call(s)). No save." : "Transaction not committed; project transaction disposed with rollback. Inspect results and commit/cancellation state.", Meta = meta };
            }
            catch (Exception ex)
            {
                meta["error"] = ex.ToString(); meta["operationSuccess"] = false; meta["apiCallSuccess"] = false;
                return new ResponseMessage { Message = "RunToolsInTransaction failed: " + ex.Message, Meta = meta };
            }
        }
    }
}
