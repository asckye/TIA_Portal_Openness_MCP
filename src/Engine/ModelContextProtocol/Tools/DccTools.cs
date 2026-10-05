using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Logic.V4;
using TiaMcp.Logic.V4.Inputs;
using TiaMcp.Logic.V4.Domain;
using ModelContextProtocol.Protocol;
using TiaMcpServer.Siemens;
using System.ComponentModel;
using ModelContextProtocol.Server;
using TiaMcpServer.Siemens.Services;
namespace TiaMcpServer.ModelContextProtocol
{
    // The option-package boundary preserves the services' execution and observation evidence.
    // Shared typed admission owns wire shapes; these checks retain action-specific native limits.
    internal static class DriveToolContract
    {
        private sealed class InputFailure : Exception
        {
            internal Error Error { get; }
            internal InputFailure(Error error) => Error = error;
        }

        private static T Input<T>(InputContract<T> contract, T value, string parameter)
        {
            var result = contract.Validate(value, parameter);
            if (result.Error != null) throw new InputFailure(result.Error);
            return result.Value!;
        }

        internal static string Path(string[] value, string parameter, bool optional)
            => V4Json.Serialize(Input(PathValidator.Create(parameter == "itemPath" || optional),
                value ?? (optional ? Array.Empty<string>() : null!), parameter));

        internal static string Attributes(AttributeMap<Scalar>? value)
            => V4Json.Serialize(value ?? new AttributeMap<Scalar>(new Dictionary<string, Scalar>()));

        internal static string ParameterValue(NativeValue value, string action)
        {
            if (action == "write") value = Input(NativeValueValidator.Create(NativeValueValidator.DriveParameter()), value, "value");
            return value.Kind == JsonValueKind.Undefined ? "null" : V4Json.Serialize(value);
        }

        internal static string FunctionValue(NativeValue value, string action)
        {
            if (value.Kind == JsonValueKind.Undefined) value = V4Json.Deserialize<NativeValue>("{}");
            NativeValuePolicy? policy = null;
            Check(() => policy = DriveFunctionPolicy.Create(action));
            return V4Json.Serialize(Input(NativeValueValidator.Create(policy!), value, "value"));
        }

        internal static void Check(Action validation)
        {
            try { validation(); }
            catch (ArgumentException) /* swallow(privacy): parser messages may contain client values or credentials */
            { throw new InputFailure(McpServer.InvalidInput("arguments")); }
        }

        internal static CallToolResult Run(string tool, bool writes, bool current, Func<ResponseMessage> operation,
            int? offset = null, int? limit = null)
        {
            try { return Map(tool, operation(), writes, current, offset, limit); }
            catch (InputFailure ex)
            { return Result(tool, null, ex.Error, Outcome.RejectedBeforeOperation, Completeness.None, writes, current); }
            catch (Exception) /* swallow(privacy): an escaped exception cannot establish the outcome of an issued write */
            { return Result(tool, null, writes ? Unknown() : NativeFailure(), writes ? Outcome.Unknown : Outcome.ReadFailed, Completeness.Unknown, writes, current); }
        }

        private static bool? Flag(JsonObject value, string key)
            => value[key] is JsonValue node && node.TryGetValue<bool>(out var flag) ? flag : (bool?)null;

