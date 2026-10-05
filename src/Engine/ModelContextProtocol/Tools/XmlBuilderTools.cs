using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
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


using TiaMcpServer.Siemens.Services;

namespace TiaMcpServer.ModelContextProtocol
{
    [McpServerToolType]
    internal sealed class XmlBuilderTools
    {
        [McpServerTool(Name = "BuildClassicHmiScreenXml"), Description("[L2][HMI-Classic]Offline-only helper: build a Classic/Basic WinCC HMI screen XML document from structured JSON. It does not connect to TIA Portal, import screens, or modify projects. Validate in a temporary Classic HMI project before using on a real project. screen.width/height MUST equal the panel display (TP700 Comfort 800x480, TP900 800x480, TP1200 1280x800; the builder default is 640x480): a classic screen imported with another size makes TIA Portal V21 exit (crash 10, guarded by ImportHmiScreen when the panel already has a screen).")]
        public ResponseXmlBuild BuildClassicHmiScreenXml(
            [Description("designJson: JSON object with Screen/Items. Items support Type=Text/Button/IOField/Lamp/Rectangle plus Name/Left/Top/Width/Height/Text/Properties.")] string designJson)
        {
            try
            {
                return XmlBuildResults.BuildOfflineXmlBuilderReport(ClassicHmiScreenXmlBuilder.BuildFromJson(designJson), "Classic HMI screen XML built offline");
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error building Classic HMI screen XML offline: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "BuildPlcUdtXml"), Description("[L2][PLC-Builders][OFFLINE] Build flat PLC UDT/PlcStruct candidate XML using the selected release's interface schema. Explicit outputReleaseKey supports 14sp1, 15.1 and 16-21; default 21 preserves existing calls. Input: {name,members:[{name,datatype,externalWritable?,commentZhCn?}]}. Returns XML only. Runtime XSD and native import validation are separate; it does not connect, import or write files.")]
        public ResponseXmlBuild BuildPlcUdtXml(
            [Description("udtJson: JSON object with members[]. Required member fields: name, datatype. Optional: externalWritable, commentZhCn/comment.")] string udtJson,
            [Description("outputReleaseKey: 14sp1|15.1|16|17|18|19|20|21. Target declaration XML format; independent of the connected host. Default 21 preserves existing calls.")] string outputReleaseKey = "21")
        {
            try
            {
                return XmlBuildResults.BuildOfflineXmlBuilderReport(PlcBuilderToolJson.BuildUdt(udtJson, outputReleaseKey), "PLC UDT XML built offline");
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Invalid PLC UDT builder input: {ex.Message}", ex, McpErrorCode.InvalidParams);
            }
        }

        [McpServerTool(Name = "BuildPlcTagTableXml"), Description("[L2][PLC-Builders][OFFLINE] Build a TIA V21 PLC tag table XML document from structured JSON. Input: {tableName,tags:[{name,dataTypeName,logicalAddress}]}. It only returns XML; it does not connect to TIA Portal, import tag tables, write files, or modify projects.")]
        public ResponseXmlBuild BuildPlcTagTableXml(
            [Description("tagTableJson: JSON object with tableName/name and tags[]. Required tag fields: name, dataTypeName/datatype, logicalAddress/address.")] string tagTableJson)
        {
            try
            {
                return XmlBuildResults.BuildOfflineXmlBuilderReport(PlcBuilderToolJson.BuildTagTable(tagTableJson), "PLC tag table XML built offline");
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Invalid PLC tag table builder input: {ex.Message}", ex, McpErrorCode.InvalidParams);
            }
        }

        [McpServerTool(Name = "BuildPlcGlobalDbXml"), Description("[L2][PLC-Builders][OFFLINE] Build flat PLC GlobalDB candidate XML using the selected release's interface schema. Explicit outputReleaseKey supports 14sp1, 15.1 and 16-21; default 21 preserves existing calls. Input: {dbName,dbNumber,staticMembers:[{name,datatype,externalWritable?,commentZhCn?,startValue?}]}. Returns XML only. Runtime XSD and native import validation are separate; it does not connect, import or write files.")]
        public ResponseXmlBuild BuildPlcGlobalDbXml(
            [Description("globalDbJson: JSON object with dbName/name, dbNumber/number, and staticMembers[] or members[].")] string globalDbJson,
            [Description("outputReleaseKey: 14sp1|15.1|16|17|18|19|20|21. Target declaration XML format; independent of the connected host. Default 21 preserves existing calls.")] string outputReleaseKey = "21")
        {
            try
            {
                return XmlBuildResults.BuildOfflineXmlBuilderReport(PlcBuilderToolJson.BuildGlobalDb(globalDbJson, outputReleaseKey), "PLC GlobalDB XML built offline");
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Invalid PLC GlobalDB builder input: {ex.Message}", ex, McpErrorCode.InvalidParams);
            }
        }

