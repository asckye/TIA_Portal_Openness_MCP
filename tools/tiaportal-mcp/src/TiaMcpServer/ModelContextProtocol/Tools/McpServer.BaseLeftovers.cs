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
        public static ResponseMessage ReadPortalInfo(bool includeProcesses=true, bool includeSessions=true, bool includeProducts=true)
            => Portal.ReadPortalInfo(includeProcesses,includeSessions,includeProducts);

        [McpServerTool(Name="ReadTransferRoutes"), Description("[L2][PLC-Online][READ] The PG/PC route tree of the exact PLC's DownloadProvider.Configuration: Modes (ConfigurationMode.Name) -> PcInterfaces (Name, Number, Addresses, Subnets with Addresses and Gateways, TargetInterfaces with Addresses), plus availability of RHDownloadProvider / RHOnlineProvider (R/H systems, with PrimaryState/BackupState), OnlineProvider (State) and CompileProvider. Nothing is applied or changed; use it to pick pgPcInterface/targetIpAddress for DownloadToPlc / GoOnline.")]
        public static ResponseMessage ReadTransferRoutes(string softwarePath, int maxItems=500)
            => Portal.ReadTransferRoutes(softwarePath,maxItems);

        [McpServerTool(Name="ManageHardwareUtilities"), Description("[L2][Hardware][FILE] Project.HwUtilities: list (Identifier + class), findModuleTypes / findContainerTypes / normalizeTypeIdentifier (ModuleInformationProvider on a typeIdentifier), exportOpcUa (OpcUaExportProvider.Export(PLC DeviceItem, new .xml file)), exportCardReaderPsc (CardReaderPscProvider.Export(Device, new .psc file[, password] - creates the card image; f-activated devices refuse on V18 and below, encryption needs CPU V40.0+). Exports refuse to overwrite; default dryRun=true; the password is never echoed.")]
        public static ResponseMessage ManageHardwareUtilities(string action="list", string typeIdentifier="", string devicePathJson="[]", string itemPathJson="[]", string filePath="", string password="", bool dryRun=true)
            => Portal.ManageHardwareUtilities(action,typeIdentifier,devicePathJson,itemPathJson,filePath,password,dryRun);

        [McpServerTool(Name="ManageDeviceServiceObjects"), Description("[L2][Hardware][WRITE] Typed objects behind three PLC services of the exact hardware object. family=webApplications (DefaultWebPagesFeature.WebApplicationConfigurations: read Name/ApplicationType/IsDefault, setDefault name). family=telecontrolDataPoints (TelecontrolManagement.TelecontrolDataPoints: read Name/DataPointType and DNP3/IEC DataPointIndex/MasterFunction or WDC DataPointIndex, update name + propertiesJson, delete, export/import filePath via ExportDataPoints/ImportDataPoints). family=certificateServices (CertificateManagementConfiguration, PLC families V3.0+: read Usage TIAPortal/Runtime, CertificateExpirationEventActivated, RemainingCertificateLifetime 10..90 % and CertificateSupportedServices Id/ServiceType/ServiceGroupName/ApplicationUri/Guid; update propertiesJson; setServiceGroupName name=<Id or Guid> propertiesJson {ServiceGroupName<=64}; createService; deleteService name=<Id or Guid>). Real changes need confirmChange with dryRun=false; no save/compile/download.")]
        public static ResponseMessage ManageDeviceServiceObjects(string devicePathJson, string itemPathJson, string family, string action="read", string name="", string propertiesJson="{}", string filePath="", bool confirmChange=false, bool dryRun=true)
            => Portal.ManageDeviceServiceObjects(devicePathJson,itemPathJson,family,action,name,propertiesJson,filePath,confirmChange,dryRun);

        [McpServerTool(Name="ReadObjectIdentifier"), Description("[L2][Project][READ] ObjectIdentifierProvider (project service): GetIdentifier of the exact object (kind=device/deviceItem via devicePathJson/itemPathJson, kind=plcBlock/plcType/plcTagTable via softwarePath + objectPath) - a cross-session stable identifier - or, with identifier given, Find(identifier) and describe the object it resolves to (class, name, owner path, ISystemObject flag). Official support: Device, DeviceItem, code/data blocks, PLC tags, software units, TechnologicalInstanceDB, PlcStruct. Read-only.")]
        public static ResponseMessage ReadObjectIdentifier(string kind="device", string devicePathJson="[]", string itemPathJson="[]", string softwarePath="", string objectPath="", string identifier="")
            => Portal.ReadObjectIdentifier(kind,devicePathJson,itemPathJson,softwarePath,objectPath,identifier);

        [McpServerTool(Name="ShowObjectInEditor"), Description("[L2][Project][WRITE] IShowable.ShowInEditor on the exact object (kind=device via devicePathJson, kind=plcBlock/plcType/plcTagTable via softwarePath + objectPath): opens it in the TIA Portal editor for the engineer. UI only - no project data changes; needs a Portal started with user interface. Default dryRun=true.")]
        public static ResponseMessage ShowObjectInEditor(string kind="device", string devicePathJson="[]", string itemPathJson="[]", string softwarePath="", string objectPath="", bool dryRun=true)
            => Portal.ShowObjectInEditor(kind,devicePathJson,itemPathJson,softwarePath,objectPath,dryRun);

        [McpServerTool(Name="RunToolsInTransaction"), Description("[L2][Project][WRITE] Run 1..20 tool calls (callsJson [{\"name\":\"<tool>\",\"arguments\":{...}}]) inside one ExclusiveAccess + Transaction(project, text): TIA groups them into a single undo unit and the transaction is committed (Transaction.CommitOnDispose) only when every call reports success; any failure rolls all of them back on dispose. Inner dryRun arguments are forced to false; CallTool / nested transactions are refused. Reports CanCommit / CommitRequested and each call's result. dryRun=true only validates the call list; real execution needs confirmChange. No save.")]
        public static ResponseMessage RunToolsInTransaction(string callsJson, string text, bool confirmChange=false, bool dryRun=true)
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
                    plan.Add(new JsonObject { ["name"] = call.Name, ["arguments"] = JsonNode.Parse(call.ArgumentsJson) });
                }
                meta["text"] = text;
                if (dryRun) { meta["success"] = true; meta["operationSuccess"] = true; return new ResponseMessage { Message = "Transaction preview: " + calls.Length + " call(s) validated, nothing executed.", Meta = meta }; }
                var results = new JsonArray(); meta["results"] = results; bool allOk = true;
                using (var scope = Portal.BeginTransaction(text))
                {
                    meta["mayHaveChanged"] = true;
                    foreach (var call in calls)
                    {
                        var response = CallTool(call.Name, BaseLeftoversLogic.ForceRealExecution(call.ArgumentsJson));
                        var innerMeta = response.Meta; bool ok = innerMeta?["success"]?.GetValue<bool>() == true && innerMeta?["operationSuccess"]?.GetValue<bool>() != false;
                        JsonNode? payload; try { payload = JsonNode.Parse(response.Message); } catch { payload = response.Message; }
                        results.Add(new JsonObject { ["name"] = call.Name, ["ok"] = ok, ["result"] = payload });
                        if (!ok || scope.IsCancellationRequested) { allOk = false; meta["stoppedAt"] = call.Name; break; }
                    }
                    meta["canCommit"] = scope.CanCommit;
                    if (allOk && scope.CanCommit) scope.Commit();
                    meta["commitRequested"] = scope.CommitRequested;
                }
                meta["committed"] = allOk; meta["success"] = allOk; meta["operationSuccess"] = allOk; meta["apiCallSuccess"] = true;
                return new ResponseMessage { Message = allOk ? "Transaction committed as one undo unit (" + calls.Length + " call(s)). No save." : "Transaction rolled back: a call failed or TIA requested cancellation (see results / stoppedAt). Nothing was kept.", Meta = meta };
            }
            catch (Exception ex)
            {
                meta["error"] = ex.ToString(); meta["operationSuccess"] = false; meta["apiCallSuccess"] = false;
                return new ResponseMessage { Message = "RunToolsInTransaction failed: " + ex.Message, Meta = meta };
            }
        }
    }
}
