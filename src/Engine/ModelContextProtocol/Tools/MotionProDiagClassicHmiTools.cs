using ModelContextProtocol.Protocol;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcpServer.Siemens;
using TiaMcp.Logic.V4;
using TiaMcp.Logic.V4.Domain;
using TiaMcp.Logic.V4.Inputs;
using TiaMcpServer.Siemens.Services;
using System.ComponentModel;
using ModelContextProtocol.Server;
namespace TiaMcpServer.ModelContextProtocol
{
    // F3 adapter for this group's RunHmiStepTool results. Structural admission
    // belongs to the shared typed boundary; the services retain native validation.
    internal static class ClassicMotionToolContract
    {
        internal static string Json<T>(T value) => value == null ? "{}" : V4Json.Serialize(value);

        internal static string Capture(JsonObject meta, Func<JsonObject, string> action)
        {
            meta["mayHaveChanged"] = false;
            meta["mayHaveWrittenFiles"] = false;
            try { return action(meta); }
            catch (Exception ex)
            {
                var cause = ex.GetBaseException();
                meta["v4FailureCode"] = cause is NotSupportedException ? "UNSUPPORTED_CAPABILITY"
                    : cause is ArgumentException ? "INVALID_ARGUMENT"
                    : cause is FileNotFoundException || cause is DirectoryNotFoundException ? "NOT_FOUND"
                    : cause is PortalException portal ? portal.Code == PortalErrorCode.InvalidState ? "PRECONDITION_FAILED" : portal.Code.ToString()
                    : cause is InvalidOperationException ? "PRECONDITION_FAILED" : "NATIVE_OPERATION_FAILED";
                throw;
            }
        }

        internal static CallToolResult Invoke(string tool, bool readOnly, bool current, Func<ResponseMessage> operation)
        {
            try { return Map(tool, operation(), readOnly, current); }
            catch (Exception) /* swallow(privacy): no native outcome is confirmed by an escaped exception */
            {
                return Result(tool, null, readOnly ? Failure() : Unknown(), readOnly ? Outcome.ReadFailed : Outcome.Unknown,
                    readOnly ? Execution.ReadOnly : Execution.Unknown, Completeness.Unknown, current);
            }
        }

        private static bool? Flag(JsonObject data, string key)
            => data[key] is JsonValue value && value.TryGetValue<bool>(out var flag) ? flag : (bool?)null;

        internal static CallToolResult Map(string tool, ResponseMessage response, bool readOnly, bool current)
        {
            var data = response.Meta == null ? new JsonObject() : (JsonObject)response.Meta.DeepClone();
            var success = data.ContainsKey("operationSuccess") ? Flag(data, "operationSuccess") : Flag(data, "success");
            bool issued = Flag(data, "mayHaveChanged") == true || Flag(data, "mayHaveWrittenFiles") == true;
            bool unknown = Flag(data, "mappingVerified") == false || Flag(data, "requiresExplicitRebind") == true
                || Flag(data, "requiresSessionReset") == true;
            string status = data["v4FailureCode"]?.ToString() ?? data["status"]?.ToString() ?? "";
            var refusal = Refusal(tool, status);
            Outcome outcome; Execution execution; Error? error;
            if (issued && (success != true || unknown))
            { outcome = Outcome.Unknown; execution = Execution.Unknown; error = Unknown(); }
            else if (refusal != null)
            { outcome = Outcome.RejectedBeforeOperation; execution = Execution.NotStarted; error = refusal; }
            else if (success == true && !unknown)
            { outcome = Outcome.Succeeded; execution = readOnly ? Execution.ReadOnly : Execution.Completed; error = null; }
            else if (readOnly)
            { outcome = Outcome.ReadFailed; execution = Execution.ReadOnly; error = Failure(); }
            else if (Flag(data, "mayHaveChanged") == false && Flag(data, "mayHaveWrittenFiles") == false)
            { outcome = Outcome.RejectedBeforeOperation; execution = Execution.NotStarted; error = new Error("The operation stopped before a write was issued.", new PreconditionFailedDetails("classic-motion-before-write", null)); }
            else
            { outcome = Outcome.Unknown; execution = Execution.Unknown; error = Unknown(); }

            bool incomplete = Incomplete(data);
            Paging? paging = null;
            if (data["offset"] is JsonValue offset && offset.TryGetValue<int>(out int start)
                && data["limit"] is JsonValue limit && limit.TryGetValue<int>(out int size)
                && data["total"] is JsonValue total && total.TryGetValue<int>(out int count) && size > 0 && start >= 0)
                paging = McpServer.OffsetPage(start, size, count);
            if (outcome == Outcome.Succeeded) data["summary"] = response.Message;
            foreach (string key in new[] { "timestamp", "tool", "success", "v4FailureCode", "offset", "limit", "nextOffset" }) data.Remove(key);
            Sanitize(data);
            var completeness = outcome == Outcome.RejectedBeforeOperation ? Completeness.None
                : outcome == Outcome.Unknown || outcome == Outcome.ReadFailed ? Completeness.Unknown
                : incomplete ? Completeness.Partial : Completeness.Complete;
            return Result(tool, data, error, outcome, execution, completeness, current, paging);
        }

