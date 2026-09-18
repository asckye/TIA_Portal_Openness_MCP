using System.ComponentModel;
using ModelContextProtocol.Server;
namespace TiaMcpServer.ModelContextProtocol
{
    public static partial class McpServer
    {
        [McpServerTool(Name="ExchangeUnifiedTags"), Description("[L2][HMI-Unified][FILE] Native WinCC Unified tag exchange in WinCC ML (YAML): export writes <fileName>.hmi.yml (plus TIA side files) via HmiTagComposition.Export(DirectoryInfo[, name]) into a NEW absolute directory on the MCP server and hashes every file; import calls Import(DirectoryInfo[, name]) on an existing directory and checks expectedTagNamesJson (exact names) afterwards. tagTable = unique table name or /Group/Sub/Table (empty = the device root Tags composition). Default preview; import needs dryRun=false and may also touch other tags in the file; tag properties are not independently compared. No save/compile/download.")]
        public static ResponseMessage ExchangeUnifiedTags(string softwarePath,string action,string directory,string tagTable="",string fileName="",string expectedTagNamesJson="[]",bool dryRun=true)
            => Portal.ExchangeUnifiedTags(softwarePath,action,directory,tagTable,fileName,expectedTagNamesJson,dryRun);
        [McpServerTool(Name="ExchangeUnifiedScriptModules"), Description("[L2][HMI-Unified][FILE] Native WinCC Unified global script module exchange: export all modules (HmiScriptModuleComposition.Export) or one exact moduleName (HmiScriptModule.Export(dir[, fileName]), the IChromDataExchangeExport contract) into a NEW absolute directory on the MCP server with every file hashed; import calls Scripts.Import(DirectoryInfo[, fileName]) on an existing directory and lists module names before/after. Default preview; import needs dryRun=false; script bodies are not compared (use ReadUnifiedGlobalScript). No save/compile/download.")]
        public static ResponseMessage ExchangeUnifiedScriptModules(string softwarePath,string action,string directory,string moduleName="",string fileName="",bool dryRun=true)
            => Portal.ExchangeUnifiedScriptModules(softwarePath,action,directory,moduleName,fileName,dryRun);
        [McpServerTool(Name="ImportUnifiedOpcUaAlarms"), Description("[L2][HMI-Unified][WRITE] Official OpcUaAlarm service of one exact Unified HMI connection (OPC UA connections only): read lists DisplayNames and resolves the node id of an optional displayName (native GetNodeId); import reads OPC UA alarm information from an absolute .xml on the MCP server via Import(xmlPath) (native bool) and lists display names afterwards. Alarm types themselves are bound with ManageUnifiedOpcUaAlarmType. Default preview; no save/compile/download.")]
        public static ResponseMessage ImportUnifiedOpcUaAlarms(string softwarePath,string connectionName,string action="read",string xmlPath="",string displayName="",bool dryRun=true)
            => Portal.ImportUnifiedOpcUaAlarms(softwarePath,connectionName,action,xmlPath,displayName,dryRun);
    }
}