        internal static CallToolResult Map(string tool, ResponseMessage response, bool writes, bool current,
            int? offset = null, int? limit = null)
        {
            var evidence = response.Meta == null ? new JsonObject() : (JsonObject)response.Meta.DeepClone();
            bool? success = Flag(evidence, "operationSuccess") ?? Flag(evidence, "success");
            if (Flag(evidence, "success") == false || Flag(evidence, "result") == false) success = false;
            bool issued = Flag(evidence, "mayHaveChanged") == true || Flag(evidence, "mayHaveWrittenFiles") == true;
            bool reset = Flag(evidence, "requiresExplicitRebind") == true || Flag(evidence, "requiresSessionReset") == true;
            bool knownFalse = Flag(evidence, "result") == false && Flag(evidence, "apiCallSuccess") != false;
            bool unknown = writes && (reset && issued || Flag(evidence, "verified") == false || Flag(evidence, "verifiedAbsent") == false);
            int applied = (evidence["appliedEntries"] as JsonArray)?.Count ?? 0;
            string status = evidence["status"]?.ToString() ?? "";
            bool projectMissing = status == "InvalidState" && !evidence.ContainsKey("apiCallSuccess") && !issued;
            Outcome outcome; Error? error = null;
            if (unknown || writes && issued && success != true && !knownFalse)
            { outcome = Outcome.Unknown; error = Unknown(); }
            else if (writes && knownFalse)
            {
                outcome = applied > 0 ? Outcome.Partial : Outcome.Failed;
                error = applied > 0 ? new Error("Configuration entries were applied before the native operation returned false.",
                    new PartialFailureDetails(applied, 1, 0)) : NativeFailure();
            }
            else if (projectMissing)
            { outcome = Outcome.RejectedBeforeOperation; error = new Error("No project is bound.", new ProjectNotBoundDetails()); }
            else if (!issued && status == "HmiReadSessionBlocked")
            { outcome = Outcome.RejectedBeforeOperation; error = new Error("The session requires an explicit reset.", new SessionResetRequiredDetails("option-package-session")); }
            else if (!issued && (status == "NotFound" || status == "NotSupportedOnVersion" || status == "InvalidParams" || status == "InvalidState"))
            {
                outcome = Outcome.RejectedBeforeOperation;
                ErrorDetails details = status == "NotFound" ? new NotFoundDetails(null)
                    : status == "NotSupportedOnVersion" ? new UnsupportedCapabilityDetails(McpServer.ReleaseKey, tool, null)
                    : status == "InvalidParams" ? new InvalidArgumentDetails("arguments", Array.Empty<string>())
                    : new PreconditionFailedDetails("option-package-prerequisites", null);
                error = new Error("The option-package request was rejected before execution.", details);
            }
            else if (success == true) outcome = Outcome.Succeeded;
            else if (!writes) { outcome = Outcome.ReadFailed; error = NativeFailure(); }
            else if (Flag(evidence, "mayHaveChanged") == false && Flag(evidence, "mayHaveWrittenFiles") != true)
            { outcome = Outcome.RejectedBeforeOperation; error = new Error("The write prerequisites were not satisfied.", new PreconditionFailedDetails("option-package-before-write", null)); }
            else { outcome = Outcome.Unknown; error = Unknown(); }

            bool incomplete = Incomplete(evidence);
            var completeness = outcome == Outcome.RejectedBeforeOperation ? Completeness.None
                : outcome == Outcome.Unknown || outcome == Outcome.ReadFailed ? Completeness.Unknown
                : outcome == Outcome.Partial || incomplete ? Completeness.Partial : Completeness.Complete;
            Paging? paging = null;
            if (offset.HasValue && limit.HasValue && outcome == Outcome.Succeeded && evidence["records"] is JsonArray rows)
            {
                int? total = evidence["expectedCount"] is JsonValue count && count.TryGetValue<int>(out var size) ? size : (int?)null;
                int? next = evidence["nextOffset"] is JsonValue nextNode && nextNode.TryGetValue<int>(out var nextOffset) ? nextOffset : (int?)null;
                paging = new Paging(PagingMode.Offset, offset, limit.Value, next, null, null, total, !next.HasValue);
            }
            evidence.Remove("timestamp"); evidence.Remove("tool");
            Clean(evidence);
            var data = new JsonObject { ["evidence"] = evidence };
            if (evidence["records"] is JsonArray records) data["items"] = records.DeepClone();
            if (outcome == Outcome.Succeeded) data["summary"] = response.Message;
            return Result(tool, data, error, outcome, completeness, writes, current, paging, reset);
        }