        private static Error? Refusal(string tool, string status)
        {
            switch (status)
            {
                case "InvalidState": return new Error("No project is bound.", new ProjectNotBoundDetails());
                case "HmiReadSessionBlocked": return new Error("The session requires an explicit reset.", new SessionResetRequiredDetails("classic-motion-session"));
                case "NotFound": case "NOT_FOUND": return new Error("The requested target was not found.", new NotFoundDetails(null));
                case "NotSupportedOnVersion": case "NativeCrashRiskBlocked": case "UNSUPPORTED_CAPABILITY": return new Error("The target or version does not support this capability.", new UnsupportedCapabilityDetails(McpServer.ReleaseKey, tool, null));
                case "InvalidParams": case "INVALID_ARGUMENT": return McpServer.InvalidInput("arguments");
                case "PRECONDITION_FAILED": return new Error("The operation's preconditions were not satisfied.", new PreconditionFailedDetails("classic-motion", null));
                default: return null;
            }
        }

        private static bool Incomplete(JsonNode? node)
        {
            if (node is JsonArray array) return array.Any(Incomplete);
            if (!(node is JsonObject obj)) return false;
            foreach (var pair in new[] { ("screenCount", "screens"), ("popupCount", "screenPopups"), ("templateCount", "screenTemplates"),
                ("tagTableCount", "tagTables"), ("scriptCount", "vbScripts"), ("count", "records") })
                if (obj[pair.Item1] is JsonValue count && count.TryGetValue<int>(out int total) && obj[pair.Item2] is JsonArray items && items.Count < total) return true;
            return Flag(obj, "dataComplete") == false || obj.Any(p =>
                p.Key.EndsWith("Truncated", StringComparison.OrdinalIgnoreCase) && p.Value?.ToString() == "true"
                || p.Key.EndsWith("Error", StringComparison.OrdinalIgnoreCase) && p.Value != null
                || (p.Key == "failures" || p.Key == "attributeErrors" || p.Key == "excludedComplexProperties") && p.Value is JsonArray failures && failures.Count > 0
                || Incomplete(p.Value));
        }

        private static void Sanitize(JsonNode? node)
        {
            if (node is JsonArray array) { foreach (var item in array) Sanitize(item); return; }
            if (!(node is JsonObject obj)) return;
            foreach (var pair in obj.ToArray())
            {
                if (pair.Key == "error" || pair.Key.EndsWith("Error", StringComparison.Ordinal))
                    obj[pair.Key] = "Native observation failed; details are available in the local diagnostic log.";
                else if (pair.Key == "messageData" || pair.Key == "detailMessageData" || pair.Key == "lastFailure") obj.Remove(pair.Key);
                else Sanitize(pair.Value);
            }
        }

        private static Error Failure() => new Error("The operation did not establish a successful result.", new NativeOperationFailedDetails(null, null, new Dictionary<string, JsonElement>()));
        private static Error Unknown() => new Error("The write outcome is unknown. Inspect the retained evidence and reset the session before further writes.", new OutcomeUnknownDetails("classic-motion-operation", new Dictionary<string, JsonElement>()));

