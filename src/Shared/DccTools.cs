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
                    if (maxDepth < 1 || maxDepth > 8) throw new ArgumentException("maxDepth must be between 1 and 8.", nameof(maxDepth));
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
                    EngineeringObjectPath.Parse(objectPathJson);
                    HardwareServicesLogic.ValidatePagination(offset, limit);
                });
                return _dcc.ReadDccObject(devicePathJson,itemPathJson,driveObjectNumber,objectPathJson,offset,limit,driveObjectIndex);
            }, offset, limit);
    }
}