        private static bool Incomplete(JsonNode? value)
        {
            if (value is JsonArray array) return array.Any(Incomplete);
            if (!(value is JsonObject obj)) return false;
            bool MissingRows(string countKey, string rowsKey) => obj[countKey] is JsonValue count && count.TryGetValue<int>(out var total)
                && total > ((obj[rowsKey] as JsonArray)?.Count ?? 0);
            bool Capped(string key, int limit) => obj[key] is JsonArray rows && rows.Count == limit;
            return Flag(obj, "dataComplete") == false || Flag(obj, "truncated") == true || Flag(obj, "incomplete") == true
                || Flag(obj, "fullObjectComplete") == false
                || MissingRows("expectedCount", "records") || MissingRows("pinCount", "pins") || MissingRows("blockCount", "blocks")
                || MissingRows("subchartCount", "subcharts") || MissingRows("interfaceCount", "chartInterfaces")
                || Capped("sequence", 1000) || Capped("runSequence", 1000) || Capped("chartSequence", 1000)
                || Capped("bits", 64) || Capped("parameters", 200) || Capped("revisionIds", 500) || Capped("values", 500)
                || Capped("requiredEntries", 500) || Capped("optionalEntries", 500) || Capped("encoderTypes", 500)
                || obj["enumValueList"] is JsonObject enums && enums.Count == 500
                || obj.Any(p => (p.Key == "notFound" || p.Key == "excludedComplexProperties" || p.Key == "failures") && p.Value is JsonArray missing && missing.Count > 0)
                || obj.Any(p => p.Key.EndsWith("Error", StringComparison.Ordinal) && p.Value != null || Incomplete(p.Value));
        }

        private static void Clean(JsonNode? value)
        {
            if (value is JsonArray array) { foreach (var item in array) Clean(item); return; }
            if (!(value is JsonObject obj)) return;
            foreach (var pair in obj.ToArray())
            {
                if (pair.Key == "messageData" || pair.Key == "detailMessageData" || pair.Key == "lastFailure"
                    || pair.Key.IndexOf("stack", StringComparison.OrdinalIgnoreCase) >= 0
                    || pair.Key.Equals("password", StringComparison.OrdinalIgnoreCase) || pair.Key.Equals("token", StringComparison.OrdinalIgnoreCase)) obj.Remove(pair.Key);
                else if (pair.Key == "error" || pair.Key == "message" || pair.Key.EndsWith("Error", StringComparison.Ordinal))
                    obj[pair.Key] = "Native diagnostic details are retained in the server log.";
                else Clean(pair.Value);
            }
        }

        private static Error NativeFailure() => new Error("The option-package operation did not establish success.",
            new NativeOperationFailedDetails(null, null, new Dictionary<string, JsonElement>()));
        private static Error Unknown() => new Error("The write outcome is unknown. Inspect the retained evidence and reset the session before further writes.",
            new OutcomeUnknownDetails("option-package-operation", new Dictionary<string, JsonElement>()));

        private static CallToolResult Result(string tool, JsonObject? data, Error? error, Outcome outcome, Completeness completeness,
            bool writes, bool current, Paging? paging = null, bool reset = false)
        {
            var warnings = new List<Warning>();
            if (current) warnings.Add(new Warning(WarningCode.UnverifiedBehavior, "Native behavior retains the current policy; V4 native acceptance is pending.", new Dictionary<string, JsonElement>()));
            if (completeness == Completeness.Partial) warnings.Add(new Warning(WarningCode.IncompleteData, "The observation covers only the reported scope and available fields.", new Dictionary<string, JsonElement>()));
            Execution execution = outcome == Outcome.RejectedBeforeOperation ? Execution.NotStarted : outcome == Outcome.Unknown ? Execution.Unknown
                : outcome == Outcome.Partial ? Execution.Partial : outcome == Outcome.ReadFailed || outcome == Outcome.Succeeded && !writes ? Execution.ReadOnly : Execution.Completed;
            var meta = new Meta(DateTimeOffset.UtcNow, McpServer.ReleaseKey, tool, Meta.Correlate(InvocationJournal.CorrelationId), outcome,
                execution, reset || outcome == Outcome.Unknown, current ? BehaviorPolicy.Current : BehaviorPolicy.NotApplicable, completeness, paging, warnings);
            var result = McpResult.From(Envelope.Create(data, error, meta));
            return new CallToolResult { IsError = result.IsError, StructuredContent = JsonNode.Parse(result.StructuredContent.GetRawText()),
                Content = new[] { new TextContentBlock { Text = result.Content[0].Text } } };
        }
    }

    // Typed SINAMICS DCC option package. The chart container is a service of a drive object (see
    // ListDriveObjects for the addressing); chartPath is Root/Sub/Sub relative to DriveControlChartContainer.Charts.
    [McpServerToolType]
    internal sealed class DccTools
    {
        private readonly DccService _dcc;