        private static CallToolResult Result(string tool, JsonObject? data, Error? error, Outcome outcome, Execution execution,
            Completeness completeness, bool current, Paging? paging = null)
        {
            var warnings = new List<Warning>();
            if (current) warnings.Add(new Warning(WarningCode.UnverifiedBehavior, "Native behavior retains the current policy; V4 native acceptance is pending.", new Dictionary<string, JsonElement>()));
            if (completeness == Completeness.Partial) warnings.Add(new Warning(WarningCode.IncompleteData, "Only the reported scope and available fields were observed.", new Dictionary<string, JsonElement>()));
            var meta = new Meta(DateTimeOffset.UtcNow, McpServer.ReleaseKey, tool, Meta.Correlate(InvocationJournal.CorrelationId), outcome, execution,
                outcome == Outcome.Unknown || error?.Code == ErrorCode.SessionResetRequired, current ? BehaviorPolicy.Current : BehaviorPolicy.NotApplicable, completeness, paging, warnings);
            var mapped = McpResult.From(Envelope.Create(data, error, meta));
            return new CallToolResult { IsError = mapped.IsError, StructuredContent = JsonNode.Parse(mapped.StructuredContent.GetRawText()),
                Content = new[] { new TextContentBlock { Text = mapped.Content[0].Text } } };
        }
    }

    [McpServerToolType]
    internal sealed class MotionProDiagClassicHmiTools
    {
        private readonly MotionProDiagClassicHmiService _motionProDiagClassicHmi;

        public MotionProDiagClassicHmiTools(MotionProDiagClassicHmiService service) => _motionProDiagClassicHmi = service;

