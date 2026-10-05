using System;
using System.ComponentModel;
using ModelContextProtocol.Protocol;
using TiaMcp.Logic.V4;
using ModelContextProtocol.Server;
using TiaMcpServer.Siemens.Services;

namespace TiaMcpServer.ModelContextProtocol
{
    [McpServerToolType]
    internal sealed class UnifiedExchangeTools
    {
        private readonly UnifiedExchangeService _service;

        public UnifiedExchangeTools(UnifiedExchangeService service) => _service = service;

        [McpServerTool(Name="ExchangeUnifiedTags"), Description("[L2][HMI-Unified][FILE] Native WinCC Unified tag exchange in WinCC ML (YAML): export writes <fileName>.hmi.yml (plus TIA side files) via HmiTagComposition.Export(DirectoryInfo[, name]) into a NEW absolute directory on the MCP server and hashes every file; import calls Import(DirectoryInfo[, name]) on an existing directory and checks expectedTagNames (exact names) afterwards. tagTable = unique table name or /Group/Sub/Table (empty = the device root Tags composition). Default preview; import needs dryRun=false and may also touch other tags in the file; tag properties are not independently compared. No save/compile/download. Current native policy; V4 safety behavior is not yet accepted.")]
        public CallToolResult ExchangeUnifiedTags(
            string softwarePath,
            [Description("export | import. ")] string action,
            string directory,
            [Description("tagTable: exact HMI tag table name.")] string tagTable="",
            [Description("fileName: file name (with extension) inside the export / import folder.")] string fileName="",
            [Description("expectedTagNames: Array of up to 500 distinct nonempty tag names expected after the import (verified).")] string[]? expectedTagNames=null,
            bool dryRun=true)
            => HmiExchangeContract.Run("ExchangeUnifiedTags", !dryRun, true, () => _service.ExchangeUnifiedTags(softwarePath,action,directory,tagTable,fileName,V4Json.Serialize(expectedTagNames ?? Array.Empty<string>()),dryRun));

        [McpServerTool(Name="ExchangeUnifiedScriptModules"), Description("[L2][HMI-Unified][FILE] Native WinCC Unified global script module exchange: export all modules (HmiScriptModuleComposition.Export) or one exact moduleName (HmiScriptModule.Export(dir[, fileName]), the IChromDataExchangeExport contract) into a NEW absolute directory on the MCP server with every file hashed; import calls Scripts.Import(DirectoryInfo[, fileName]) on an existing directory and lists module names before/after. Default preview; import needs dryRun=false; script bodies are not compared (use ReadUnifiedGlobalScript). No save/compile/download. Current native policy; V4 safety behavior is not yet accepted.")]
        public CallToolResult ExchangeUnifiedScriptModules(
            string softwarePath,
            [Description("export | import. ")] string action,
            string directory,
            [Description("moduleName: exact name of the global script module.")] string moduleName="",
            [Description("fileName: file name (with extension) inside the export / import folder.")] string fileName="",
            bool dryRun=true)
            => HmiExchangeContract.Run("ExchangeUnifiedScriptModules", !dryRun, true, () => _service.ExchangeUnifiedScriptModules(softwarePath,action,directory,moduleName,fileName,dryRun));

        [McpServerTool(Name="ImportUnifiedOpcUaAlarms"), Description("[L2][HMI-Unified][WRITE] Official OpcUaAlarm service of one exact Unified HMI connection (OPC UA connections only): read lists DisplayNames and resolves the node id of an optional displayName (native GetNodeId); import reads OPC UA alarm information from an absolute .xml on the MCP server via Import(xmlPath) (native bool) and lists display names afterwards. Alarm types themselves are bound with ManageUnifiedOpcUaAlarmType. Default preview; no save/compile/download.")]
        public CallToolResult ImportUnifiedOpcUaAlarms(
            string softwarePath,
            [Description("connectionName: exact connection name.")] string connectionName,
            [Description("read | import. ")] string action="read",
            [Description("xmlPath: full path of the XML file on the TIA machine.")] string xmlPath="",
            [Description("displayName: display name of the alarm.")] string displayName="",
            bool dryRun=true)
            => HmiExchangeContract.Run("ImportUnifiedOpcUaAlarms", action == "import" && !dryRun, action == "import", () => _service.ImportUnifiedOpcUaAlarms(softwarePath,connectionName,action,xmlPath,displayName,dryRun));
    }
}
