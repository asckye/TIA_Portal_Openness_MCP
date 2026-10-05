using TiaMcp.Logic.V4;
using TiaMcp.Logic.V4.Inputs;
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
using TiaMcpServer.Siemens.Services;

namespace TiaMcpServer.ModelContextProtocol
{
    [McpServerToolType]
    internal sealed class OpcUaTools
    {
        private readonly OpcUaService _service;

        public OpcUaTools(OpcUaService service) => _service = service;

        [McpServerTool(Name = "GetPlcOpcUaConfiguration"), Description(
            "[L2][Category:PLC-OpcUA][PreCondition:Connect+OpenProject]" +
            " Read the full OPC UA server configuration for a PLC: server interfaces, SIMATIC interfaces, and reference namespaces — each with their Name, Enabled state, and key properties." +
            " Use this to audit what OPC UA interfaces exist before enabling or exporting them." +
            " Enabled=true means the interface is active and will be downloaded to the CPU. Current native policy; V4 safety behavior is not yet accepted.")]
        public CallToolResult GetOpcUaConfigV4(
            [Description("softwarePath: path to the PLC software, e.g. 'PLC_1'")] string softwarePath)
            => EngineeringToolContract.Run("GetPlcOpcUaConfiguration", false, () => GetOpcUaConfig(softwarePath));