        [McpServerTool(Name="GetMotionAxisConfiguration"), Description("[L2][PLC-TechnologyObjects][READ] Exact Motion technology object (axis/cam/kinematics/output cam/measuring input/ident) read: scalar object values plus every native Motion/Ident service it provides (hardware interfaces, master-value couplings, interpreter mappings, cam/interpreter capabilities) and, under typed, the same data as typed rows (AxisHardwareConnectionProvider actor / sensors / torque, encoder, measuring input, output cam, master values, V21 TOMapping / DBMemberMapping / Ident). Absent services are reported by state. Optional paginated parameters. No drive or motion command.")]
        public CallToolResult GetMotionAxisConfiguration(
            string softwarePath,
            string objectPath,
            [Description("includeParameters: true also returns parameters.")] bool includeParameters=false,
            int offset=0,
            int limit=100)
            => ClassicMotionToolContract.Invoke("GetMotionAxisConfiguration", true, false,
                () => _motionProDiagClassicHmi.ReadMotionAxisConfiguration(softwarePath,objectPath,includeParameters,offset,limit));
        [McpServerTool(Name="ManageMotionAxis"), Description("[L2][PLC-TechnologyObjects][WRITE] Native Motion TO operations beyond cam/bit-address tools: add/removeMasterValue (aspect synchronous*/conveyor*/superimposingSetPoint, name=exact master TO path), create/update/deleteMapping (aspect toMapping/dbMemberMapping, name=alias, properties), connect/disconnect (aspect actor/sensor/torque/encoder/measuringInput/outputCam; target selects exactly one native overload: devicePath+itemPath[+secondItemPath|channelIndex|channelType+channelIoType+channelNumber for Connect(Channel)], dbMemberPath, plcTagPath, address, or encoder bit addresses; typed AxisEncoderHardwareConnectionInterface / TorqueHardwareConnectionInterface / MeasuringInput / OutputCam calls with typed readback rows), connectIdent (deviceItem target). Exact names; Offline PLC for real writes; deletes need confirmDelete=true. Default preview; native readback; no save/compile/download, never commands a drive. Native behaviorPolicy=current; V4 native acceptance is pending.")]
        public CallToolResult ManageMotionAxis(
            string softwarePath,
            string objectPath,
            [Description("read | addMasterValue | removeMasterValue | createMapping | updateMapping | deleteMapping | connect | disconnect | connectIdent. ")] string action,
            [Description("aspect: which aspect of the axis to manage (see the tool description).")] string aspect="",
            string name="",
            [Description("target: Object naming the target (see the tool description).")] MotionTarget target = null!,
            AttributeMap<Scalar> properties = null!,
            int sensorIndex=0,
            bool confirmDelete=false,
            bool dryRun=true)
            => ClassicMotionToolContract.Invoke("ManageMotionAxis", dryRun || action == "read", action != "read",
                () => _motionProDiagClassicHmi.ManageMotionAxis(softwarePath,objectPath,action,aspect,name,ClassicMotionToolContract.Json(target),ClassicMotionToolContract.Json(properties),sensorIndex,confirmDelete,dryRun));
        [McpServerTool(Name="ManagePlcSupervision"), Description("[L2][PLC-Software][WRITE] ProDiag native object access on an exact PLC or block: read provider metadata (attributes, advertised compositions), readComposition/createEntry/deleteEntry through the official IEngineeringObject composition API (typeName must be advertised by GetCreationInfos), setAttributes on the provider, exportSettings/importSettings (.dat) via SupervisionSettingsProvider. Openness exposes no typed supervision list; XLSX bulk exchange is ExchangePlcSupervisions. Offline PLC for real writes; deletes need confirmDelete=true. Default preview; no save/compile/download. Native behaviorPolicy=current; V4 native acceptance is pending.")]
        public CallToolResult ManagePlcSupervision(
            string softwarePath,
            [Description("read | readComposition | createEntry | deleteEntry | setAttributes | exportSettings | importSettings. ")] string action,
            string blockPath="",
            [Description("providerKind: supervision | settings.")] string providerKind="supervision",
            [Description("compositionName: exact name of the composition (collection) on the object.")] string compositionName="",
            [Description("entryName: exact name of the entry inside the composition.")] string entryName="",
            [Description("typeName: exact type name of the entry to create.")] string typeName="",
            string filePath="",
            AttributeMap<Scalar> attributes = null!,
            int offset=0,
            int limit=100,
            bool confirmDelete=false,
            bool dryRun=true)
            => ClassicMotionToolContract.Invoke("ManagePlcSupervision", dryRun || action == "read" || action == "readComposition", action != "read" && action != "readComposition",
                () => _motionProDiagClassicHmi.ManagePlcSupervision(softwarePath,action,blockPath,providerKind,compositionName,entryName,typeName,filePath,ClassicMotionToolContract.Json(attributes),offset,limit,confirmDelete,dryRun));
        [McpServerTool(Name="ListClassicHmiScripts"), Description("[L2][HMI-Classic][READ] Classic (non-Unified) WinCC VB scripts under an exact script folder path: folder tree, script names and every readable attribute advertised by the object. Script source is not a typed Openness property; export it with ManageClassicHmiScript. Live pagination; Unified targets are refused.")]
        public CallToolResult ListClassicHmiScripts(string softwarePath,string folderPath="",int offset=0,int limit=100)
            => ClassicMotionToolContract.Invoke("ListClassicHmiScripts", true, false,
                () => _motionProDiagClassicHmi.ReadClassicHmiScripts(softwarePath,folderPath,offset,limit));
        [McpServerTool(Name="ManageClassicHmiScript"), Description("[L2][HMI-Classic][WRITE] Exact classic VB script or folder: read, export (new XML file, hashed), import (scriptPath=target folder; Override needs confirmDelete=true), delete (confirmDelete=true), createFolder/deleteFolder (empty only), setAttributes (advertised writable attributes, readback verified). No Create-from-code exists natively; new scripts come from XML import. Default preview; no save/compile/download. Native behaviorPolicy=current; V4 native acceptance is pending.")]
        public CallToolResult ManageClassicHmiScript(
            string softwarePath,
            [Description("scriptPath: 'Folder/Script' path of the script.")] string scriptPath,
            [Description("read | export | import | delete | createFolder | deleteFolder | setAttributes. ")] string action,
            string filePath="",
            string importOptions="None",
            AttributeMap<Scalar> attributes = null!,
            bool confirmDelete=false,
            bool dryRun=true)
            => ClassicMotionToolContract.Invoke("ManageClassicHmiScript", dryRun || action == "read", action != "read",
                () => _motionProDiagClassicHmi.ManageClassicHmiScript(softwarePath,scriptPath,action,filePath,importOptions,ClassicMotionToolContract.Json(attributes),confirmDelete,dryRun));
        [McpServerTool(Name="ManageClassicHmiCycle"), Description("[L2][HMI-Classic][WRITE] Classic HMI cycles: read (all or exact cycleName with advertised time/unit attributes), export (new XML file), import (Override needs confirmDelete=true), delete (confirmDelete=true; system cycles refused), setAttributes (readback verified). CycleComposition has no native Create. Default preview; no save/compile/download. Native behaviorPolicy=current; V4 native acceptance is pending.")]
        public CallToolResult ManageClassicHmiCycle(
            string softwarePath,
            [Description("read | export | import | delete | setAttributes. ")] string action,
            [Description("cycleName: exact cycle name.")] string cycleName="",
            string filePath="",
            string importOptions="None",
            AttributeMap<Scalar> attributes = null!,
            int offset=0,
            int limit=100,
            bool confirmDelete=false,
            bool dryRun=true)
            => ClassicMotionToolContract.Invoke("ManageClassicHmiCycle", dryRun || action == "read", action != "read",
                () => _motionProDiagClassicHmi.ManageClassicHmiCycle(softwarePath,action,cycleName,filePath,importOptions,ClassicMotionToolContract.Json(attributes),offset,limit,confirmDelete,dryRun));
        [McpServerTool(Name="ManageClassicHmiTextGraphicList"), Description("[L2][HMI-Classic][WRITE] Classic HMI text/graphic lists (listKind text/graphic): read (all or exact listName with advertised compositions), readEntries/createEntry/deleteEntry through an advertised compositionName (typeName from GetCreationInfos), export, import (Override needs confirmDelete=true), delete (confirmDelete=true), setAttributes. No native list Create; new lists arrive by XML import. Default preview; no save/compile/download. Native behaviorPolicy=current; V4 native acceptance is pending.")]
        public CallToolResult ManageClassicHmiTextGraphicList(
            string softwarePath,
            [Description("listKind: text | graphic.")] string listKind,
            [Description("read | readEntries | createEntry | deleteEntry | export | import | delete | setAttributes. ")] string action,
            [Description("listName: exact list name.")] string listName="",
            [Description("compositionName: exact name of the composition (collection) on the object.")] string compositionName="",
            [Description("entryName: exact name of the entry inside the composition.")] string entryName="",
            [Description("typeName: exact type name of the entry to create.")] string typeName="",
            string filePath="",
            string importOptions="None",
            AttributeMap<Scalar> attributes = null!,
            int offset=0,
            int limit=100,
            bool confirmDelete=false,
            bool dryRun=true)
            => ClassicMotionToolContract.Invoke("ManageClassicHmiTextGraphicList", dryRun || action == "read" || action == "readEntries", action != "read" && action != "readEntries",
                () => _motionProDiagClassicHmi.ManageClassicHmiTextGraphicList(softwarePath,listKind,action,listName,compositionName,entryName,typeName,filePath,importOptions,ClassicMotionToolContract.Json(attributes),offset,limit,confirmDelete,dryRun));
        [McpServerTool(Name="GetClassicHmiGlobalization"), Description("[L2][HMI-Classic][READ] Classic HMI multilingual graphics via the native GraphicsProvider service: names, scalar properties and advertised attributes with live pagination. NotSupported on V20 (service type absent). Image bytes are not read.")]
        public CallToolResult GetClassicHmiGlobalization(string softwarePath,int offset=0,int limit=100)
            => ClassicMotionToolContract.Invoke("GetClassicHmiGlobalization", true, false,
                () => _motionProDiagClassicHmi.ReadClassicHmiGlobalization(softwarePath,offset,limit));
        [McpServerTool(Name="ListClassicHmiFaceplates"), Description("[L2][HMI-Classic][READ] Classic HMI faceplate (or vbScript/cScript/all) library types with versions from the project library or an exact open global library, under an exact type folder path. Scalar properties only; live pagination; no instantiation.")]
        public CallToolResult ListClassicHmiFaceplates(
            [Description("kind: faceplate | vbScript | cScript | all.")] string kind="faceplate",
            string libraryName="",
            string folderPath="",
            int offset=0,
            int limit=100)
            => ClassicMotionToolContract.Invoke("ListClassicHmiFaceplates", true, false,
                () => _motionProDiagClassicHmi.ReadClassicHmiFaceplates(kind,libraryName,folderPath,offset,limit));
        [McpServerTool(Name="ExportPlcProDiagInfo"), Description("[L2][PLC-Software][READ] Native CodeBlock.ExportProDIAGInfo: writes the alarm messages of one exact ProDiag FB (blockPath under BlockGroup or a unit's BlockGroup) as CSV files into an existing directoryPath on the TIA Portal machine and hashes the new files. Refused before the call unless the block is a ProDiag FB and consistent (compile first). Default dryRun=true; no project change. Native behaviorPolicy=current; V4 native acceptance is pending.")]
        public CallToolResult ExportPlcProDiagInfo(
            string softwarePath,
            string blockPath,
            [Description("directoryPath: folder on the TIA machine that receives the files.")] string directoryPath,
            string unitName="",
            [Description("unitKind: which unit collection unitName refers to - unit | safety.")] string unitKind="unit",
            bool dryRun=true)
            => ClassicMotionToolContract.Invoke("ExportPlcProDiagInfo", dryRun, true,
                () => _motionProDiagClassicHmi.ExportPlcProDiagInfo(softwarePath,blockPath,directoryPath,unitName,unitKind,dryRun));

