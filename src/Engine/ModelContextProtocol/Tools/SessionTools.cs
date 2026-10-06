using ModelContextProtocol.Protocol;
using TiaMcp.Logic.V4.Inputs;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using TiaMcpServer.Siemens;
using static TiaMcpServer.ModelContextProtocol.McpServer;

namespace TiaMcpServer.ModelContextProtocol
{
    [McpServerToolType]
    internal sealed class SessionTools
    {
        private readonly IEngineeringSession _session;

        public SessionTools(IEngineeringSession session) => _session = session;

        [McpServerTool(Name = "ConnectPortal"), Description("[L1][Portal][SESSION] Explicit connection by optional exact project filename stem. Uses running-process metadata before Attach, refuses multiple matching instances, reserves the chosen TIA instance against other same-user MCP processes, and captures PID/start time/full project path. Prefer ConnectProject for exact identity from ListPortalProcessProjects. Without a name, exactly one existing process is required; with none running a new instance starts. No fallback to another project or automatic retry. Use ConnectIsolatedPortal to start a separate headless instance. Meta contains boundProcessId, startedNew and binding. Native behaviorPolicy=current; V4 native acceptance is pending.")]
        public CallToolResult ConnectPortalV4(
            [Description("projectName: optional exact project filename stem. Duplicate matches are refused; use ConnectProject with full identity.")] string projectName = "",
            [Description("allowStart: compatibility parameter. Attach failures never trigger automatic startup; use ConnectIsolatedPortal for an explicit new instance.")] bool allowStart = false)
            => SessionToolContract.Run("ConnectPortal", true, true, () => Connect(projectName, allowStart));

