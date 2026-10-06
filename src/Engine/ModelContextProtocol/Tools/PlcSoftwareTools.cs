using TiaMcp.Logic.V4.Domain;
using TiaMcp.Logic.V4.Inputs;
using TiaMcp.Logic.V4;
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
using static TiaMcpServer.ModelContextProtocol.McpServer;

namespace TiaMcpServer.ModelContextProtocol
{
    [McpServerToolType]
    internal sealed class PlcSoftwareTools
    {
        private readonly PlcSoftwareService _software;
        private readonly IEngineeringSession _session;

        public PlcSoftwareTools(PlcSoftwareService software, IEngineeringSession session)
        {
            _software = software;
            _session = session;
        }

        [McpServerTool(Name = "GetSoftwareInfo"), Description("[L1][PLC-Software] Get PLC software properties (language, version, block counts). Requires: ConnectPortal + OpenProject. softwarePath comes from GetProjectTree (e.g. 'PLC_1'). Use GetSoftwareTree for the full block hierarchy. Current native policy; V4 safety behavior is not yet accepted.")]
        public CallToolResult GetSoftwareInfoV4(
            [Description("softwarePath: defines the path in the project structure to the plc software")] string softwarePath)
            => PlcToolContract.Run("GetSoftwareInfo", false, true, () => GetSoftwareInfo(softwarePath));

        public ResponseSoftwareInfo GetSoftwareInfo(
            string softwarePath)
        {
            try
            {
                var software = _session.GetPlcSoftware(softwarePath);
                if (software != null)
                {

                    var attributes = Helper.GetAttributeList(software);

                    return new ResponseSoftwareInfo
                    {
                        Message = $"Software info retrieved from '{softwarePath}'",
                        Name = software.Name,
                        Attributes = attributes,
                        Description = software.ToString(),
                        Meta = ResponseMeta.Basic(DateTime.Now, true)
                    };
                }
                else
                {
                    throw new McpException($"Software not found at '{softwarePath}'" + _session.AvailablePlcPathsSuffix(), McpErrorCode.InternalError);
                }
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error retrieving software info from '{softwarePath}': {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "CompilePlcSoftware"), Description("[L1][PLC-Software] Compile all blocks in the PLC software. Requires: ConnectPortal + OpenProject. Returns basic success/failure. For structured error/warning details use CompilePlcDiagnostics instead. Must compile before ExportPlcBlock if any blocks are inconsistent. After adding new blocks via import, always compile to catch type/interface mismatches. Current native policy; V4 safety behavior is not yet accepted. Native behaviorPolicy=current; V4 native acceptance is pending.")]
        public CallToolResult CompileSoftwareV4(
            [Description("softwarePath: defines the path in the project structure to the plc software")] string softwarePath,
            [Description("password: the password to access adminsitration, default: no password")] string password = "")
            => PlcToolContract.Run("CompilePlcSoftware", true, true, () => CompileSoftware(softwarePath, password));

        public ResponseCompile CompileSoftware(
            string softwarePath,
            string password = "")
        {
            try
            {
                var compileWatch = System.Diagnostics.Stopwatch.StartNew();
                var result = OnlineToolPolicy.WithAutoOffline(() => _session.CompileSoftware(softwarePath, password), softwarePath);
                var compileMs = compileWatch.ElapsedMilliseconds;
                var collected = CompilerDiagnostics.CollectCompilerMessages(result.Messages);
                var summary = collected.Summary(result.State.ToString(), result.ErrorCount, result.WarningCount);
                summary["compileElapsedMs"] = compileMs;
                summary["softwarePath"] = softwarePath;
                // envelope: legacy-late-stamp
                summary["timestamp"] = DateTime.Now;

                return new ResponseCompile
                {
                    Message = $"Software '{softwarePath}' compile state={summary["effectiveState"]}; root counts and diagnostics scopes are in Meta.",
                    State = summary["effectiveState"]!.ToString(),
                    ErrorCount = collected.HasError && result.ErrorCount == 0 ? (int?)null : result.ErrorCount,
                    WarningCount = collected.HasWarning && result.WarningCount == 0 ? (int?)null : result.WarningCount,
                    Messages = collected.Raw,
                    Meta = summary
                };
            }
            catch (PortalException pex)
            {
                throw new McpException($"Failed compiling software '{softwarePath}' [{pex.Code}]: {pex.Message}", pex, McpErrorCode.InternalError);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error compiling software '{softwarePath}': {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "GetSoftwareTree"), Description("[L1][PLC-Software] Get the PLC user block/type hierarchy as ASCII tree; system block groups and external sources are excluded. Inspect meta.dataComplete for unreadable attributes. Requires: ConnectPortal + OpenProject. softwarePath from GetProjectTree (e.g. 'PLC_1'). ALWAYS call before ExportPlcBlock/ImportPlcBlock to get exact group paths (e.g. 'Program blocks/FBs/FB_Motor'). Returns OB/FB/FC/GlobalDB/UDT/ExternalSource blocks with group hierarchy. Current native policy; V4 safety behavior is not yet accepted.")]
        public CallToolResult GetSoftwareTreeV4(
            [Description("softwarePath: defines the path in the project structure to the plc software")] string softwarePath)
            => PlcToolContract.Run("GetSoftwareTree", false, true, () => GetSoftwareTree(softwarePath));

        public ResponseSoftwareTree GetSoftwareTree(
            string softwarePath)
        {
            try
            {
                var tree = _software.GetSoftwareTree(softwarePath, out var metadata);

                if (!string.IsNullOrEmpty(tree))
                {
                    return new ResponseSoftwareTree
                    {
                        Message = $"Software tree retrieved from '{softwarePath}'",
                        Tree = "```\n" + tree + "\n```",
                        Meta = metadata
                    };
                }
                else
                {
                    throw new McpException($"Failed retrieving software tree from '{softwarePath}'", McpErrorCode.InternalError);
                }
            }
            catch (PortalException pex)
            {
                // 路径解析不到是调用方的参数问题，不是服务器内部意外错误。
                throw new McpException(pex.Message, pex,
                    pex.Code == PortalErrorCode.NotFound ? McpErrorCode.InvalidParams : McpErrorCode.InternalError);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error retrieving software tree from '{softwarePath}': {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }
    }
}
