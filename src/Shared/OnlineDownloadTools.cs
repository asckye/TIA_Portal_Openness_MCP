using TiaMcp.Logic.V4;
using TiaMcp.Logic.V4.Inputs;
using TiaMcpServer.Siemens.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using ModelContextProtocol;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml.Linq;
using System;
using TiaMcpServer.Siemens;

namespace TiaMcpServer.ModelContextProtocol
{
    [McpServerToolType]
    internal sealed class OnlineDownloadTools
    {
        private readonly OnlineDownloadService _onlineDownload;

        public OnlineDownloadTools(OnlineDownloadService onlineDownload) => _onlineDownload = onlineDownload;

        [McpServerTool(Name = "GetOnlineState"), Description(
            "[L1][Category:PLC-Online][PreCondition:ConnectPortal+OpenProject]" +
            " Read the current online connection state of a PLC (Offline/Connecting/Online/Incompatible/NotReachable/Protected/Disconnecting)." +
            " Does NOT change state — purely a read operation." +
            " Use before ConnectOnlinePlc to check current state, or after DownloadPlc to verify the CPU is reachable." +
            " State=Online means the PC is communicating with the physical CPU." +
            " State=Incompatible means online but firmware/config mismatch — download required." +
            " State=NotReachable means network or IP configuration issue." +
            " NOTE: This reports Openness connection state, NOT the CPU operating mode (RUN/STOP)." +
            " The TIA Portal public API does not expose CPU operating mode — check the CPU front panel LEDs or HMI for RUN/STOP status. Native behaviorPolicy=current; V4 native acceptance is pending.")]
        public CallToolResult GetOnlineStateV4(
            [Description("softwarePath: path to the PLC software, e.g. 'PLC_1'")] string softwarePath)
            => RuntimeToolContract.Run("GetOnlineState", true, true, () =>
            {
                return GetOnlineState(softwarePath);
            });

