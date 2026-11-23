using TiaMcp.Logic.V4.Hmi;
using TiaMcp.Logic.V4.Construction;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml.Linq;
using TiaMcpServer.Siemens;



namespace TiaMcpServer.ModelContextProtocol
{
    [McpServerToolType]
    internal sealed class XmlBuilderTools
    {
        [McpServerTool(Name = "BuildClassicHmiScreen"),
                Description("[L2][HMI-Classic]Offline-only helper: build a V18-stamped Classic/Basic WinCC HMI screen XML document from structured JSON (V18–V21 hosts; V18/V19 TP700 Comfort import acceptance NOT RUN). It does not connect to TIA Portal, import screens, or modify projects. Validate in a temporary Classic HMI project before using on a real project. screen.width/height MUST equal the panel display (TP700 Comfort 800x480, TP900 800x480, TP1200 1280x800; the builder default is 640x480): a classic screen imported with another size makes TIA Portal V21 exit (crash 10, guarded by ImportHmiScreen when the panel already has a screen).")]
        public CallToolResult BuildClassicHmiScreenXmlV4(
            [Description("design: ClassicScreenSpec. Supply the structured value directly; exact camelCase fields, no null or JSON string " +
                "encoding.")] ClassicScreenSpec design)
            => OfflineContracts
                .Run("BuildClassicHmiScreen", () => BuildClassicHmiScreenXml(design.ToBuilderInput().ToJsonString()), writes: false, current: HardwareContract
                    .ReleaseKey is "18" or "19");

        public ResponseXmlBuild BuildClassicHmiScreenXml(
            [Description("designJson: JSON object with Screen/Items. Items support Type=Text/Button/IOField/Lamp/Rectangle plus " +
                "Name/Left/Top/Width/Height/Text/Properties.")] string designJson)
        {
            try
            {
                return XmlBuildResults
                    .BuildOfflineXmlBuilderReport(ClassicHmiScreenXmlBuilder.BuildFromJson(designJson), "Classic HMI screen XML built offline");
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error building Classic HMI screen XML offline: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode
                        .InternalError);
            }
        }

        [McpServerTool(Name = "BuildPlcUdt"),
                Description("[L2][PLC-Builders][OFFLINE] Build flat PLC UDT/PlcStruct candidate XML using the selected release's interface schema. Explicit " +
                "outputReleaseKey supports 14sp1, 15.1 and 16-21; the default is the serving release. Input: " +
                "{name,members:[{name,datatype,externalWritable?,commentZhCn?}]}. Returns XML only. Runtime XSD and native import validation are separate; " +
                "it does not connect, import or write files.")]
        public CallToolResult BuildPlcUdtXmlV4(
            [Description("udt: UdtSpec. Supply the structured value directly; exact camelCase fields, no null or JSON string encoding.")] UdtSpec udt,
            [Description("outputReleaseKey: 14sp1|15.1|16|17|18|19|20|21. Target declaration XML format; independent of the connected host. Omit to use the " +
                "serving release.")] string outputReleaseKey =
                null!)
            => OfflineContracts
                .Run("BuildPlcUdt", () => BuildPlcUdtXml(ConstructionAdapter.ToBuilderJson(udt), outputReleaseKey), writes: false, current: false);

        public ResponseXmlBuild BuildPlcUdtXml(
            [Description("udtJson: JSON object with members[]. Required member fields: name, datatype. Optional: externalWritable, " +
                "commentZhCn/comment.")] string udtJson,
            [Description("outputReleaseKey: 14sp1|15.1|16|17|18|19|20|21. Target declaration XML format; independent of the connected host. Omit to use the " +
                "serving release.")] string outputReleaseKey =
                null!)
        {
            try
            {
                return XmlBuildResults
                    .BuildOfflineXmlBuilderReport(PlcBuilderToolJson.BuildUdt(udtJson, outputReleaseKey ?? HardwareContract.ReleaseKey),
                        "PLC UDT XML built offline");
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Invalid PLC UDT builder input: {ex.Message}", ex, McpErrorCode.InvalidParams);
            }
        }