        public ResponseJsonReport GetOpcUaConfig(
            string softwarePath)
        {
            try { return _service.GetOpcUaConfig(softwarePath); }
            catch (Exception ex) when (ex is not McpException)
            { throw new McpException($"Unexpected error reading OPC UA config: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError); }
        }

        [McpServerTool(Name = "ManageOpcUaInterface"), Description(
            "[L2][Category:PLC-OpcUA][WRITE] Read or delete ONE OPC UA server interface, SIMATIC interface or reference namespace of a PLC (ServerInterfaceGroup.ServerInterfaces / SimaticInterfaces / ReferenceNamespaces, native Delete with readback)." +
            " Use it to remove an interface that blocks compilation (an empty server interface makes the whole PLC fail with 'The OPC UA server interface ... is empty or does not contain unique nodes'). Default preview; dryRun=false deletes; no save/compile/download. Current native policy; V4 safety behavior is not yet accepted.")]
        public CallToolResult ManageOpcUaInterfaceV4(
            [Description("softwarePath: path to the PLC software, e.g. 'PLC_1'")] string softwarePath,
            [Description("interfaceName: exact name as shown in GetPlcOpcUaConfiguration")] string interfaceName,
            [Description("read | delete. action: read (default) or delete")] string action = "read",
            [Description("interfaceType: 'ServerInterface' (default), 'SimaticInterface', or 'ReferenceNamespace'")] string interfaceType = "ServerInterface",
            [Description("dryRun: true (default) previews; false deletes")] bool dryRun = true)
            => EngineeringToolContract.Run("ManageOpcUaInterface", !dryRun && action != "read" && action != "list", () => ManageOpcUaInterface(softwarePath, interfaceName, action, interfaceType, dryRun));

        public ResponseMessage ManageOpcUaInterface(
            string softwarePath,
            string interfaceName,
            string action = "read",
            string interfaceType = "ServerInterface",
            bool dryRun = true)
            => _service.ManageOpcUaInterface(softwarePath, interfaceName, action, interfaceType, dryRun);

        [McpServerTool(Name = "SetOpcUaInterfaceEnabled"), Description(
            "[L2][Category:PLC-OpcUA][PreCondition:Connect+OpenProject]" +
            " Enable or disable an OPC UA server interface, SIMATIC interface, or reference namespace." +
            " Setting Enabled=true activates the interface — download to PLC is required for the change to take effect on the CPU." +
            " interfaceType options: 'ServerInterface' (default), 'SimaticInterface', 'ReferenceNamespace'." +
            " Workflow: GetPlcOpcUaConfiguration → SetOpcUaInterfaceEnabled → DownloadPlc. Current native policy; V4 safety behavior is not yet accepted.")]
        public CallToolResult SetOpcUaInterfaceEnabledV4(
            [Description("softwarePath: path to the PLC software, e.g. 'PLC_1'")] string softwarePath,
            [Description("interfaceName: exact name of the interface as shown in GetPlcOpcUaConfiguration")] string interfaceName,
            [Description("enabled: true to enable, false to disable")] bool enabled,
            [Description("interfaceType: 'ServerInterface' (default), 'SimaticInterface', or 'ReferenceNamespace'")] string interfaceType = "ServerInterface")
            => EngineeringToolContract.Run("SetOpcUaInterfaceEnabled", true, () => SetOpcUaInterfaceEnabled(softwarePath, interfaceName, enabled, interfaceType));

        public ResponseMessage SetOpcUaInterfaceEnabled(
            string softwarePath,
            string interfaceName,
            bool enabled,
            string interfaceType = "ServerInterface")
        {
            try { return _service.SetOpcUaInterfaceEnabled(softwarePath, interfaceName, enabled, interfaceType); }
            catch (Exception ex) when (ex is not McpException)
            { throw new McpException($"Unexpected error setting OPC UA interface enabled state: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError); }
        }

        [McpServerTool(Name = "ExportOpcUaInterface"), Description(
            "[L2][Category:PLC-OpcUA][PreCondition:Connect+OpenProject]" +
            " Export an OPC UA server interface or reference namespace to an XML file." +
            " The exported XML can be inspected, modified, and re-imported." +
            " interfaceType: 'ServerInterface' (default), 'SimaticInterface', 'ReferenceNamespace'. Current native policy; V4 safety behavior is not yet accepted.")]
        public CallToolResult ExportOpcUaInterfaceV4(
            [Description("softwarePath: path to the PLC software, e.g. 'PLC_1'")] string softwarePath,
            [Description("interfaceName: exact name of the interface to export")] string interfaceName,
            [Description("exportPath: full file path for the XML output, e.g. 'C:\\Temp\\OpcUa_Interface.xml'")] string exportPath,
            [Description("interfaceType: 'ServerInterface' (default), 'SimaticInterface', or 'ReferenceNamespace'")] string interfaceType = "ServerInterface")
            => EngineeringToolContract.Run("ExportOpcUaInterface", true, () => ExportOpcUaInterface(softwarePath, interfaceName, exportPath, interfaceType));

        public ResponseMessage ExportOpcUaInterface(
            string softwarePath,
            string interfaceName,
            string exportPath,
            string interfaceType = "ServerInterface")
        {
            try { return _service.ExportOpcUaInterface(softwarePath, interfaceName, exportPath, interfaceType); }
            catch (Exception ex) when (ex is not McpException)
            { throw new McpException($"Unexpected error exporting OPC UA interface: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError); }
        }

        [McpServerTool(Name = "ImportOpcUaInterface"), Description(
            "[L2][Category:PLC-OpcUA][PreCondition:Connect+OpenProject]" +
            " Import an OPC UA server interface or reference namespace from an XML file." +
            " If an interface with the same name (derived from the file name) already exists, it is updated in place." +
            " Otherwise a new interface is created." +
            " Download to PLC after import to apply changes to the CPU." +
            " interfaceType: 'ServerInterface' (default), 'ReferenceNamespace'. Current native policy; V4 safety behavior is not yet accepted.")]
        public CallToolResult ImportOpcUaInterfaceV4(
            [Description("softwarePath: path to the PLC software, e.g. 'PLC_1'")] string softwarePath,
            [Description("importPath: full file path to the XML file")] string importPath,
            [Description("interfaceType: 'ServerInterface' (default) or 'ReferenceNamespace'")] string interfaceType = "ServerInterface")
            => EngineeringToolContract.Run("ImportOpcUaInterface", true, () => ImportOpcUaInterface(softwarePath, importPath, interfaceType));

        public ResponseMessage ImportOpcUaInterface(
            string softwarePath,
            string importPath,
            string interfaceType = "ServerInterface")
        {
            try { return _service.ImportOpcUaInterface(softwarePath, importPath, interfaceType); }
            catch (Exception ex) when (ex is not McpException)
            { throw new McpException($"Unexpected error importing OPC UA interface: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError); }
        }

        [McpServerTool(Name = "GenerateOpcUaModelledInterface"), Description("[L2][PLC-OpcUA][FILE] Generate a user-modelled OPC UA NodeSet XML from one PLC or exact software unit using the Siemens MIT generator source: exported data types, accessible tags, global/instance DBs, optional project folders and empty DBs. Default dryRun=true validates targets/options without exporting. accessLevels maps Inputs/Outputs/Memory/Counters/Timers/GlobalDBs/InstanceDBs/SafetyGlobalDBs/SafetyInstanceDBs to 0 exclude, 1 read, 2 write, 3 read/write, 4 project permissions; safety allows only 0/1, default 1, other areas default 4. NEW absolute .xml output. Optimized nodes, string IDs only; nested FB member access remains unsupported upstream. Reports skipped nodes/warnings. Exports native data to isolated scratch files; no UI, project import, save, compile or download. Use existing ImportOpcUaInterface separately; output not live-validated yet. Current native policy; V4 safety behavior is not yet accepted.")]
        public CallToolResult GenerateOpcUaModelledInterfaceV4([Description("Exact PLC software path in the bound project.")] string softwarePath, [Description("Name for the generated OPC UA interface, 1..128 characters.")] string interfaceName, [Description("Absolute OPC UA namespace URI, for example urn:company:machine.")] string namespaceUri, [Description("New absolute output file; existing files are refused.")] string outputPath,
            [Description("Optional exact software unit name; empty selects the PLC scope.")] string unitName = "", [Description("Include project folders in the generated address space.")] bool keepFolderStructure = false, [Description("Retain data blocks without accessible variables.")] bool keepEmptyDataBlocks = false, [Description("Area-to-level map; see tool description; safety permits only 0 or 1.")] AttributeMap<int> accessLevels = null!, [Description("true previews without executing the requested write/action; false executes.")] bool dryRun = true)
            => EngineeringToolContract.Run("GenerateOpcUaModelledInterface", !dryRun, () => GenerateOpcUaModelledInterface(softwarePath, interfaceName, namespaceUri, outputPath, unitName, keepFolderStructure, keepEmptyDataBlocks, EngineeringToolContract.Json(accessLevels), dryRun));

        public ResponseMessage GenerateOpcUaModelledInterface(string softwarePath, string interfaceName, string namespaceUri, string outputPath,
            string unitName = "", bool keepFolderStructure = false, bool keepEmptyDataBlocks = false, string accessLevelsJson = "{}", bool dryRun = true)
        {
            try { return new ResponseMessage { Message = dryRun ? "Generation preview." : "Model XML generated; inspect warnings and validate by separate import/compile.", Meta = _service.GenerateOpcUaModelledInterface(softwarePath, interfaceName, namespaceUri, outputPath, unitName, keepFolderStructure, keepEmptyDataBlocks, accessLevelsJson, dryRun) }; }
            catch (Exception ex) { return new ResponseMessage { Message = "Generation failed: " + ex.Message, Meta = new JsonObject { ["success"] = false, ["error"] = ex.Message, ["mayHaveWrittenFiles"] = !dryRun, ["imported"] = false } }; }
        }

        [McpServerTool(Name="GetOpcUaAccessControl"), Description("[L2][PLC-OpcUA][READ] Read the PLC OPC UA server access control (ServerInterfaceGroup.AccessControl): section roles (RoleMappings with per-namespace permissions) or restrictions (NamespaceAccessRestrictions). Exact softwarePath; offset/limit pagination. NotSupported on TIA V20. No secrets, nothing modified. Current native policy; V4 safety behavior is not yet accepted.")]
        public CallToolResult ReadOpcUaAccessControlV4(
            string softwarePath,
            [Description("section: roles | restrictions.")] string section="roles",
            int offset=0,
            int limit=100)
            => EngineeringToolContract.Run("GetOpcUaAccessControl", false, () => ReadOpcUaAccessControl(softwarePath, section, offset, limit));

        public ResponseMessage ReadOpcUaAccessControl(
            string softwarePath,
            string section="roles",
            int offset=0,
            int limit=100)
            => _service.ReadOpcUaAccessControl(softwarePath,section,offset,limit);

        [McpServerTool(Name="ManageOpcUaAccessControl"), Description("[L2][PLC-OpcUA][WRITE] Modify PLC OPC UA server access control: createRole (roleName, definedInNamespace), addStandardRole (roleName), deleteRole (roleName), setProjectRole (roleName, projectRole), setPermission (roleName, exact namespaceUri, permission Browse|Read|Write|Call|ReceiveEvents|ReadRolePermissions, enabled), setRestriction (exact namespaceUri, properties of scalar properties). Exact names only; ambiguity refused. Real run needs confirmChange=true, Offline PLC and exclusive access; readback verified. NotSupported on TIA V20. Default preview; no save/compile/download. Current native policy; V4 safety behavior is not yet accepted.")]
        public CallToolResult ManageOpcUaAccessControlV4(
            string softwarePath,
            [Description("action: the operation to perform - createRole | addStandardRole | deleteRole | setProjectRole | setPermission | setRestriction.")] string action,
            [Description("roleName: exact role name.")] string roleName="",
            [Description("definedInNamespace: namespace URI in which the role is defined.")] string definedInNamespace="",
            [Description("projectRole: exact project role name.")] string projectRole="",
            [Description("namespaceUri: OPC UA namespace URI.")] string namespaceUri="",
            [Description("permission: Browse | Read | Write | Call | ReceiveEvents | ReadRolePermissions.")] string permission="",
            [Description("enabled: true enables, false disables.")] bool enabled=false,
            AttributeMap<Scalar> properties = null!,
            bool confirmChange=false,
            bool dryRun=true)
            => EngineeringToolContract.Run("ManageOpcUaAccessControl", !dryRun && action != "read" && action != "list", () => ManageOpcUaAccessControl(softwarePath, action, roleName, definedInNamespace, projectRole, namespaceUri, permission, enabled, EngineeringToolContract.Json(properties), confirmChange, dryRun));

        public ResponseMessage ManageOpcUaAccessControl(
            string softwarePath,
            string action,
            string roleName="",
            string definedInNamespace="",
            string projectRole="",
            string namespaceUri="",
            string permission="",
            bool enabled=false,
            string propertiesJson="{}",
            bool confirmChange=false,
            bool dryRun=true)
            => _service.ManageOpcUaAccessControl(softwarePath,action,roleName,definedInNamespace,projectRole,namespaceUri,permission,enabled,propertiesJson,confirmChange,dryRun);
    }
}
