using System;
using System.ComponentModel;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;

namespace TiaMcpServer.ModelContextProtocol
{
    public static partial class McpServer
    {
        [McpServerTool(Name = "GenerateOpcUaModelledInterface"), Description("[L2][PLC-OpcUA][FILE] Generate a user-modelled OPC UA NodeSet XML from one PLC or exact software unit using the Siemens MIT generator source: exported data types, accessible tags, global/instance DBs, optional project folders and empty DBs. Default dryRun=true validates targets/options without exporting. accessLevelsJson maps Inputs/Outputs/Memory/Counters/Timers/GlobalDBs/InstanceDBs/SafetyGlobalDBs/SafetyInstanceDBs to 0 exclude, 1 read, 2 write, 3 read/write, 4 project permissions; safety allows only 0/1, default 1, other areas default 4. NEW absolute .xml output. Optimized nodes, string IDs only; nested FB member access remains unsupported upstream. Reports skipped nodes/warnings. Exports native data to isolated scratch files; no UI, project import, save, compile or download. Use existing ImportOpcUaInterface separately; output not live-validated yet.")]
        public static ResponseMessage GenerateOpcUaModelledInterface([Description("Exact PLC software path in the bound project.")] string softwarePath, [Description("Name for the generated OPC UA interface, 1..128 characters.")] string interfaceName, [Description("Absolute OPC UA namespace URI, for example urn:company:machine.")] string namespaceUri, [Description("New absolute output file; existing files are refused.")] string outputPath,
            [Description("Optional exact software unit name; empty selects the PLC scope.")] string unitName = "", [Description("Include project folders in the generated address space.")] bool keepFolderStructure = false, [Description("Retain data blocks without accessible variables.")] bool keepEmptyDataBlocks = false, [Description("JSON area-to-level map; see tool description; safety permits only 0 or 1.")] string accessLevelsJson = "{}", [Description("true previews without executing the requested write/action; false executes.")] bool dryRun = true)
        {
            try { return new ResponseMessage { Message = dryRun ? "Generation preview." : "Model XML generated; inspect warnings and validate by separate import/compile.", Meta = Portal.GenerateOpcUaModelledInterface(softwarePath, interfaceName, namespaceUri, outputPath, unitName, keepFolderStructure, keepEmptyDataBlocks, accessLevelsJson, dryRun) }; }
            catch (Exception ex) { return new ResponseMessage { Message = "Generation failed: " + ex.Message, Meta = new JsonObject { ["success"] = false, ["error"] = ex.Message, ["mayHaveWrittenFiles"] = !dryRun, ["imported"] = false } }; }
        }
    }
}