        [McpServerTool(Name = "BuildPlcTagTable"),
                Description("[L2][PLC-Builders][OFFLINE] Build a PLC tag table XML document for the serving release from structured JSON. Input: " +
                "{tableName,tags:[{name,dataTypeName,logicalAddress}]}. It only returns XML; it does not connect to TIA Portal, import tag tables, write " +
                "files, or modify projects.")]
        public CallToolResult BuildPlcTagTableXmlV4(
            [Description("tagTable: PlcTagTableSpec. Supply the structured value directly; exact camelCase fields, no null or JSON string " +
                "encoding.")] PlcTagTableSpec tagTable)
            => OfflineContracts.Run("BuildPlcTagTable", () => BuildPlcTagTableXml(ConstructionAdapter.ToBuilderJson(tagTable)), writes: false, current: false);

        public ResponseXmlBuild BuildPlcTagTableXml(
            [Description("tagTableJson: JSON object with tableName/name and tags[]. Required tag fields: name, dataTypeName/datatype, " +
                "logicalAddress/address.")] string tagTableJson)
        {
            try
            {
                return XmlBuildResults
                    .BuildOfflineXmlBuilderReport(PlcBuilderToolJson.BuildTagTableForRelease(tagTableJson, HardwareContract.ReleaseKey),
                        "PLC tag table XML built offline");
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Invalid PLC tag table builder input: {ex.Message}", ex, McpErrorCode.InvalidParams);
            }
        }

        [McpServerTool(Name = "BuildPlcGlobalDb"),
                Description("[L2][PLC-Builders][OFFLINE] Build flat PLC GlobalDB candidate XML using the selected release's interface schema. Explicit " +
                "outputReleaseKey supports 14sp1, 15.1 and 16-21; the default is the serving release. Input: " +
                "{dbName,dbNumber,staticMembers:[{name,datatype,externalWritable?,commentZhCn?,startValue?}]}. Returns XML only. Runtime XSD and native " +
                "import validation are separate; it does not connect, import or write files.")]
        public CallToolResult BuildPlcGlobalDbXmlV4(
            [Description("globalDb: GlobalDbSpec. Supply the structured value directly; exact camelCase fields, no null or JSON string " +
                "encoding.")] GlobalDbSpec globalDb,
            [Description("outputReleaseKey: 14sp1|15.1|16|17|18|19|20|21. Target declaration XML format; independent of the connected host. Omit to use the " +
                "serving release.")] string outputReleaseKey =
                null!)
            => OfflineContracts
                .Run("BuildPlcGlobalDb", () => BuildPlcGlobalDbXml(ConstructionAdapter.ToBuilderJson(globalDb), outputReleaseKey), writes: false, current
                    : false);

        public ResponseXmlBuild BuildPlcGlobalDbXml(
            [Description("globalDbJson: JSON object with dbName/name, dbNumber/number, and staticMembers[] or members[].")] string globalDbJson,
            [Description("outputReleaseKey: 14sp1|15.1|16|17|18|19|20|21. Target declaration XML format; independent of the connected host. Omit to use the " +
                "serving release.")] string outputReleaseKey =
                null!)
        {
            try
            {
                return XmlBuildResults
                    .BuildOfflineXmlBuilderReport(PlcBuilderToolJson.BuildGlobalDb(globalDbJson, outputReleaseKey ?? HardwareContract.ReleaseKey),
                        "PLC GlobalDB XML built offline");
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Invalid PLC GlobalDB builder input: {ex.Message}", ex, McpErrorCode.InvalidParams);
            }
        }

        [McpServerTool(Name = "BuildStructuredText"),
                Description("[L2][PLC-Builders][OFFLINE] Build a TIA V21 StructuredText/v4 XML fragment from operation JSON. Input: " +
                "{operations:[{op:'if'|'else'|'endif'|'assign'|'token'|'blank'|'newline', ...}]}. It only returns XML; it does not connect to TIA Portal, " +
                "import blocks, write files, or modify projects.")]
        public CallToolResult BuildStructuredTextXmlV4(
            [Description("structuredText: StructuredTextSpec. Supply the structured value directly; exact camelCase fields, no null or JSON string " +
                "encoding.")] StructuredTextSpec structuredText,
            [Description("innerOnly: true returns only inner XML for embedding into a block composer; false returns <StructuredText>.")] bool innerOnly = false)
            => OfflineContracts
                .Run("BuildStructuredText", () => BuildStructuredTextXml(ConstructionAdapter.ToBuilderJson(structuredText), innerOnly), writes: false, current
                    : false);