        [McpServerTool(Name = "BuildStructuredTextXml"), Description("[L2][PLC-Builders][OFFLINE] Build a TIA V21 StructuredText/v4 XML fragment from operation JSON. Input: {operations:[{op:'if'|'else'|'endif'|'assignment'|'token'|'blank'|'newline', ...}]}. It only returns XML; it does not connect to TIA Portal, import blocks, write files, or modify projects.")]
        public ResponseXmlBuild BuildStructuredTextXml(
            [Description("structuredTextJson: JSON object with operations[]. assignment uses target + literalValue/value; if uses condition/variable; token uses text.")] string structuredTextJson,
            [Description("innerOnly: true returns only inner XML for embedding into a block composer; false returns <StructuredText>.")] bool innerOnly = false)
        {
            try
            {
                return XmlBuildResults.BuildOfflineXmlBuilderReport(PlcBuilderToolJson.BuildStructuredText(structuredTextJson, innerOnly), "PLC StructuredText XML built offline");
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Invalid StructuredText builder input: {ex.Message}", ex, McpErrorCode.InvalidParams);
            }
        }

        [McpServerTool(Name = "BuildFlgNetCallXml"), Description("[L2][PLC-Builders][OFFLINE] NARROW SCOPE: builds ONLY a LAD network that calls one FC with parameters. For general ladder (contacts/coils/SR/compare/Move/math) author S7DCL text and import with ImportPlcBlocksDocuments — there is no XML builder for those, and hand-written FlgNet XML is the usual cause of import errors. Build a TIA V21 LAD FlgNet/v5 FC call network XML from structured JSON. Input: {callName,parameters:[{name,section,dataType,sourceKind?,symbolPath?|symbol?|value?}]}. It only returns XML; it does not connect to TIA Portal, import blocks, write files, or modify projects.")]
        public ResponseXmlBuild BuildFlgNetCallXml(
            [Description("flgNetJson: JSON object with callName/name and parameters[]. Global parameters use symbolPath[] or dotted symbol; constants use sourceKind='constant' and value.")] string flgNetJson)
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

        [McpServerTool(Name = "ComposePlcFcBlockXml"), Description("[L2][PLC-Builders][OFFLINE] Compose a TIA V21 SCL FC block XML from interface JSON and StructuredText content. Input: {blockName,blockNumber,inputs:[{name,datatype}],outputs:[{name,datatype}],structuredTextInnerXml? or structuredText:{operations:[]}}. It only returns XML; it does not connect to TIA Portal, import blocks, write files, or modify projects.")]
        public ResponseXmlBuild ComposePlcFcBlockXml(
            [Description("fcBlockJson: JSON object with blockName/name, blockNumber/number, inputs[], outputs[], and structuredTextInnerXml or structuredText.operations[].")] string fcBlockJson)
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

        [McpServerTool(Name = "ComposePlcFbBlockXml"), Description("[L2][PLC-Builders][OFFLINE] Compose a TIA V21 SCL FB block XML from interface JSON and StructuredText content. Input: {blockName,blockNumber,inputs?,outputs?,inouts?,statics?,temps?,structuredTextInnerXml? or structuredText:{operations:[]}}. It only returns XML; it does not connect to TIA Portal, import blocks, write files, create instance DBs, or modify projects.")]
        public ResponseXmlBuild ComposePlcFbBlockXml(
            [Description("fbBlockJson: JSON object with blockName/name, blockNumber/number, optional inputs/outputs/inouts/statics/temps arrays, and structuredTextInnerXml or structuredText.operations[].")] string fbBlockJson)
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

        [McpServerTool(Name = "ComposePlcLadFcBlockXml"), Description("[L2][PLC-Builders][OFFLINE] NARROW SCOPE: every network must be an FC call; this cannot emit contacts/coils/SR/compare/Move/math. For general ladder, author S7DCL text (.s7dcl + .s7res) and import with ImportPlcBlocksDocuments instead. Compose a TIA V21 LAD FC block XML containing one or more FlgNet/v5 FC-call networks. Each network is an FC call described as { callJson: { callName, parameters[] }, titleZhCn?, commentZhCn? }. Top-level: blockName, blockNumber, optional inputs/outputs members, optional commentZhCn / titleZhCn. Returns XML only; does not connect to TIA Portal or import. Pair with ImportPlcBlock.")]
        public ResponseXmlBuild ComposePlcLadFcBlockXml(
            [Description("ladFcBlockJson: JSON object with blockName, blockNumber, networks[] (each with callJson{callName,parameters[]}, optional titleZhCn/commentZhCn), optional inputs[]/outputs[] interface members with commentZhCn, optional commentZhCn/titleZhCn block-level.")] string ladFcBlockJson)
        {
            try
            {
                return XmlBuildResults.BuildOfflineXmlBuilderReport(PlcBuilderToolJson.ComposeLadFcBlock(ladFcBlockJson), "PLC LAD FC block XML composed offline");
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Invalid PLC LAD FC composer input: {ex.Message}", ex, McpErrorCode.InvalidParams);
            }
        }
    }
}