        public ResponseOnlineState GetOnlineState(string softwarePath)
        {
            try
            {
                return _onlineDownload.GetOnlineState(softwarePath);
            }
            catch (PortalException pex)
            {
                // 路径解析不到是调用方的参数问题，不是服务器内部意外错误。
                throw new McpException(pex.Message, pex,
                    pex.Code == PortalErrorCode.NotFound ? McpErrorCode.InvalidParams : McpErrorCode.InternalError);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error reading online state for '{softwarePath}': {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "ConnectOnlinePlc"), Description(
            "[L1][Category:PLC-Online][ONLINE][PreCondition:ConnectPortal+OpenProject]" +
            " Establish an online connection from TIA Portal to the physical PLC." +
            " Not needed before DownloadPlc: DownloadPlc connects through its own route and refuses while the PLC is online (call DisconnectOnlinePlc first). Use it before CompareSoftwareToOnline or online monitoring." +
            " Returns State=Online on success; GoOnline returns before the connection is established, so the state is polled while it reads Connecting (up to 10 s)." +
            " An already online PLC is reported as Online without applying a route (ApplyConfiguration throws while the device is online)." +
            " If ipAddress and pgPcInterface are omitted, the connection must already be configured in this session (ConnectionConfiguration.IsConfigured); otherwise the call is not attempted." +
            " If ipAddress is provided, the matching ConfigurationAddress of the route tree (target interface, subnet or gateway; created on the target interface when TIA has not seen it yet) is applied and OnlineProvider.GoOnline(ConfigurationAddress) is used - this is also how a PLCSIM Advanced instance is reached (pgPcInterface 'PLCSIM Virtual Ethernet Adapter' / 'PLCSIM')." +
            " Common failures: NotReachable (wrong IP / no cable), Protected (CPU requires authentication — supply password), Incompatible (firmware mismatch)." +
            " An explicitly requested route that ConnectionConfiguration.ApplyConfiguration does not apply is refused with its reason." +
            " S7-1500 FW >= 2.9 CPUs (incl. PLCSIM Advanced) ask for certificate trust on the first contact (TlsVerificationConfiguration); trustDeviceCertificate=false (default) leaves an untrusted certificate untrusted and TIA refuses the connection ('The device is not trusted'); trustDeviceCertificate=true answers Trusted for this call - the same prompt TIA shows in the UI. Meta.tlsVerification records PlcName / VerificationInfo / the selection and, on V21, the certificates the device presented." +
            " A password is sent only over a TLS-secured connection where the prompt reports IsSecureCommunication, unless allowNonTlsLogin=true. Native behaviorPolicy=current; V4 native acceptance is pending.")]
        public CallToolResult GoOnlineV4(
            [Description("softwarePath: path to the PLC software, e.g. 'PLC_1'")] string softwarePath,
            [Description("ipAddress: optional IP address override, e.g. '192.168.1.10'. Leave empty to use the project's configured IP.")] string ipAddress = "",
            [Description("password: optional CPU access password. Required when the CPU has read/write protection configured. Leave empty for unprotected CPUs.")] string password = "",
            [Description("userName: optional user for UMAC-protected PLCs (answers OnlineAuthenticationConfiguration with password); leave empty for legacy password-only protection.")] string userName = "",
            [Description("userType: optional OnlineCredentials.Type (None/AnonymousUser/GlobalUser/ProjectUser/SingleSignOnUser/PasswordOnly); default ProjectUser when userName is given.")] string userType = "",
            [Description("rhTarget: empty for standard CPUs; primary or backup goes online to that CPU of an R/H system through RHOnlineProvider.")] string rhTarget = "",
            [Description("pgPcInterface: optional PG/PC adapter name substring (as listed by ListTransferRoutes / ScanAccessibleDevices, e.g. 'PLCSIM Virtual Ethernet Adapter'); with ipAddress the route is applied before going online (ConnectionConfiguration.ApplyConfiguration).")] string pgPcInterface = "",
            [Description("trustDeviceCertificate: false (default) leaves an untrusted certificate of FW >= 2.9 CPUs untrusted and TIA refuses the connection; true answers the TLS certificate prompt with Trusted for this call. Meta.tlsVerification reports the decision and, on V21, the certificates the device presented.")] bool trustDeviceCertificate = false,
            [Description("allowNonTlsLogin: false (default) sends a password only where the prompt reports IsSecureCommunication=true or the release cannot report it (the download and upload password prompts from V17, OnlineAuthenticationConfiguration from V19, OnlinePasswordConfiguration on V21); true also sends it over a connection that is not TLS-secured.")] bool allowNonTlsLogin = false)
            => RuntimeToolContract.Run("ConnectOnlinePlc", false, true, () =>
            {
                return GoOnline(softwarePath, ipAddress, password, userName, userType, rhTarget, pgPcInterface, trustDeviceCertificate, allowNonTlsLogin);
            });

        public ResponseOnlineState GoOnline(string softwarePath, string ipAddress = "", string password = "", string userName = "", string userType = "", string rhTarget = "", string pgPcInterface = "", bool trustDeviceCertificate = false, bool allowNonTlsLogin = false)
        {
            try
            {
                return _onlineDownload.GoOnline(
                    softwarePath,
                    string.IsNullOrWhiteSpace(ipAddress) ? null : ipAddress,
                    string.IsNullOrWhiteSpace(password) ? null : password,
                    string.IsNullOrWhiteSpace(userName) ? null : userName,
                    string.IsNullOrWhiteSpace(userType) ? null : userType,
                    rhTarget ?? "",
                    string.IsNullOrWhiteSpace(pgPcInterface) ? null : pgPcInterface,
                    trustDeviceCertificate,
                    allowNonTlsLogin);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error going online for '{softwarePath}': {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "DisconnectOnlinePlc"), Description(
            "[L1][Category:PLC-Online][PreCondition:ConnectPortal+OpenProject]" +
            " Disconnect the online session between TIA Portal and the physical PLC." +
            " Safe to call even if not currently online; works on an online PLC (GoOffline needs no route). The state is read back after GoOffline and success is reported only when it reads Offline." +
            " Always go offline when monitoring is complete. Native behaviorPolicy=current; V4 native acceptance is pending.")]
        public CallToolResult GoOfflineV4(
            [Description("softwarePath: path to the PLC software, e.g. 'PLC_1'")] string softwarePath)
            => RuntimeToolContract.Run("DisconnectOnlinePlc", false, true, () =>
            {
                return GoOffline(softwarePath);
            });

        public ResponseMessage GoOffline(string softwarePath)
        {
            try
            {
                return _onlineDownload.GoOffline(softwarePath);
            }
            catch (PortalException pex)
            {
                throw new McpException(pex.Message, pex,
                    pex.Code == PortalErrorCode.NotFound ? McpErrorCode.InvalidParams : McpErrorCode.InternalError);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error going offline for '{softwarePath}': {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "DisconnectOnlinePlcs"), Description(
            "[L1][Category:PLC-Online][PreCondition:ConnectPortal+OpenProject]" +
            " Take EVERY PLC in the open project offline in one call and report each PLC's before/after online state." +
            " Use this whenever CompilePlcSoftware/Export*/Import* is blocked by 'operation not permitted in online mode':" +
            " a UI-initiated online session or a second online PLC is NOT released by DisconnectOnlinePlc on a single softwarePath." +
            " Fully autonomous — never ask the user to toggle online/offline in the TIA UI, and never OCR the toolbar. Native behaviorPolicy=current; V4 native acceptance is pending.")]
        public CallToolResult GoOfflineAllV4()
            => RuntimeToolContract.Run("DisconnectOnlinePlcs", false, true, () =>
            {
                return GoOfflineAll();
            });

        public ResponseJsonReport GoOfflineAll()
        {
            try
            {
                var data = _onlineDownload.GoOfflineAll();
                bool all = data["allOffline"]?.GetValue<bool>() ?? false;
                return new ResponseJsonReport
                {
                    Ok = all,
                    Message = data["message"]?.ToString() ?? "DisconnectOnlinePlcs completed.",
                    Data = data,
                    Meta = ResponseMeta.Basic(DateTime.Now, all)
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"DisconnectOnlinePlcs failed: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }


        [McpServerTool(Name = "CompareSoftwareToOnline"), Description(
            "[L2][Category:PLC-Online][PreCondition:ConnectPortal+OpenProject+ConnectOnlinePlc]" +
            " Compare the offline PLC software in the project against the program currently running on the physical CPU." +
            " Use after editing blocks to confirm what differs from the live CPU before downloading," +
            " or after a download to verify offline/online consistency." +
            " Returns a tree-walked list of differences: only difference states (ObjectsDifferent, LeftMissing, RightMissing, FolderContentsDifferent and the folder own-state differences) are reported; ObjectsIdentical, FolderContentsIdentical and CompareIrrelevant are not." +
            " Requires ConnectOnlinePlc to be called first; will return IsOnline=false with guidance otherwise. Native behaviorPolicy=current; V4 native acceptance is pending.")]
        public CallToolResult CompareSoftwareToOnlineV4(
            [Description("softwarePath: path to the PLC software, e.g. 'PLC_1'")] string softwarePath,
            [Description("maxDepth: maximum tree depth to walk (default 4). Lower = faster but less detail.")] int maxDepth = 4,
            [Description("maxEntries: cap on differences returned (default 200). Truncated=true in response if reached.")] int maxEntries = 200)
            => RuntimeToolContract.Run("CompareSoftwareToOnline", true, true, () =>
            {
                return CompareSoftwareToOnline(softwarePath, maxDepth, maxEntries);
            });

        public ResponseCompare CompareSoftwareToOnline(string softwarePath, int maxDepth = 4, int maxEntries = 200)
        {
            try
            {
                return _onlineDownload.CompareSoftwareToOnline(softwarePath, maxDepth, maxEntries);
            }
            catch (PortalException pex)
            {
                throw new McpException(pex.Message, pex,
                    pex.Code == PortalErrorCode.NotFound ? McpErrorCode.InvalidParams : McpErrorCode.InternalError);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error comparing '{softwarePath}' to online: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "CheckDownloadReadiness"), Description(
            "[L1][Category:PLC-Online][PreCondition:ConnectPortal+OpenProject+CompilePlcSoftware]" +
            " Check whether a PLC is ready to receive a program download WITHOUT actually downloading." +
            " Verifies: DownloadProvider service is available, a network/IP configuration exists in the hardware config." +
            " Returns Ready=true only when all checks pass." +
            " Use this before DownloadPlc to surface problems early (missing IP, no hardware config, etc.)." +
            " Meta.downloadRoutes lists every PG/PC interface -> CPU route (best-ranked first, preferred=true when the" +
            " adapter shares a subnet with the CPU) — check it on a multi-NIC PC (WLAN/VPN/PLCSIM) before downloading." +
            " Does NOT compile — run CompilePlcSoftware first to ensure blocks are consistent. Native behaviorPolicy=current; V4 native acceptance is pending.")]
        public CallToolResult CheckDownloadReadinessV4(
            [Description("softwarePath: path to the PLC software in the project tree, e.g. 'PLC_1'")] string softwarePath)
            => RuntimeToolContract.Run("CheckDownloadReadiness", true, true, () =>
            {
                return CheckDownloadReadiness(softwarePath);
            });

        public ResponseCheckDownload CheckDownloadReadiness(string softwarePath)
        {
            try
            {
                return _onlineDownload.CheckDownloadReadiness(softwarePath);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error checking download readiness for '{softwarePath}': {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "DownloadPlc"), Description(
            "[L1][Category:PLC-Online][ONLINE-WRITE][PreCondition:ConnectPortal+OpenProject+CompilePlcSoftware+CheckDownloadReadiness]" +
            " Download the compiled PLC program to the physical CPU over the network." +
            " The CPU will stop briefly during download and restart automatically (controlled by startAfterDownload)." +
            " SAFETY: Verify no personnel are near the machine before downloading. This changes live PLC behavior." +
            " Workflow: ConnectPortal → OpenProject → CompilePlcSoftware → CheckDownloadReadiness → DownloadPlc → GetOnlineState." +
            " The PLC must be offline (call DisconnectOnlinePlc first): the route is applied with ConnectionConfiguration.ApplyConfiguration, which throws while the device is online, and the download connects by itself." +
            " On success State=Success or Warning. On Error check Errors[] for details." +
            " Prompt answers: StopModules / StartModules follow stopBeforeDownload / startAfterDownload, DataBlockReinitialization follows keepActualValues, AllBlocksDownload follows consistentBlocksOnly, UserManagementDownload follows userManagementMode; destructive prompts answer NoAction; promptAnswers names others." +
            " Refused unless the request allows them: SafetyProgram (allowSafetyProgramDownload), DifferentTargetConfiguration (allowHardwareDifferences), ActiveTestCanBeAborted / ActiveTestCanPreventDownload (allowActiveTestCancel), ExpandDownload (allowExpandedDownload) and LoadIdentificationData (allowIdentificationDataLoad); the download stops at such a prompt and the result names it and the parameter that allows it." +
            " The approval request lists every prompt answer the call will give (promptAnswerPlan)." +
            " On a multi-NIC PC the PG/PC interface is picked automatically (the adapter sharing a subnet with the CPU);" +
            " Meta.pgPcRoute reports which one was used. Override with pgPcInterface / targetIpAddress when the pick is wrong; an explicit route that ApplyConfiguration does not apply is refused, only an automatic pick continues unconfirmed (Meta.routeApplied=false)." +
            " S7-1500 FW >= 2.9 CPUs (incl. PLCSIM Advanced) ask for certificate trust on the first contact; trustDeviceCertificate=false (default) makes TIA refuse an untrusted certificate, true answers Trusted; Meta.tlsVerification records the decision and, on V21, the presented certificates." +
            " A password is sent only over a TLS-secured connection where the prompt reports IsSecureCommunication, unless allowNonTlsLogin=true. Native behaviorPolicy=current; V4 native acceptance is pending.")]
        public CallToolResult DownloadToPlcV4(
            [Description("softwarePath: path to the PLC software, e.g. 'PLC_1'")] string softwarePath,
            [Description("consistentBlocksOnly: true (default) leaves the AllBlocksDownload prompt unanswered, so TIA aborts instead of downloading all blocks; false answers it with its only selection, download all blocks. ConsistentBlocksDownload is always answered ConsistentDownload; neither value downloads inconsistent blocks - compile first.")] bool consistentBlocksOnly = true,
            [Description("keepActualValues: true (default) keeps the DB actual values (DataBlockReinitialization: NoAction, so TIA aborts a download that needs reinitialization; V21 DataBlockReinitializationOrKeepActualValues: KeepActualValues); false resets all data values to their start values (StopPlcAndReinitialize) only with stopBeforeDownload=true, otherwise both prompts answer NoAction and TIA aborts - irreversible.")] bool keepActualValues = true,
            [Description("startAfterDownload: true=automatically set CPU to RUN after download (default), false=leave CPU in STOP")] bool startAfterDownload = true,
            [Description("stopBeforeDownload: true=automatically stop CPU before download (required for most downloads), false=attempt online download without stopping")] bool stopBeforeDownload = true,
            [Description("password: optional CPU access password. Required when the CPU has download protection configured. Leave empty for unprotected CPUs.")] string password = "",
            [Description("pgPcInterface: optional PG/PC interface name (substring, case-insensitive), e.g. 'PLCSIM' or 'Realtek'. Leave empty to auto-pick the adapter that shares a subnet with the CPU. Run CheckDownloadReadiness to see the available names.")] string pgPcInterface = "",
            [Description("targetIpAddress: optional CPU IP to download to, e.g. '192.168.0.1'. Disambiguates which route to use when the project has several CPU interfaces. Leave empty to auto-pick.")] string targetIpAddress = "",
            [Description("userManagementMode: how the UserManagementDownload prompt is answered: keep (default, keeps the online user management data), updateKeepPassword (updates data but keeps online passwords), resetToProject (downloads all user management data and resets to project).")] string userManagementMode = "keep",
            [Description("promptAnswers: structured AttributeMap<string> input.")] AttributeMap<string> promptAnswers = null!,
            [Description("moduleAccessPassword: optional password for ModuleReadAccessPassword / ModuleWriteAccessPassword prompts; defaults to 'password' when empty. Never logged.")] string moduleAccessPassword = "",
            [Description("blockBindingPassword: optional password for the BlockBindingPassword prompt (know-how protected blocks bound to a CPU/card). Never logged.")] string blockBindingPassword = "",
            [Description("masterSecretPassword: optional password for the PlcMasterSecretPassword prompt. Never logged.")] string masterSecretPassword = "",
            [Description("rhTarget: empty for standard CPUs; primary or backup downloads to that CPU of an R/H system through RHDownloadProvider.DownloadToPrimary/DownloadToBackup (routes and prompts of RHDownloadProvider.Configuration).")] string rhTarget = "",
            [Description("trustDeviceCertificate: false (default) leaves an untrusted certificate of FW >= 2.9 CPUs untrusted and TIA refuses the connection; true answers the TLS certificate prompt with Trusted for this call. Meta.tlsVerification reports the decision and, on V21, the certificates the device presented.")] bool trustDeviceCertificate = false,
            [Description("allowSafetyProgramDownload: true answers the SafetyProgram prompt (F-program download) with ConsistentDownload; false (default) refuses it and the download stops there.")] bool allowSafetyProgramDownload = false,
            [Description("allowHardwareDifferences: true answers DifferentTargetConfiguration (differences between configured and target modules) with AcceptAll; false (default) refuses it and the download stops there.")] bool allowHardwareDifferences = false,
            [Description("allowActiveTestCancel: true answers ActiveTestCanBeAborted / ActiveTestCanPreventDownload (active test and commissioning functions are cancelled) with AcceptAll; false (default) refuses them and the download stops there.")] bool allowActiveTestCancel = false,
            [Description("allowExpandedDownload: true answers ExpandDownload (the download must be expanded beyond the selection) with Download; false (default) refuses it and the download stops there.")] bool allowExpandedDownload = false,
            [Description("allowIdentificationDataLoad: true answers LoadIdentificationData (write I&M identification data to the PROFINET IO devices) with LoadData; false (default) refuses it and the download stops there.")] bool allowIdentificationDataLoad = false,
            [Description("allowNonTlsLogin: false (default) sends a password only where the prompt reports IsSecureCommunication=true or the release cannot report it (the download and upload password prompts from V17, OnlineAuthenticationConfiguration from V19, OnlinePasswordConfiguration on V21); true also sends it over a connection that is not TLS-secured.")] bool allowNonTlsLogin = false,
            [Description("downloadScope: software (default; the Openness download option Software, download software all) or softwareOnlyChanges (the option SoftwareOnlyChanges, download only the changes; V17+).")] string downloadScope = "software")
            => RuntimeToolContract.Run("DownloadPlc", false, true, () =>
            {
                return DownloadToPlc(softwarePath, consistentBlocksOnly, keepActualValues, startAfterDownload, stopBeforeDownload, password, pgPcInterface, targetIpAddress, userManagementMode, RuntimeToolContract.Answers(promptAnswers), moduleAccessPassword, blockBindingPassword, masterSecretPassword, rhTarget, trustDeviceCertificate,
                    new DownloadAllowances { SafetyProgramDownload = allowSafetyProgramDownload, HardwareDifferences = allowHardwareDifferences, ActiveTestCancel = allowActiveTestCancel,
                        ExpandedDownload = allowExpandedDownload, IdentificationDataLoad = allowIdentificationDataLoad, NonTlsLogin = allowNonTlsLogin }, downloadScope);
            });

        public ResponseDownload DownloadToPlc(string softwarePath, bool consistentBlocksOnly = true, bool keepActualValues = true, bool startAfterDownload = true, bool stopBeforeDownload = true, string password = "", string pgPcInterface = "", string targetIpAddress = "", string userManagementMode = "keep", string promptAnswersJson = "{}", string moduleAccessPassword = "", string blockBindingPassword = "", string masterSecretPassword = "", string rhTarget = "", bool trustDeviceCertificate = false, DownloadAllowances? allowances = null, string downloadScope = "software")
        {
            try
            {
                var result = _onlineDownload.DownloadToPlc(
                    softwarePath,
                    consistentBlocksOnly,
                    keepActualValues,
                    startAfterDownload,
                    stopBeforeDownload,
                    string.IsNullOrWhiteSpace(password) ? null : password,
                    string.IsNullOrWhiteSpace(pgPcInterface) ? null : pgPcInterface,
                    string.IsNullOrWhiteSpace(targetIpAddress) ? null : targetIpAddress,
                    string.IsNullOrWhiteSpace(userManagementMode) ? "keep" : userManagementMode,
                    string.IsNullOrWhiteSpace(promptAnswersJson) ? "{}" : promptAnswersJson,
                    string.IsNullOrWhiteSpace(moduleAccessPassword) ? null : moduleAccessPassword,
                    string.IsNullOrWhiteSpace(blockBindingPassword) ? null : blockBindingPassword,
                    string.IsNullOrWhiteSpace(masterSecretPassword) ? null : masterSecretPassword,
                    rhTarget ?? "",
                    trustDeviceCertificate,
                    allowances,
                    string.IsNullOrWhiteSpace(downloadScope) ? "software" : downloadScope);

                return result;
            }
            catch (McpException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new McpException($"Unexpected error downloading to '{softwarePath}': {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }


        [McpServerTool(Name="ListTransferRoutes"), Description("[L2][PLC-Online][READ] The PG/PC route tree of the exact PLC's DownloadProvider.Configuration: Modes (ConfigurationMode.Name) -> PcInterfaces (Name, Number, Addresses, Subnets with Addresses and Gateways, TargetInterfaces with Addresses), plus availability of RHDownloadProvider / RHOnlineProvider (R/H systems, with PrimaryState/BackupState), OnlineProvider (State) and CompileProvider. Nothing is applied or changed; use it to pick pgPcInterface/targetIpAddress for DownloadPlc / ConnectOnlinePlc. Native behaviorPolicy=current; V4 native acceptance is pending.")]
        public CallToolResult ReadTransferRoutesV4(
            [Description("softwarePath: PLC software path from GetProjectTree, e.g. 'PLC_1'.")] string softwarePath,
            [Description("maxItems: cap on route-tree rows returned.")] int maxItems = 500)
            => RuntimeToolContract.Run("ListTransferRoutes", true, true, () =>
            {
                return ReadTransferRoutes(softwarePath, maxItems);
            });

        public ResponseMessage ReadTransferRoutes(string softwarePath, int maxItems = 500)
            => _onlineDownload.ReadTransferRoutes(softwarePath,maxItems);


        [McpServerTool(Name="ScanAccessibleDevices"), Description("[L2][PLC-Online][ONLINE] Live network scan of reachable devices (name, IP, MAC, device series) on one exact PG/PC interface via ConfigurationPcInterface.GetAccessibleDevices. pgPcInterface is the exact name or number (required when several exist); softwarePath optionally scans through an existing PLC's connection configuration. Read-only for the project; probes the network. Feeds UploadStationFromPlc. Native behaviorPolicy=current; V4 native acceptance is pending.")]
        public CallToolResult ScanAccessibleDevicesV4(
            [Description("pgPcInterface: exact PG/PC interface name or number (required when several exist), e.g. 'PLCSIM'.")] string pgPcInterface = "",
            [Description("softwarePath: optional PLC software path - scans through that PLC's connection configuration instead of the project-level provider.")] string softwarePath = "",
            [Description("offset: first device to return (paging).")] int offset = 0,
            [Description("limit: maximum devices to return.")] int limit = 100)
            => RuntimeToolContract.Run("ScanAccessibleDevices", true, true, () =>
            {
                return ScanAccessibleDevices(pgPcInterface, softwarePath, offset, limit);
            });

        public ResponseMessage ScanAccessibleDevices(string pgPcInterface = "", string softwarePath = "", int offset = 0, int limit = 100)
            => _onlineDownload.ScanAccessibleDevices(pgPcInterface,softwarePath,offset,limit);
        [McpServerTool(Name="UploadStationFromPlc"), Description("[L2][PLC-Online][ONLINE-WRITE] Upload a station from a live PLC into the project as a NEW device (StationUploadProvider.StationUpload). targetIpAddress must exactly match an IP address in the route tree of the selected PG/PC interface (target interfaces, subnets or gateways) or, on V19+, one seen by ScanAccessibleDevices on that interface; V15.1-V18 have no accessible-device scan, so only addresses already in the route tree work. ConfigurationAddressComposition.Create accepts IP addresses only - a device listed by MAC alone, such as a PLCSIM Advanced instance before its first download, cannot be uploaded from. The default preview only reads the route tree (no network traffic, no change); for an address not in it, execution after approval runs a DCP network scan (GetAccessibleDevices) and creates the route address (ConfigurationAddressComposition.Create) - both approval-gated writes. Execution needs confirmUpload=true, takes exclusive access, answers upload prompts (missing products: TryUpload; password prompts from password) and verifies the uploaded device exists. No save/compile/download. Native behaviorPolicy=current; V4 native acceptance is pending.")]
        public CallToolResult UploadStationFromPlcV4(
            [Description("targetIpAddress: IP address of the PLC exactly as ScanAccessibleDevices lists it on the same interface (IP only, no MAC).")] string targetIpAddress,
            [Description("pgPcInterface: exact PG/PC interface name or number, e.g. 'PLCSIM'; required when several interfaces exist ('' only when there is one).")] string pgPcInterface = "",
            [Description("password: CPU access password when the CPU is protected; never logged.")] string password = "",
            [Description("promptAnswers: structured AttributeMap<string> input.")] AttributeMap<string> promptAnswers = null!,
            [Description("confirmUpload: must be true together with dryRun=false to upload (takes exclusive access, creates a new device).")] bool confirmUpload = false,
            [Description("dryRun: true (default) previews the route and the target from the route tree only; false uploads.")] bool dryRun = true,
            [Description("trustDeviceCertificate: false (default) leaves an untrusted certificate of FW >= 2.9 CPUs untrusted and TIA refuses the connection; true answers the TLS certificate prompt with Trusted for this call. Meta.tlsVerification reports the decision and, on V21, the certificates the device presented.")] bool trustDeviceCertificate = false,
            [Description("allowNonTlsLogin: false (default) sends a password only where the prompt reports IsSecureCommunication=true or the release cannot report it (the download and upload password prompts from V17, OnlineAuthenticationConfiguration from V19, OnlinePasswordConfiguration on V21); true also sends it over a connection that is not TLS-secured.")] bool allowNonTlsLogin = false)
            => RuntimeToolContract.Run("UploadStationFromPlc", dryRun, true, () =>
            {
                RuntimeToolContract.Confirm(dryRun, confirmUpload);
                return UploadStationFromPlc(targetIpAddress, pgPcInterface, password, RuntimeToolContract.Answers(promptAnswers), confirmUpload, dryRun, trustDeviceCertificate, allowNonTlsLogin);
            });

        public ResponseMessage UploadStationFromPlc(string targetIpAddress, string pgPcInterface = "", string password = "", string promptAnswersJson = "{}", bool confirmUpload = false, bool dryRun = true, bool trustDeviceCertificate = false, bool allowNonTlsLogin = false)
            => _onlineDownload.UploadStationFromPlc(targetIpAddress,pgPcInterface,password,promptAnswersJson,confirmUpload,dryRun,trustDeviceCertificate,allowNonTlsLogin);
        [McpServerTool(Name="UploadDeviceParameters"), Description("[L2][PLC-Online][ONLINE-WRITE] Upload drive parameters from a live SINAMICS StartDrive device into the exact offline head module (ParameterUploadProvider.ParameterUpload; the provider exists only on StartDrive head modules, DeviceItemClassifications.HM - CPUs and other items are refused). Route is selected by pgPcInterface/targetIpAddress; targetIpAddress '' passes a null ConfigurationAddress (the official pattern). The default preview selects the route without applying it (no ApplyConfiguration, no address creation); execution after approval applies it, and an explicit route that is not applied is refused. Execution needs confirmUpload=true and exclusive access; returns before/after scalar properties. No save/compile/download. Native behaviorPolicy=current; V4 native acceptance is pending.")]
        public CallToolResult UploadDeviceParametersV4(
            [Description("devicePath: structured string array input.")] string[] devicePath,
            [Description("itemPath: structured string array input.")] string[] itemPath,
            [Description("targetIpAddress: IP address of the drive as ScanAccessibleDevices lists it; '' uses no explicit address (null ConfigurationAddress).")] string targetIpAddress,
            string pgPcInterface = "",
            string password = "",
            [Description("promptAnswers: structured AttributeMap<string> input.")] AttributeMap<string> promptAnswers = null!,
            bool confirmUpload = false,
            bool dryRun = true,
            [Description("trustDeviceCertificate: false (default) leaves an untrusted certificate of FW >= 2.9 CPUs untrusted and TIA refuses the connection; true answers the TLS certificate prompt with Trusted for this call. Meta.tlsVerification reports the decision and, on V21, the certificates the device presented.")] bool trustDeviceCertificate = false,
            [Description("allowNonTlsLogin: false (default) sends a password only where the prompt reports IsSecureCommunication=true or the release cannot report it (the download and upload password prompts from V17, OnlineAuthenticationConfiguration from V19, OnlinePasswordConfiguration on V21); true also sends it over a connection that is not TLS-secured.")] bool allowNonTlsLogin = false)
            => RuntimeToolContract.Run("UploadDeviceParameters", dryRun, true, () =>
            {
                RuntimeToolContract.Confirm(dryRun, confirmUpload);
                return UploadDeviceParameters(RuntimeToolContract.Path(devicePath, "devicePath", false), RuntimeToolContract.Path(itemPath, "itemPath", true), targetIpAddress, pgPcInterface, password, RuntimeToolContract.Answers(promptAnswers), confirmUpload, dryRun, trustDeviceCertificate, allowNonTlsLogin);
            });

        public ResponseMessage UploadDeviceParameters(string devicePathJson, string itemPathJson, string targetIpAddress, string pgPcInterface = "", string password = "", string promptAnswersJson = "{}", bool confirmUpload = false, bool dryRun = true, bool trustDeviceCertificate = false, bool allowNonTlsLogin = false)
            => _onlineDownload.UploadDeviceParameters(devicePathJson,itemPathJson,targetIpAddress,pgPcInterface,password,promptAnswersJson,confirmUpload,dryRun,trustDeviceCertificate,allowNonTlsLogin);
        [McpServerTool(Name="DownloadPlcToFolder"), Description("[L2][PLC-Online][FILE] Write the PLC download as a memory-card image into a new or empty absolute directory (DownloadProvider.Download(DirectoryInfo)); targetForSoftware CPU or PlcSimulationAdvanced. No PLC is contacted. Prompts follow DownloadPlc rules (keepActualValues, userManagementMode, promptAnswers); overwriteOnMemoryCard=false answers NoAction; SafetyProgram and ExpandDownload are refused unless allowSafetyProgramDownload / allowExpandedDownload is true. Default preview; execution needs confirmDownload=true. Reports files/bytes written and unanswered or refused prompts. Native behaviorPolicy=current; V4 native acceptance is pending.")]
        public CallToolResult DownloadPlcToFolderV4(
            string softwarePath,
            string destinationDirectory,
            [Description("targetForSoftware: the TargetForSoftware prompt answer (TargetForSoftwareSelections, V19+): CPU (default, a real CPU's memory card) or PlcSimulationAdvanced. The folder itself is destinationDirectory.")] string targetForSoftware = "CPU",
            [Description("overwriteOnMemoryCard: true answers the OverwriteOnMemoryCard prompt with overwrite.")] bool overwriteOnMemoryCard = false,
            [Description("keepActualValues: true (default) keeps the data block values. Reinitialization needs a CPU STOP, which a folder download never requests, so false answers a reinitialization prompt with NoAction and the folder download stops there.")] bool keepActualValues = true,
            [Description("userManagementMode: how the UserManagementDownload prompt is answered: keep (default), updateKeepPassword or resetToProject.")] string userManagementMode = "keep",
            [Description("promptAnswers: structured AttributeMap<string> input.")] AttributeMap<string> promptAnswers = null!,
            [Description("confirmDownload: must be true together with dryRun=false to write the download files.")] bool confirmDownload = false,
            bool dryRun = true,
            [Description("allowSafetyProgramDownload: true answers the SafetyProgram prompt (F-program) with ConsistentDownload; false (default) refuses it and the folder download stops there.")] bool allowSafetyProgramDownload = false,
            [Description("allowExpandedDownload: true answers ExpandDownload (the download must be expanded beyond the selection) with Download; false (default) refuses it and the folder download stops there.")] bool allowExpandedDownload = false)
            => RuntimeToolContract.Run("DownloadPlcToFolder", dryRun, true, () =>
            {
                RuntimeToolContract.Confirm(dryRun, confirmDownload);
                return DownloadPlcToFolder(softwarePath, destinationDirectory, targetForSoftware, overwriteOnMemoryCard, keepActualValues, userManagementMode, RuntimeToolContract.Answers(promptAnswers), confirmDownload, dryRun, allowSafetyProgramDownload, allowExpandedDownload);
            });

        public ResponseMessage DownloadPlcToFolder(string softwarePath, string destinationDirectory, string targetForSoftware = "CPU", bool overwriteOnMemoryCard = false, bool keepActualValues = true, string userManagementMode = "keep", string promptAnswersJson = "{}", bool confirmDownload = false, bool dryRun = true, bool allowSafetyProgramDownload = false, bool allowExpandedDownload = false)
            => _onlineDownload.DownloadPlcToFolder(softwarePath,destinationDirectory,targetForSoftware,overwriteOnMemoryCard,keepActualValues,userManagementMode,promptAnswersJson,confirmDownload,dryRun,allowSafetyProgramDownload,allowExpandedDownload);
    }
}

namespace TiaMcpServer.Siemens.Services
{
    // The DownloadPlc opt-ins: each prompt that widens a download, and a password over a connection that is not TLS-secured,
    // is answered only when the request names it.
    internal sealed class DownloadAllowances
    {
        internal bool SafetyProgramDownload { get; set; }
        internal bool HardwareDifferences { get; set; }
        internal bool ActiveTestCancel { get; set; }
        internal bool ExpandedDownload { get; set; }
        internal bool IdentificationDataLoad { get; set; }
        internal bool NonTlsLogin { get; set; }
    }
}