        public ResponseXmlBuild BuildStructuredTextXml(
            [Description("structuredTextJson: JSON object with operations[]. assignment uses target + literalValue/value; if uses condition/variable; token " +
                "uses text.")] string structuredTextJson,
            [Description("innerOnly: true returns only inner XML for embedding into a block composer; false returns <StructuredText>.")] bool innerOnly = false)
        {
            try
            {
                return XmlBuildResults
                    .BuildOfflineXmlBuilderReport(PlcBuilderToolJson.BuildStructuredText(structuredTextJson, innerOnly),
                        "PLC StructuredText XML built offline");
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Invalid StructuredText builder input: {ex.Message}", ex, McpErrorCode.InvalidParams);
            }
        }

        [McpServerTool(Name = "BuildFlgNetCall"),
                Description("[L2][PLC-Builders][OFFLINE] Build one V21 FlgNet/v5 FC-call fragment from " +
                    "{callName,parameters:[{name,section,dataType,sourceKind?,symbolPath?|constantValue?}]}.[[BuildPlcLadFcBlock| For contacts, " +
                    "compares, OR, " +
                    "fan-out, coils, MOVE, TON/TOF/TP, CTU/CTD/CTUD and user FC/FB calls use BuildPlcLadFcBlock rung networks on V21.]] " +
                    "For unsupported timers, counters and math or library/system instructions, and all " +
                    "general LAD on V20, author S7DCL text and import with ImportPlcBlocksDocuments, or start from an exported donor block. " +
                    "A bare fragment cannot be imported. Returns candidate XML in " +
                    "memory only; no files, connection or worker. Native acceptance NOT RUN.")]
        public CallToolResult BuildFlgNetCallXmlV4(
            [Description("flgNet: FlgNetCallSpec. Supply the structured value directly; exact camelCase fields, no null or JSON string " +
                "encoding.")] FlgNetCallSpec flgNet)
            => OfflineContracts.Run("BuildFlgNetCall", () => BuildFlgNetCallXml(ConstructionAdapter.ToBuilderJson(flgNet)), writes: false, current: false);

        public ResponseXmlBuild BuildFlgNetCallXml(
            [Description("flgNetJson: JSON object with callName/name and parameters[]. Global parameters use symbolPath[] or dotted symbol; constants use " +
                "sourceKind='constant' and value.")] string flgNetJson)
        {
            try
            {
                return XmlBuildResults.BuildOfflineXmlBuilderReport(PlcBuilderToolJson.BuildFlgNetCall(flgNetJson), "PLC FlgNet call XML built offline");
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Invalid FlgNet call builder input: {ex.Message}", ex, McpErrorCode.InvalidParams);
            }
        }

        [McpServerTool(Name = "BuildPlcFcBlock"),
                Description("[L2][PLC-Builders][OFFLINE] Compose a TIA V21 SCL FC block XML from interface JSON and StructuredText content. Input: " +
                "{blockName,blockNumber,inputs:[{name,datatype}],outputs:[{name,datatype}],structuredText:{operations:[]}}. It only returns XML; it does not " +
                "connect to TIA Portal, import blocks, write files, or modify projects. Registered on V21 only: V20 withdraws this V21-format composer until " +
                "an import of its output on V20 is verified.")]
        public CallToolResult ComposePlcFcBlockXmlV4(
            [Description("fcBlock: FcBlockSpec. Supply the structured value directly; exact camelCase fields, no null or JSON string " +
                "encoding.")] FcBlockSpec fcBlock)
            => OfflineContracts.Run("BuildPlcFcBlock", () => ComposePlcFcBlockXml(ConstructionAdapter.ToBuilderJson(fcBlock)), writes: false, current: false);

        public ResponseXmlBuild ComposePlcFcBlockXml(
            [Description("fcBlockJson: JSON object with blockName/name, blockNumber/number, inputs[], outputs[], and structuredTextInnerXml or " +
                "structuredText.operations[].")] string fcBlockJson)
        {
            try
            {
                return XmlBuildResults.BuildOfflineXmlBuilderReport(PlcBuilderToolJson.ComposeFcBlock(fcBlockJson), "PLC FC block XML composed offline");
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Invalid PLC FC composer input: {ex.Message}", ex, McpErrorCode.InvalidParams);
            }
        }

