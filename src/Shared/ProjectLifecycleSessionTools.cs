using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.Adapters.Contracts;
using TiaMcpServer.Siemens;
using TiaMcpServer.Siemens.Services;
namespace TiaMcpServer.ModelContextProtocol;
[McpServerToolType]
internal sealed class ProjectLifecycleSessionTools
{
    private readonly ProjectLifecyclePortService _service;
    public ProjectLifecycleSessionTools(ProjectLifecyclePortService service) => _service = service;
        [McpServerTool(Name = "ConnectIsolatedPortal"), Description(
            "[L1][Portal] Start a BRAND-NEW headless TIA Portal instance instead of attaching to a running one. "
            + "It never attaches to, modifies or closes any TIA window or project the user already has open. "
            + "USE THIS when the user is working in the TIA Portal UI: plain ConnectPortal attaches to their instance "
            + "and OpenProject then (correctly) refuses to touch their project, so the whole server is unusable "
            + "until they close it. Must be the FIRST connection tool in a fresh MCP process — calling it after "
            + "another connection leaves an orphaned portal process that later attaches steal. "
            + "Afterwards use OpenProject / CreateProject as usual, then CloseProject and DisconnectPortal." + " Native behaviorPolicy=current; V4 native acceptance is pending.")]
        public CallToolResult ConnectIsolatedPortalV4()
            => SessionToolContract.Run("ConnectIsolatedPortal", true, true, () => ConnectIsolated());

        public ResponseConnect ConnectIsolated()
        {
            try
            {
                return _service.ConnectIsolated(); /* Native creation and the I1 binding are one worker operation. */

            }
            catch (PortalException pex)
            {
                throw new McpException(pex.Message, pex, McpErrorCode.InvalidParams);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Failed to start isolated TIA Portal: {ex.Message}{McpHints.Recovery(ex)}",
                    ex, McpErrorCode.InternalError);
            }
        }


        [McpServerTool(Name = "EnsureOpennessUserGroup"), Description("[L1][Portal]Ensure current Windows user is in TIA Openness user group (may prompt UI). Returns success=true when membership is OK.")]
        public Task<CallToolResult> EnsureOpennessUserGroupV4()
            => SessionToolContract.RunAsync("EnsureOpennessUserGroup", true, false, () => EnsureOpennessUserGroup());

        public async Task<ResponseMessage> EnsureOpennessUserGroup()
        {
            try
            {
                var ok = _service.EnsureGroup();
                return new ResponseMessage
                {
                    Message = ok ? "Openness user group OK" : "Openness user group NOT OK",
                    Meta = ResponseMeta.Basic(DateTime.Now, ok)
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error ensuring Openness user group: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }


        [McpServerTool(Name="GetPortalInfo"), Description("[L2][Portal][READ] Diagnostic snapshot of every running TIA Portal process (TiaPortalProcess: Id, Mode WithUserInterface/WithoutUserInterface, Path, ProjectPath, AcquisitionTime; AttachedSessions with Id/Version/IsActive/AttachTime/UtilizationTime/AccessLevel/TrustAuthority/ProcessPath/ProcessId; InstalledSoftware = TiaPortalProduct Name/Version/Options), the bound process, the bound project's TextCategories (Identifier/Name) and HwUtilities (Identifier, class), ObjectIdentifierProvider availability and the explicitly bound project name. Non-blocking, read-only; works without a project.")]
        public CallToolResult GetPortalInfoV4(
            [Description("includeProcesses: list the TIA Portal processes on the machine.")] bool includeProcesses=true,
            [Description("includeSessions: list the sessions of the bound portal.")] bool includeSessions=true,
            [Description("includeProducts: list the installed TIA products / option packages (TiaPortalProduct).")] bool includeProducts=true)
            => SessionToolContract.Run("GetPortalInfo", false, false, () => ReadPortalInfo(includeProcesses, includeSessions, includeProducts));

        public ResponseMessage ReadPortalInfo(
            [Description("includeProcesses: list the TIA Portal processes on the machine.")] bool includeProcesses=true,
            [Description("includeSessions: list the sessions of the bound portal.")] bool includeSessions=true,
            [Description("includeProducts: list the installed TIA products / option packages (TiaPortalProduct).")] bool includeProducts=true)
            => _service.ReadPortalInfo(includeProcesses,includeSessions,includeProducts);

}