        public ResponseConnect Connect(
            [Description("projectName: optional exact project filename stem. Duplicate matches are refused; use ConnectProject with full identity.")] string projectName = "",
            [Description("allowStart: compatibility parameter. Attach failures never trigger automatic startup; use ConnectIsolatedPortal for an explicit new instance.")] bool allowStart = false)
        {
            Logger?.LogInformation("Connecting to TIA Portal...");

            try
            {
                // ConnectPortal 失败时抛 PortalException（结构化错误码），下方 catch 统一映射到 McpException
                // envelope: legacy-stamp-then-verdict
                var info = new JsonObject { ["timestamp"] = DateTime.Now };
                _session.ConnectPortal(string.IsNullOrWhiteSpace(projectName) ? null : projectName, allowStart, info);
                info["success"] = true;
                var bound = info["boundProcessId"]?.ToString();
                var startedNew = info["startedNew"]?.GetValue<bool>() == true;
                return new ResponseConnect
                {
                    Message = startedNew ? "Started a new TIA Portal instance (PID " + bound + ") - no TIA Portal was running" + (allowStart ? " that could be attached" : "") + "."
                                         : "Connected to TIA-Portal (attached to PID " + bound + ")" + (info["warning"] != null ? "; " + info["warning"]!.GetValue<string>().TrimEnd('.') : "") + ".",
                    Meta = info
                };
            }
            catch (PortalException pex)
            {
                throw new McpException($"Failed to connect to TIA-Portal [{pex.Code}]: {pex.Message}", pex, McpErrorCode.InternalError);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error connecting to TIA-Portal: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

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
                _session.ConnectIsolatedPortal();

                // 回读句柄才算证据，不拿「没抛异常」当成功。
                bool connected = _session.IsConnected();
                return new ResponseConnect
                {
                    Message = connected
                        ? "Connected to isolated headless TIA Portal (no existing TIA window or project was touched)."
                        : "Unverified: ConnectIsolatedPortal returned without an error, but the portal handle could not be read. "
                          + "Whether the isolated instance started is unknown. Inspect GetSessionState before continuing.",
                    Meta = ResponseMeta.Basic(DateTime.Now, connected, ("verified", connected), ("isolated", true))
                };
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

        [McpServerTool(Name = "ListPortalProcessProjects"), Description("[L1][Portal]List running TIA process IDs, ISO start times and full project paths from process metadata without attaching. Use these values with ConnectProject.")]
        public CallToolResult ListPortalProcessProjectsV4()
            => SessionToolContract.Run("ListPortalProcessProjects", false, false, () => ListPortalProcessProjects());

        public ResponseStringList ListPortalProcessProjects()
        {
            try
            {
                var items = _session.ListPortalProcessProjects();
                return new ResponseStringList
                {
                    Message = "TIA Portal processes inspected",
                    Items = items,
                    Meta = ResponseMeta.Basic(DateTime.Now, true)
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error listing TIA Portal processes: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "EnsureOpennessUserGroup"), Description("[L1][Portal]Ensure current Windows user is in TIA Openness user group (may prompt UI). Returns success=true when membership is OK.")]
        public Task<CallToolResult> EnsureOpennessUserGroupV4()
            => SessionToolContract.RunAsync("EnsureOpennessUserGroup", true, false, () => EnsureOpennessUserGroup());

        public async Task<ResponseMessage> EnsureOpennessUserGroup()
        {
            try
            {
                var ok = await Siemens.Openness.IsUserInGroup();
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

        [McpServerTool(Name = "DisconnectPortal"), Description("[L1][Portal] DisconnectPortal from TIA Portal and release the Openness handle. Call after all project work is done. Any unsaved changes will be lost — call SaveProject first if needed. Native behaviorPolicy=current; V4 native acceptance is pending.")]
        public CallToolResult DisconnectPortalV4()
            => SessionToolContract.Run("DisconnectPortal", true, true, () => Disconnect());

        public ResponseDisconnect Disconnect()
        {
            try
            {
                if (_session.DisconnectPortal())
                {
                    return new ResponseDisconnect
                    {
                        Message = "Disconnected from TIA-Portal",
                        Meta = ResponseMeta.Basic(DateTime.Now, true)
                    };
                }
                else
                {
                    throw new McpException("Failed disconnecting from TIA-Portal", McpErrorCode.InternalError);
                }
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error disconnecting from TIA-Portal: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "GetSessionState"), Description("[L0][Portal] Get cached connection identity and OS process liveness: IsConnected, bound Project name, Session name, PID/start/path/generation and journal health. Does not query project collections, attach or rebind. Use this to check preconditions before other tools — if IsConnected=false, call ConnectPortal first; if Project is empty, call OpenProject or CreateProject.")]
        public CallToolResult GetSessionStateV4()
            => SessionToolContract.Run("GetSessionState", false, false, () => GetState());

        public ResponseState GetState()
        {
            try
            {
                var state = _session.GetState();

                if (state != null)
                {
                    return new ResponseState
                    {
                        Message = "TIA-Portal MCP server state retrieved",
                        IsConnected = state.IsConnected,
                        Project = state.Project,
                        Session = state.Session,
                        // envelope: legacy-late-verdict
                        Meta = new JsonObject
                        {
                            ["timestamp"] = DateTime.Now,
                            ["hmiReadHealth"] = _session.GetHmiReadHealth(),
                            ["binding"] = _session.GetBindingIdentity(),
                            ["journalHealth"] = InvocationJournal.Health(),
                            ["success"] = true
                        }
                    };
                }
                else
                {
                    throw new McpException("Failed to retrieve TIA-Portal MCP server state", McpErrorCode.InternalError);
                }
                

            }
            catch (Exception ex) when (ex is not McpException)
            {
                var process = _session.GetPortalProcessHealth();
                var dead = process["processAlive"] is JsonValue alive && alive.TryGetValue<bool>(out var running) && !running;
                throw new McpException($"Unexpected error retrieving TIA-Portal MCP server state: {ex.Message}{McpHints.Recovery(ex)}" + (dead ? $"  ▶ TIA Portal process {process["boundProcessId"]} is no longer running (crashed or closed): restart TIA Portal, reopen the project, then AttachOpenProject." : ""), ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "InitializeEnvironment"), Description("[L0][Bootstrap] FIRST tool any AI model should call. Read-only single-call orientation: returns TIA version, Openness group status, current connection/project state, the recommended next tool, the L0/L1 tool roster, and known TIA Openness limitations. Does NOT connect to TIA Portal — call ConnectPortal afterwards based on RecommendedNextTool.")]
        public Task<CallToolResult> InitializeEnvironmentV4()
            => SessionToolContract.RunAsync("InitializeEnvironment", false, false, () => Bootstrap());

        public async Task<ResponseBootstrap> Bootstrap()
        {
            try
            {
                var env = new BootstrapEnvironment
                {
                    TiaVersionInUse = Engineering.TiaMajorVersion == 0 ? (int?)null : Engineering.TiaMajorVersion,
                    TiaVersionDetected = Engineering.DetectTiaMajorVersion(),
                    TiaInstallPath = Environment.GetEnvironmentVariable("TiaPortalLocation"),
                    Transport = Environment.GetEnvironmentVariable("MCP_TRANSPORT") ?? "stdio",
                };

                bool isolatedParent = TiaMcpServer.Isolation.IsolatedWorkerHost.Current != null
                    && !TiaMcpServer.Isolation.IsolatedWorkerHost.IsChild;
                if (Runtime.OpennessReadiness.GroupOk.HasValue) env.OpennessGroupOk = Runtime.OpennessReadiness.GroupOk.Value;
                else if (isolatedParent) env.OpennessGroupOk = Runtime.EnvironmentDoctor.CurrentUserInOpennessGroup();
                else if (Runtime.OpennessReadiness.Ready)
                {
                    try { env.OpennessGroupOk = Siemens.Openness.IsUserInGroupNoFix(); }
                    catch /* swallow(env-probe): if group membership cannot be checked, Bootstrap does not claim Openness access is ready */ { env.OpennessGroupOk = false; }
                }

                if (isolatedParent && Runtime.OpennessReadiness.Ready)
                {
                    if (env.OpennessGroupOk != true)
                    {
                        const string cause = "Current user is not in the required Siemens TIA Openness group.";
                        const string fix = "Add the current Windows user to the local 'Siemens TIA Openness' group, sign out and back in, then restart the MCP client.";
                        Runtime.OpennessReadiness.MarkUnavailable(cause, fix, fix, false);
                    }
                    else
                    {
                        var check = Runtime.EnvironmentDoctor.Run(EngineRouter.CompiledTiaMajorVersion, env.TiaVersionDetected)
                            .FirstOrDefault(item => item.Gating && !item.Ok);
                        if (check != null)
                            Runtime.OpennessReadiness.MarkUnavailable(check.DetailEn, check.FixEn ?? "Run `tia doctor` for repair steps.",
                                check.FixZh ?? check.FixEn ?? "Run `tia doctor` for repair steps.", true);
                        else Runtime.OpennessReadiness.MarkReady(true);
                    }
                }

                var portalDto = new BootstrapPortal();
                try
                {
                    if (!Runtime.OpennessReadiness.Ready) throw new InvalidOperationException("TIA Openness is not ready.");
                    var st = _session.GetState();
                    portalDto.Connected = st?.IsConnected;
                    portalDto.ProjectName = st?.Project;
                    portalDto.SessionName = st?.Session;
                }
                catch /* swallow(probe-optional): Bootstrap remains available with a disconnected indication when the current Portal state cannot be read */ { portalDto.Connected = false; }
                portalDto.LastConnectError = _session.LastConnectError;

                string nextTool;
                string reason;
                if (!Runtime.OpennessReadiness.Ready)
                {
                    if (env.OpennessGroupOk == false)
                    {
                        nextTool = "EnsureOpennessUserGroup";
                    }
                    else if (env.TiaVersionDetected == null)
                    {
                        nextTool = "(install TIA Portal)";
                    }
                    else
                    {
                        nextTool = "(repair TIA Openness environment)";
                    }
                    reason = Runtime.OpennessReadiness.Guidance(Runtime.EnvironmentDoctor.PreferChinese);
                }
                else if (env.OpennessGroupOk != true)
                {
                    nextTool = "EnsureOpennessUserGroup";
                    reason = "Current user is not in 'Siemens TIA Openness' Windows group; add the user to this local group, sign out and back in, then restart the MCP client.";
                }
                else if (env.TiaVersionInUse == null && env.TiaVersionDetected == null)
                {
                    nextTool = "(install TIA Portal)";
                    reason = "No TIA Portal installation detected. Install V18+ and set TiaPortalLocation env var.";
                }
                else if (portalDto.Connected != true)
                {
                    nextTool = "ConnectPortal";
                    reason = "Not connected to TIA Portal yet. ConnectPortal first; if a project is already open in TIA UI, then call AttachOpenProject.";
                }
                else if (string.IsNullOrWhiteSpace(portalDto.ProjectName) || portalDto.ProjectName == "-")
                {
                    nextTool = "AttachOpenProject";
                    reason = "Connected to portal but no project bound. Use AttachOpenProject if a project is already open in TIA UI, or OpenProject/CreateProject otherwise.";
                }
                else
                {
                    nextTool = "GetProjectTree";
                    reason = "Project is open. Inspect the tree before any write operation.";
                }

                var layers = new BootstrapToolLayers
                {
                    L0 = new[] { "InitializeEnvironment", "GetSessionState", "RunCapabilitySelfTest" },
                    L1 = new[]
                    {
                        "ConnectPortal", "DisconnectPortal", "AttachOpenProject", "OpenProject", "CreateProject",
                        "SaveProject", "CloseProject", "GetProjectTree", "GetSoftwareTree",
                        "BuildAndImportPlcArtifact", "CompilePlcSoftware", "DownloadPlc", "ConnectOnlinePlc", "DisconnectOnlinePlc"
                    },
                    L2Count = McpServer.GetMcpToolNames().Count(),
                };

                var rules = new[] { TiaOpenness.Shared.ToolUsageCatalog.Instructions,
                    IsLiteProfile() ? "FindTools searches all tools in this release; CallTool invokes a discovered tool." : "All tools in this release are listed." };
                var limits = new[] { "Availability depends on the selected release, target object and installed options; read the selected tool's contract and example evidence." };

                bool ready = Runtime.OpennessReadiness.Ready && env.OpennessGroupOk == true
                    && (env.TiaVersionInUse != null || env.TiaVersionDetected != null);

                return new ResponseBootstrap
                {
                    Ready = ready,
                    Environment = env,
                    Portal = portalDto,
                    RecommendedNextTool = nextTool,
                    RecommendedReason = reason,
                    OperatingRules = rules,
                    KnownLimitations = limits,
                    ToolLayers = layers,
                    SkillFile = "plugin/skill/SKILL.md",
                    ServerVersion = typeof(McpServer).Assembly.GetName().Version?.ToString(),
                    Capabilities = Capability.Snapshot(),
                    Message = ready ? "TIA Portal MCP ready" : "TIA Portal MCP not ready — see RecommendedNextTool",
                    Meta = ResponseMeta.Basic(DateTime.Now, true)
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"InitializeEnvironment unexpected error: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "ConnectProject"), Description("[L0][Portal][SESSION] Attach only to one running TIA process using processId, processStartUtc and full projectPath from ListPortalProcessProjects. Rejects stale identity, a competing MCP lease, and an unclean prior owner. Captures exact project identity; never starts TIA, saves, closes another project, or automatically retries. MCP-owned open projects must be explicitly closed/disconnected first. Native behaviorPolicy=current; V4 native acceptance is pending.")]
        public CallToolResult ConnectProjectV4(
            [Description("processId: exact running TIA PID from ListPortalProcessProjects.")] int processId,
            [Description("processStartUtc: exact ISO UTC start timestamp from the same process listing; prevents PID reuse.")] string processStartUtc,
            [Description("projectPath: absolute project file path from that process listing; another path is refused.")] string projectPath)
            => SessionToolContract.Run("ConnectProject", true, true, () => ConnectToProject(processId, processStartUtc, projectPath));

        public ResponseMessage ConnectToProject(
            [Description("processId: exact running TIA PID from ListPortalProcessProjects.")] int processId,
            [Description("processStartUtc: exact ISO UTC start timestamp from the same process listing; prevents PID reuse.")] string processStartUtc,
            [Description("projectPath: absolute project file path from that process listing; another path is refused.")] string projectPath)
        {
            _session.ConnectToProject(processId, processStartUtc, projectPath);
            return new ResponseMessage { Message = "Exact TIA project binding established.", Meta = ResponseMeta.Unstamped(true, ("binding", _session.GetBindingIdentity())) };
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
            => _session.ReadPortalInfo(includeProcesses,includeSessions,includeProducts);
    }
}