        [McpServerTool(Name = "BuildPlcFbBlock"),
                Description("[L2][PLC-Builders][OFFLINE] Compose a TIA V21 SCL FB block XML from interface JSON and StructuredText content. Input: " +
                "{blockName,blockNumber,inputs?,outputs?,inouts?,statics?,temps?,structuredText:{operations:[]}}. It only returns XML; it does not connect " +
                "to TIA Portal, import blocks, write files, create instance DBs, or modify projects. Registered on V21 only: V20 withdraws this V21-format " +
                "composer until an import of its output on V20 is verified.")]
        public CallToolResult ComposePlcFbBlockXmlV4(
            [Description("fbBlock: FbBlockSpec. Supply the structured value directly; exact camelCase fields, no null or JSON string " +
                "encoding.")] FbBlockSpec fbBlock)
            => OfflineContracts.Run("BuildPlcFbBlock", () => ComposePlcFbBlockXml(ConstructionAdapter.ToBuilderJson(fbBlock)), writes: false, current: false);

        public ResponseXmlBuild ComposePlcFbBlockXml(
            [Description("fbBlockJson: JSON object with blockName/name, blockNumber/number, optional inputs/outputs/inouts/statics/temps arrays, and " +
                "structuredTextInnerXml or structuredText.operations[].")] string fbBlockJson)
        {
            try
            {
                return XmlBuildResults.BuildOfflineXmlBuilderReport(PlcBuilderToolJson.ComposeFbBlock(fbBlockJson), "PLC FB block XML composed offline");
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Invalid PLC FB composer input: {ex.Message}", ex, McpErrorCode.InvalidParams);
            }
        }

        [McpServerTool(Name = "BuildPlcLadFcBlock"),
                Description("[L2][PLC-Builders][OFFLINE] Compose a V21 LAD FC candidate from " +
                    "{blockName,blockNumber,inputs?,outputs?,networks:[{call|rung,titleZhCn?,commentZhCn?}]}. Exactly one call or rung per " +
                    "network. Rung supports NO/NC, typed comparisons, OR groups, shared-prefix " +
                    "fan-out, coil/set/reset, MOVE with ENO chaining, TON/TOF/TP, CTU/CTD/CTUD and user FC/FB CALL with named pins; " +
                    "global constants and indexed component paths. Time PT; Output/InOut pins require symbols. Known instruction names and " +
                    "the MC_ prefix are reserved; no block resolution. Local symbols must start " +
                    "with a declared input/output. Edge contacts require a verified V21 export (edge-contact-unverified). " +
                    "Limits: 64 networks, 256 parts per rung network, depth 16, strings 4096. No symbol/type " +
                    "resolution; compile is the authority. Returns XML only, no files, connection, import or worker. V21 only; native " +
                    "acceptance NOT RUN. For unsupported timers, counters and math or library/system instructions, author S7DCL text and import " +
                    "with ImportPlcBlocksDocuments, or start from an exported donor block.")]
        public CallToolResult ComposePlcLadFcBlockXmlV4(
            [Description("ladFcBlock: LadFcBlockSpec with exactly one call or rung per network. Rung outputs include timers (TON/TOF/TP), " +
                "counters (CTU/CTD/CTUD) with global instances, and user FC/FB calls with named pins and optional ENO chains. " +
                "Supply the structured value " +
                "directly; exact camelCase fields, no null or JSON string encoding.")] LadFcBlockSpec ladFcBlock)
            => ladFcBlock.EdgeContactRefusal() is { } refusal ? McpServer.V4Reject("BuildPlcLadFcBlock", refusal)
                : OfflineContracts.Run("BuildPlcLadFcBlock", () => ComposePlcLadFcBlockXml(ConstructionAdapter.ToBuilderJson(ladFcBlock)),
                    writes: false, current: false);

        public ResponseXmlBuild ComposePlcLadFcBlockXml(
            [Description("ladFcBlockJson: JSON object with blockName, blockNumber, networks[] (each with " +
                "callJson{callName,parameters[]} or rung{series,outputs,fanOut?}, optional " +
                "titleZhCn/commentZhCn), optional inputs[]/outputs[] interface members with commentZhCn, optional commentZhCn/titleZhCn " +
                "block-level.")] string ladFcBlockJson)
        {
            try
            {
                return XmlBuildResults
                    .BuildOfflineXmlBuilderReport(PlcBuilderToolJson.ComposeLadFcBlock(ladFcBlockJson), "PLC LAD FC block XML composed offline");
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Invalid PLC LAD FC composer input: {ex.Message}", ex, McpErrorCode.InvalidParams);
            }
        }
    }
}