        public DccTools(DccService dcc) => _dcc = dcc;

        [McpServerTool(Name="ListDccCharts"), Description("[L2][Hardware][READ] Typed DCC chart container of one drive object (DriveControlChartContainer; real project: provided by a drive axis of an S120 CU320-2 PN V5.2 whose technology extensions include DCC, not by the CU drive object nor by a V5.1 axis): without chartPath every root chart with subcharts to maxDepth, partitions, chart interfaces, blocks (includeBlocks) and pins (includePins), the DCB libraries with block types (includeLibraries) and the execution order (GetChartSequence); with chartPath one chart plus its run sequence (GetRunSequence). NotSupported when the drive object provides no DCC. Read-only.")]
        public CallToolResult ReadDccCharts(
            string[] devicePath,
            string[] itemPath,
            ushort driveObjectNumber=0,
            int driveObjectIndex=-1,
            string chartPath="",
            int maxDepth=3,
            [Description("includeBlocks: true also returns the blocks of each chart / group.")] bool includeBlocks=true,
            [Description("includePins: true also returns block pins.")] bool includePins=false,
            [Description("includeLibraries: true also returns the DCB libraries.")] bool includeLibraries=true)
            => DriveToolContract.Run("ListDccCharts", false, false, () =>
            {
                string devicePathJson = DriveToolContract.Path(devicePath, "devicePath", optional: false);
                string itemPathJson = DriveToolContract.Path(itemPath, "itemPath", optional: false);
                DriveToolContract.Check(() =>
                {
                    StartdriveLogic.ParseDriveSelector(driveObjectNumber, driveObjectIndex);
                    if (maxDepth < 1 || maxDepth > 8) throw new ArgumentException();
                    if (chartPath.Length > 0) DccLogic.ChartParts(chartPath);
                });
                return _dcc.ReadDccCharts(devicePathJson,itemPathJson,driveObjectNumber,driveObjectIndex,chartPath,maxDepth,includeBlocks,includePins,includeLibraries);
            });
        [McpServerTool(Name="ManageDccChart"), Description("[L2][Hardware][WRITE] One DCC chart or subchart (chartName is the chart path Root/Sub; empty on create = DriveControlChartComposition.Create() auto name): read, readSequence (GetChartSequence of the container or GetRunSequence of the chart), create (Create(name) with properties Comment / HorizontalSheets / VerticalSheets / PositionX / PositionY / Partition), update (typed scalars, Name renames, Partition names a partition of the parent chart, sequenceIndex = Statement.MoveInRuntimeSequence), delete (Delete() with all subcharts and blocks; confirmDelete when real), export (DriveControlChart.Export to a new .dcc file, verified by size and SHA-256), exportAll (DriveControlChartComposition.Export), import (Import(file, importOptions None|RenameOnConflict), returns the remapped parameter numbers), optimizeSequence (OptimizeRunSequence), showEditor (ShowDccEditor, interactive TIA only). DCC exceptions are reported typed (dccException type / family / licenceMissing). Default preview; no save, download or online drive command. Current native policy; V4 native acceptance is pending.")]
        public CallToolResult ManageDccChart(
            string[] devicePath,
            string[] itemPath,
            [Description("chartName: chart path 'Root/Sub' (exact names).")] string chartName="",
            [Description("action: the operation to perform - read | readSequence | create | update | delete | export | exportAll | import | optimizeSequence | showEditor.")] string action="read",
            string filePath="",
            string importOptions="",
            AttributeMap<Scalar> properties = null!,
            bool dryRun=true,
            int driveObjectIndex=-1,
            bool confirmDelete=false,
            [Description("sequenceIndex: 0-based position in the run sequence (-1 = unchanged).")] int sequenceIndex=-1,
            ushort driveObjectNumber=0)
            => DriveToolContract.Run("ManageDccChart", !dryRun && action != "read" && action != "readSequence", true, () =>
            {
                string devicePathJson = DriveToolContract.Path(devicePath, "devicePath", optional: false);
                string itemPathJson = DriveToolContract.Path(itemPath, "itemPath", optional: false);
                string propertiesJson = DriveToolContract.Attributes(properties);
                DriveToolContract.Check(() =>
                {
                    StartdriveLogic.ParseDriveSelector(driveObjectNumber, driveObjectIndex);
                    DccLogic.ValidateChartRequest(chartName, action, filePath, importOptions, propertiesJson, confirmDelete, dryRun, sequenceIndex);
                });
                return _dcc.ManageDccChart(devicePathJson,itemPathJson,driveObjectNumber,chartName,action,filePath,importOptions,propertiesJson,dryRun,driveObjectIndex,confirmDelete,sequenceIndex);
            });
        [McpServerTool(Name="ManageDccBlock"), Description("[L2][Hardware][WRITE] DCC blocks of one chart: read (all blocks, or one block with pins / published parameters / connections when blockName is given), create (DccBlockComposition.Create(blockType) auto name, Create(name, blockType) or Create(name, blockType, libraryName); properties Comment / PositionX / PositionY / GenericInputsNumber / Partition), update (typed scalars, Name renames, Partition by name, sequenceIndex = MoveInRuntimeSequence), delete (confirmDelete when real), setAsPredecessor (next created statement runs after this block). Default preview; readback after every write; no save or download. Current native policy; V4 native acceptance is pending.")]
        public CallToolResult ManageDccBlock(
            string[] devicePath,
            string[] itemPath,
            string chartPath,
            [Description("blockName: exact block name inside the chart.")] string blockName="",
            [Description("action: the operation to perform - read | create | update | delete | setAsPredecessor.")] string action="read",
            [Description("blockType: DCB block type identifier ('Library/Type') to create.")] string blockType="",
            string libraryName="",
            AttributeMap<Scalar> properties = null!,
            ushort driveObjectNumber=0,
            int driveObjectIndex=-1,
            bool confirmDelete=false,
            [Description("sequenceIndex: 0-based position in the run sequence (-1 = unchanged).")] int sequenceIndex=-1,
            bool dryRun=true)
            => DriveToolContract.Run("ManageDccBlock", !dryRun && action != "read", true, () =>
            {
                string devicePathJson = DriveToolContract.Path(devicePath, "devicePath", optional: false);
                string itemPathJson = DriveToolContract.Path(itemPath, "itemPath", optional: false);
                string propertiesJson = DriveToolContract.Attributes(properties);
                DriveToolContract.Check(() =>
                {
                    StartdriveLogic.ParseDriveSelector(driveObjectNumber, driveObjectIndex);
                    DccLogic.ValidateBlockRequest(chartPath, blockName, action, blockType, libraryName, propertiesJson, confirmDelete, dryRun, sequenceIndex);
                });
                return _dcc.ManageDccBlock(devicePathJson,itemPathJson,chartPath,blockName,action,blockType,libraryName,propertiesJson,driveObjectNumber,driveObjectIndex,confirmDelete,sequenceIndex,dryRun);
            });
        [McpServerTool(Name="ManageDccPin"), Description("[L2][Hardware][WRITE] One pin of a DCC block: read (value / unit / comment / invisible / forTest / isInput / isPublished, the published DccParameter, connections with typed sink and source), update (properties Comment / Value / Unit / Invisible / ForTest; Value is converted to the pin's own data type - real project: an ADD input is System.Single and TIA refuses Int32 / Double), connect (partner {block, pin} = DccPin.Connect(DccPin) or {chartInterface} = Connect(DccChartInterface)), disconnect (deletes the DccConnection to that partner), publish (Publish(setAsSignal) automatic number, Publish(setAsSignal, parameterNumber) or Publish(parameterNumber, arrayIndex, setAsSignal) for indexed signal parameters, SINAMICS FW V6.1+), unpublish (Unpublish, removes its connections), updateParameter (published DccParameter Number / ArrayIndex / ParameterText / IsSignal). Default preview; readback verifies publish / unpublish; no save or download. Current native policy; V4 native acceptance is pending.")]
        public CallToolResult ManageDccPin(
            string[] devicePath,
            string[] itemPath,
            string chartPath,
            [Description("blockName: exact block name inside the chart.")] string blockName,
            [Description("pinName: exact pin name on the block.")] string pinName,
            [Description("action: the operation to perform - read | update | connect | disconnect | publish | unpublish | updateParameter.")] string action="read",
            AttributeMap<Scalar> properties = null!,
            [Description("partner: Partner {block, pin} or {chartInterface}; only for connect / disconnect.")] DccPartnerSpec partner = null!,
            [Description("setAsSignal: true publishes the pin as a signal.")] bool setAsSignal=false,
            [Description("parameterNumber: drive parameter number (p / r number without the letter).")] int parameterNumber=-1,
            [Description("arrayIndex: array index of the parameter (-1 = scalar).")] int arrayIndex=-1,
            ushort driveObjectNumber=0,
            int driveObjectIndex=-1,
            bool dryRun=true)
            => DriveToolContract.Run("ManageDccPin", !dryRun && action != "read", true, () =>
            {
                string devicePathJson = DriveToolContract.Path(devicePath, "devicePath", optional: false);
                string itemPathJson = DriveToolContract.Path(itemPath, "itemPath", optional: false);
                string propertiesJson = DriveToolContract.Attributes(properties);
                string partnerJson = partner?.Json.GetRawText() ?? "{}";
                DriveToolContract.Check(() =>
                {
                    StartdriveLogic.ParseDriveSelector(driveObjectNumber, driveObjectIndex);
                    DccLogic.ValidatePinRequest(chartPath, blockName, pinName, action, propertiesJson, partnerJson, setAsSignal, parameterNumber, arrayIndex, dryRun);
                });
                return _dcc.ManageDccPin(devicePathJson,itemPathJson,chartPath,blockName,pinName,action,propertiesJson,partnerJson,setAsSignal,parameterNumber,arrayIndex,driveObjectNumber,driveObjectIndex,dryRun);
            });
        [McpServerTool(Name="ManageDccChartInterface"), Description("[L2][Hardware][WRITE] Chart interfaces of one DCC chart (DccChartInterfaceComposition): read (all, or one by interfaceName), create (Create(DccPin) from sourceBlock + sourcePin; the interface is named after the pin), update (properties Comment / Value / Unit / Invisible / ForTest), delete (confirmDelete when real). Real project (S120 V5.2 drive axis, DCC/5201000): DccChartInterfaceComposition.Create(DccPin) answered 'This operation is not supported' on the root chart and on a subchart alike - chart interfaces are not creatable through Openness on that firmware. Default preview; no save or download. Current native policy; V4 native acceptance is pending.")]
        public CallToolResult ManageDccChartInterface(
            string[] devicePath,
            string[] itemPath,
            string chartPath,
            [Description("interfaceName: exact chart interface name.")] string interfaceName="",
            [Description("action: the operation to perform - read | create | update | delete.")] string action="read",
            [Description("sourceBlock: exact name of the block whose pin feeds the interface.")] string sourceBlock="",
            [Description("sourcePin: exact pin name on sourceBlock.")] string sourcePin="",
            AttributeMap<Scalar> properties = null!,
            ushort driveObjectNumber=0,
            int driveObjectIndex=-1,
            bool confirmDelete=false,
            bool dryRun=true)
            => DriveToolContract.Run("ManageDccChartInterface", !dryRun && action != "read", true, () =>
            {
                string devicePathJson = DriveToolContract.Path(devicePath, "devicePath", optional: false);
                string itemPathJson = DriveToolContract.Path(itemPath, "itemPath", optional: false);
                string propertiesJson = DriveToolContract.Attributes(properties);
                DriveToolContract.Check(() =>
                {
                    StartdriveLogic.ParseDriveSelector(driveObjectNumber, driveObjectIndex);
                    DccLogic.ValidateInterfaceRequest(chartPath, interfaceName, action, sourceBlock, sourcePin, propertiesJson, confirmDelete, dryRun);
                });
                return _dcc.ManageDccChartInterface(devicePathJson,itemPathJson,chartPath,interfaceName,action,sourceBlock,sourcePin,propertiesJson,driveObjectNumber,driveObjectIndex,confirmDelete,dryRun);
            });
        [McpServerTool(Name="ManageDccChartPartition"), Description("[L2][Hardware][WRITE] Partitions of one DCC chart (DccChartPartitionComposition): read, create (Create(name)), update (properties Name / Comment), delete (confirmDelete when real). Blocks and subcharts are moved between partitions with ManageDccBlock / ManageDccChart properties.Partition. Default preview; no save or download. Current native policy; V4 native acceptance is pending.")]
        public CallToolResult ManageDccChartPartition(
            string[] devicePath,
            string[] itemPath,
            string chartPath,
            [Description("partitionName: exact partition name.")] string partitionName="",
            [Description("action: the operation to perform - read | create | update | delete.")] string action="read",
            AttributeMap<Scalar> properties = null!,
            ushort driveObjectNumber=0,
            int driveObjectIndex=-1,
            bool confirmDelete=false,
            bool dryRun=true)
            => DriveToolContract.Run("ManageDccChartPartition", !dryRun && action != "read", true, () =>
            {
                string devicePathJson = DriveToolContract.Path(devicePath, "devicePath", optional: false);
                string itemPathJson = DriveToolContract.Path(itemPath, "itemPath", optional: false);
                string propertiesJson = DriveToolContract.Attributes(properties);
                DriveToolContract.Check(() =>
                {
                    StartdriveLogic.ParseDriveSelector(driveObjectNumber, driveObjectIndex);
                    DccLogic.ValidatePartitionRequest(chartPath, partitionName, action, propertiesJson, confirmDelete, dryRun);
                });
                return _dcc.ManageDccChartPartition(devicePathJson,itemPathJson,chartPath,partitionName,action,propertiesJson,driveObjectNumber,driveObjectIndex,confirmDelete,dryRun);
            });
        [McpServerTool(Name="ManageDcbLibraries"), Description("[L2][Hardware][WRITE] DCB libraries: read lists the DcbLibrary entries used by the drive object's charts (library name, version, DcbBlockType name / description); import calls the V21 DcbLibraryImporter.ImportDcbLibrary(filePath .zip) on the project library (DCB extension library; NotSupported on V20). Default preview; no save. Current native policy; V4 native acceptance is pending.")]
        public CallToolResult ManageDcbLibraries(
            [Description("action: the operation to perform - read | import.")] string action="read",
            string[] devicePath = null!,
            string[] itemPath = null!,
            ushort driveObjectNumber=0,
            int driveObjectIndex=-1,
            string filePath="",
            bool dryRun=true)
            => DriveToolContract.Run("ManageDcbLibraries", !dryRun && action != "read", true, () =>
            {
                string devicePathJson = DriveToolContract.Path(devicePath, "devicePath", optional: true);
                string itemPathJson = DriveToolContract.Path(itemPath, "itemPath", optional: true);
                DriveToolContract.Check(() =>
                {
                    StartdriveLogic.ParseDriveSelector(driveObjectNumber, driveObjectIndex);
                    DccLogic.ValidateLibraryRequest(action, filePath, dryRun);
                });
                return _dcc.ManageDcbLibraries(action,devicePathJson,itemPathJson,driveObjectNumber,driveObjectIndex,filePath,dryRun);
            });
        [McpServerTool(Name="GetDccObject"), Description("[L2][Hardware][READ] Exact offline DCC chart/block/pin/library property path (objectPath [{property,name}] from the DriveControlChartContainer) with bounded scalar pagination; the typed views are ListDccCharts / ManageDccBlock / ManageDccPin. Complex properties excluded explicitly; no complete internal binding guarantee.")]
        public CallToolResult ReadDccObject(string[] devicePath, string[] itemPath, PropertyStep[] objectPath = null!, int offset=0, int limit=100, int driveObjectIndex=-1, ushort driveObjectNumber=0)
            => DriveToolContract.Run("GetDccObject", false, false, () =>
            {
                string devicePathJson = DriveToolContract.Path(devicePath, "devicePath", optional: false);
                string itemPathJson = DriveToolContract.Path(itemPath, "itemPath", optional: false);
                string objectPathJson = V4Json.Serialize(objectPath ?? Array.Empty<PropertyStep>());
                DriveToolContract.Check(() =>
                {
                    StartdriveLogic.ParseDriveSelector(driveObjectNumber, driveObjectIndex);
                    EngineeringObjectAddress.Parse(objectPathJson);
                    HardwareServicesLogic.ValidatePagination(offset, limit);
                });
                return _dcc.ReadDccObject(devicePathJson,itemPathJson,driveObjectNumber,objectPathJson,offset,limit,driveObjectIndex);
            }, offset, limit);
    }
}