        [McpServerTool(Name="ExchangeMotionCamData"), Description("[L2][PLC-TechnologyObjects][WRITE] Native cam text/binary/point-list export and text/binary import. Explicit native format/separator, new output file, default preview. Import requires Offline. No drive/motion command. Native behaviorPolicy=current; V4 native acceptance is pending.")]
        public CallToolResult ExchangeMotionCamData(
            string softwarePath,
            string objectPath,
            [Description("export | import | exportBinary | importBinary | exportPoints. Text, native binary or point-list exchange.")] string action,
            string filePath,
            [Description("MCD | Scout | PointList. Native CamDataFormat for text export.")] string format="",
            [Description("Comma | Tab. Native CamDataFormatSeparator name for text import/export and point lists; do not pass a literal separator character.")] string separator="",
            [Description("pointCount: number of points to export.")] int pointCount=0,
            bool dryRun=true)
            => ClassicMotionToolContract.Invoke("ExchangeMotionCamData", dryRun, true,
                () => _motionProDiagClassicHmi.ExchangeMotionCamData(softwarePath,objectPath,action,filePath,format,separator,pointCount,dryRun));

        [McpServerTool(Name="ConfigureMotionHardwareConnection"), Description("[L2][PLC-TechnologyObjects][WRITE] Offline axis actor/sensor/torque hardware mapping read/connect/disconnect. Addresses are BIT addresses. Explicit sensor index; default preview. Native readback, no live drive or motion command. Native behaviorPolicy=current; V4 native acceptance is pending.")]
        public CallToolResult ConfigureMotionHardwareConnection(
            string softwarePath,
            string objectPath,
            [Description("interfaceKind: actor | sensor | torque.")] string interfaceKind,
            [Description("action: the operation to perform - read | connect | disconnect.")] string action,
            [Description("Nonnegative integer input BIT address; e.g. byte 10 bit 0 is 80.")] int inputBitAddress=0,
            [Description("Nonnegative integer output BIT address; e.g. byte 10 bit 0 is 80.")] int outputBitAddress=0,
            [Description("connectOption: connect option name (Default or AllowAllModules).")] string connectOption="Default",
            int sensorIndex=0,
            bool dryRun=true)
            => ClassicMotionToolContract.Invoke("ConfigureMotionHardwareConnection", dryRun || action == "read", action != "read",
                () => _motionProDiagClassicHmi.ConfigureMotionHardwareConnection(softwarePath,objectPath,interfaceKind,action,inputBitAddress,outputBitAddress,connectOption,sensorIndex,dryRun));
    }
}
