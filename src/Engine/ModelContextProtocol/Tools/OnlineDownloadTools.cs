using TiaMcpServer.Siemens.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using ModelContextProtocol;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW;
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
            "[L1][Category:PLC-Online][PreCondition:Connect+OpenProject]" +
            " Read the current online connection state of a PLC (Offline/Connecting/Online/Incompatible/NotReachable/Protected/Disconnecting)." +
            " Does NOT change state — purely a read operation." +
            " Use before GoOnline to check current state, or after DownloadToPlc to verify the CPU is reachable." +
            " State=Online means the PC is communicating with the physical CPU." +
            " State=Incompatible means online but firmware/config mismatch — download required." +
            " State=NotReachable means network or IP configuration issue." +
            " NOTE: This reports Openness connection state, NOT the CPU operating mode (RUN/STOP)." +
            " The TIA Portal public API does not expose CPU operating mode — check the CPU front panel LEDs or HMI for RUN/STOP status.")]
        public ResponseOnlineState GetOnlineState(
            [Description("softwarePath: path to the PLC software, e.g. 'PLC_1'")] string softwarePath)
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

        [McpServerTool(Name = "GoOnline"), Description(
            "[L1][Category:PLC-Online][ONLINE][PreCondition:Connect+OpenProject]" +
            " Establish an online connection from TIA Portal to the physical PLC." +
            " Required before DownloadToPlc to confirm reachability, or for future online monitoring tools." +
            " Returns State=Online on success." +
            " If ipAddress is omitted, uses the IP address configured in the project's hardware configuration." +
            " If ipAddress is provided, the matching ConfigurationAddress of the route tree (target interface, subnet or gateway; created on the target interface when TIA has not seen it yet) is applied and GoOnline(ConfigurationAddress) is used - this is also how a PLCSIM Advanced instance is reached (pgPcInterface 'PLCSIM Virtual Ethernet Adapter' / 'PLCSIM')." +
            " Common failures: NotReachable (wrong IP / no cable), Protected (CPU requires authentication — supply password), Incompatible (firmware mismatch)." +
            " S7-1500 FW >= 2.9 CPUs (incl. PLCSIM Advanced) ask for certificate trust on the first contact (TlsVerificationConfiguration); trustDeviceCertificate=true (default) answers Trusted - the same prompt TIA shows in the UI - and Meta.tlsVerification records PlcName / VerificationInfo / the selection; with false the connection is refused by TIA ('The device is not trusted').")]
        public ResponseOnlineState GoOnline(
            [Description("softwarePath: path to the PLC software, e.g. 'PLC_1'")] string softwarePath,
            [Description("ipAddress: optional IP address override, e.g. '192.168.1.10'. Leave empty to use the project's configured IP.")] string ipAddress = "",
            [Description("password: optional CPU access password. Required when the CPU has read/write protection configured. Leave empty for unprotected CPUs.")] string password = "",
            [Description("userName: optional user for UMAC-protected PLCs (answers OnlineAuthenticationConfiguration with password); leave empty for legacy password-only protection.")] string userName = "",
            [Description("userType: optional OnlineCredentials.Type (None/AnonymousUser/GlobalUser/ProjectUser/SingleSignOnUser/PasswordOnly); default ProjectUser when userName is given.")] string userType = "",
            [Description("rhTarget: empty for standard CPUs; primary or backup goes online to that CPU of an R/H system through RHOnlineProvider.")] string rhTarget = "",
            [Description("pgPcInterface: optional PG/PC adapter name substring (as listed by ReadTransferRoutes / ScanAccessibleDevices, e.g. 'PLCSIM Virtual Ethernet Adapter'); with ipAddress the route is applied before going online (ConnectionConfiguration.ApplyConfiguration).")] string pgPcInterface = "",
            [Description("trustDeviceCertificate: true (default) answers the TLS certificate prompt of FW >= 2.9 CPUs with Trusted for this call; false leaves it unanswered and TIA refuses the connection.")] bool trustDeviceCertificate = true)
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
                    trustDeviceCertificate);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error going online for '{softwarePath}': {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "GoOffline"), Description(
            "[L1][Category:PLC-Online][PreCondition:Connect+OpenProject]" +
            " Disconnect the online session between TIA Portal and the physical PLC." +
            " Safe to call even if not currently online. Always go offline when monitoring or download is complete.")]
        public ResponseMessage GoOffline(
            [Description("softwarePath: path to the PLC software, e.g. 'PLC_1'")] string softwarePath)
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

        [McpServerTool(Name = "GoOfflineAll"), Description(
            "[L1][Category:PLC-Online][PreCondition:Connect+OpenProject]" +
            " Take EVERY PLC in the open project offline in one call and report each PLC's before/after online state." +
            " Use this whenever CompilePlcSoftware/Export*/Import* is blocked by 'operation not permitted in online mode':" +
            " a UI-initiated online session or a second online PLC is NOT released by GoOffline on a single softwarePath." +
            " Fully autonomous — never ask the user to toggle online/offline in the TIA UI, and never OCR the toolbar.")]
        public ResponseJsonReport GoOfflineAll()
        {
            try
            {
                var data = _onlineDownload.GoOfflineAll();
                bool all = data["allOffline"]?.GetValue<bool>() ?? false;
                return new ResponseJsonReport
                {
                    Ok = all,
                    Message = data["message"]?.ToString() ?? "GoOfflineAll completed.",
                    Data = data,
                    Meta = ResponseMeta.Basic(DateTime.Now, all)
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"GoOfflineAll failed: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }


        [McpServerTool(Name = "CompareSoftwareToOnline"), Description(
            "[L2][Category:PLC-Online][PreCondition:Connect+OpenProject+GoOnline]" +
            " Compare the offline PLC software in the project against the program currently running on the physical CPU." +
            " Use after editing blocks to confirm what differs from the live CPU before downloading," +
            " or after a download to verify offline/online consistency." +
            " Returns a tree-walked list of differences (only entries where ComparisonResult is not 'Equal' are reported)." +
            " Requires GoOnline to be called first; will return IsOnline=false with guidance otherwise.")]
        public ResponseCompare CompareSoftwareToOnline(
            [Description("softwarePath: path to the PLC software, e.g. 'PLC_1'")] string softwarePath,
            [Description("maxDepth: maximum tree depth to walk (default 4). Lower = faster but less detail.")] int maxDepth = 4,
            [Description("maxEntries: cap on differences returned (default 200). Truncated=true in response if reached.")] int maxEntries = 200)
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
            "[L1][Category:PLC-Online][PreCondition:Connect+OpenProject+CompilePlcSoftware]" +
            " Check whether a PLC is ready to receive a program download WITHOUT actually downloading." +
            " Verifies: DownloadProvider service is available, a network/IP configuration exists in the hardware config." +
            " Returns Ready=true only when all checks pass." +
            " Use this before DownloadToPlc to surface problems early (missing IP, no hardware config, etc.)." +
            " Meta.downloadRoutes lists every PG/PC interface -> CPU route (best-ranked first, preferred=true when the" +
            " adapter shares a subnet with the CPU) — check it on a multi-NIC PC (WLAN/VPN/PLCSIM) before downloading." +
            " Does NOT compile — run CompilePlcSoftware first to ensure blocks are consistent.")]
        public ResponseCheckDownload CheckDownloadReadiness(
            [Description("softwarePath: path to the PLC software in the project tree, e.g. 'PLC_1'")] string softwarePath)
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

        [McpServerTool(Name = "DownloadToPlc"), Description(
            "[L1][Category:PLC-Online][ONLINE-WRITE][PreCondition:Connect+OpenProject+CompilePlcSoftware+CheckDownloadReadiness]" +
            " Download the compiled PLC program to the physical CPU over the network." +
            " The CPU will stop briefly during download and restart automatically (controlled by startAfterDownload)." +
            " SAFETY: Verify no personnel are near the machine before downloading. This changes live PLC behavior." +
            " Workflow: Connect → OpenProject → CompilePlcSoftware → CheckDownloadReadiness → DownloadToPlc → GetOnlineState." +
            " On success State=Success or Warning. On Error check Errors[] for details." +
            " Default options (keepActualValues=true, consistentBlocksOnly=true) are safe for most scenarios." +
            " Set keepActualValues=false only when DB initial values must be reset — this is irreversible." +
            " On a multi-NIC PC the PG/PC interface is picked automatically (the adapter sharing a subnet with the CPU);" +
            " Meta.pgPcRoute reports which one was used. Override with pgPcInterface / targetIpAddress when the pick is wrong." +
            " S7-1500 FW >= 2.9 CPUs (incl. PLCSIM Advanced) ask for certificate trust on the first contact; trustDeviceCertificate=true (default) answers Trusted and Meta.tlsVerification records it, false makes TIA refuse the connection.")]
        public ResponseDownload DownloadToPlc(
            [Description("softwarePath: path to the PLC software, e.g. 'PLC_1'")] string softwarePath,
            [Description("consistentBlocksOnly: true=download only consistent blocks (safe default), false=download all blocks even inconsistent ones")] bool consistentBlocksOnly = true,
            [Description("keepActualValues: true=preserve current DB actual values (safe default), false=reset all DB values to initial values (irreversible)")] bool keepActualValues = true,
            [Description("startAfterDownload: true=automatically set CPU to RUN after download (default), false=leave CPU in STOP")] bool startAfterDownload = true,
            [Description("stopBeforeDownload: true=automatically stop CPU before download (required for most downloads), false=attempt online download without stopping")] bool stopBeforeDownload = true,
            [Description("password: optional CPU access password. Required when the CPU has download protection configured. Leave empty for unprotected CPUs.")] string password = "",
            [Description("pgPcInterface: optional PG/PC interface name (substring, case-insensitive), e.g. 'PLCSIM' or 'Realtek'. Leave empty to auto-pick the adapter that shares a subnet with the CPU. Run CheckDownloadReadiness to see the available names.")] string pgPcInterface = "",
            [Description("targetIpAddress: optional CPU IP to download to, e.g. '192.168.0.1'. Disambiguates which route to use when the project has several CPU interfaces. Leave empty to auto-pick.")] string targetIpAddress = "",
            [Description("userManagementMode: how the UserManagementDownload prompt is answered: keep (default, keeps the online user management data), updateKeepPassword (updates data but keeps online passwords), resetToProject (downloads all user management data and resets to project).")] string userManagementMode = "keep",
            [Description("promptAnswersJson: optional JSON object of explicit answers for download prompts by type name, e.g. {\"ResetModule\":\"DeleteAll\",\"OverwriteHmiData\":true}. Selection prompts take an enum name, checkbox prompts take true/false. Without an entry, destructive prompts (InitializeMemory, OverwriteOnMemoryCard, OverwriteSystemData, ResetModule, SwitchBackupToPrimary, ProtectionLevelChanged) default to NoAction/NoChange and prompts without a known default stay unanswered; Meta.promptsAnswered / Meta.promptsUnanswered list what happened.")] string promptAnswersJson = "{}",
            [Description("moduleAccessPassword: optional password for ModuleReadAccessPassword / ModuleWriteAccessPassword prompts; defaults to 'password' when empty. Never logged.")] string moduleAccessPassword = "",
            [Description("blockBindingPassword: optional password for the BlockBindingPassword prompt (know-how protected blocks bound to a CPU/card). Never logged.")] string blockBindingPassword = "",
            [Description("masterSecretPassword: optional password for the PlcMasterSecretPassword prompt. Never logged.")] string masterSecretPassword = "",
            [Description("rhTarget: empty for standard CPUs; primary or backup downloads to that CPU of an R/H system through RHDownloadProvider.DownloadToPrimary/DownloadToBackup.")] string rhTarget = "",
            [Description("trustDeviceCertificate: true (default) answers the TLS certificate prompt of FW >= 2.9 CPUs with Trusted for this call; false leaves it unanswered and TIA refuses the connection.")] bool trustDeviceCertificate = true)
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
                    trustDeviceCertificate);

                if (result.Ok == false && result.Errors != null && result.Errors.Length > 0)
                    throw new McpException(
                        $"Download to '{softwarePath}' failed: {result.Message}",
                        McpErrorCode.InternalError);

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


        [McpServerTool(Name="ReadTransferRoutes"), Description("[L2][PLC-Online][READ] The PG/PC route tree of the exact PLC's DownloadProvider.Configuration: Modes (ConfigurationMode.Name) -> PcInterfaces (Name, Number, Addresses, Subnets with Addresses and Gateways, TargetInterfaces with Addresses), plus availability of RHDownloadProvider / RHOnlineProvider (R/H systems, with PrimaryState/BackupState), OnlineProvider (State) and CompileProvider. Nothing is applied or changed; use it to pick pgPcInterface/targetIpAddress for DownloadToPlc / GoOnline.")]
        public ResponseMessage ReadTransferRoutes(
            [Description("softwarePath: PLC software path from GetProjectTree, e.g. 'PLC_1'.")] string softwarePath,
            [Description("maxItems: cap on route-tree rows returned.")] int maxItems=500)
            => _onlineDownload.ReadTransferRoutes(softwarePath,maxItems);


        [McpServerTool(Name="ScanAccessibleDevices"), Description("[L2][PLC-Online][ONLINE] Live network scan of reachable devices (name, IP, MAC, device series) on one exact PG/PC interface via ConfigurationPcInterface.GetAccessibleDevices. pgPcInterface is the exact name or number (required when several exist); softwarePath optionally scans through an existing PLC's connection configuration. Read-only for the project; probes the network. Feeds UploadStationFromPlc.")]
        public ResponseMessage ScanAccessibleDevices(
            [Description("pgPcInterface: exact PG/PC interface name or number (required when several exist), e.g. 'PLCSIM'.")] string pgPcInterface="",
            [Description("softwarePath: optional PLC software path - scans through that PLC's connection configuration instead of the project-level provider.")] string softwarePath="",
            [Description("offset: first device to return (paging).")] int offset=0,
            [Description("limit: maximum devices to return.")] int limit=100)
            => _onlineDownload.ScanAccessibleDevices(pgPcInterface,softwarePath,offset,limit);
        [McpServerTool(Name="UploadStationFromPlc"), Description("[L2][PLC-Online][ONLINE-WRITE] Upload a station from a live PLC into the project as a NEW device (StationUploadProvider.StationUpload). targetIpAddress must exactly match an IP address seen by ScanAccessibleDevices on the same PG/PC interface (ConfigurationAddressComposition.Create accepts IP addresses only - a device listed by MAC alone, such as a PLCSIM Advanced instance before its first download, cannot be uploaded from). Default preview; execution needs confirmUpload=true, takes exclusive access, answers upload prompts (missing products: TryUpload; password prompts from password) and verifies the uploaded device exists. No save/compile/download.")]
        public ResponseMessage UploadStationFromPlc(
            [Description("targetIpAddress: IP address of the PLC exactly as ScanAccessibleDevices lists it on the same interface (IP only, no MAC).")] string targetIpAddress,
            [Description("pgPcInterface: PG/PC interface name substring, e.g. 'PLCSIM' or 'Realtek' ('' = the first interface).")] string pgPcInterface="",
            [Description("password: CPU access password when the CPU is protected; never logged.")] string password="",
            [Description("promptAnswersJson: JSON object of explicit answers to upload prompts by prompt type name.")] string promptAnswersJson="{}",
            [Description("confirmUpload: must be true together with dryRun=false to upload (takes exclusive access, creates a new device).")] bool confirmUpload=false,
            [Description("dryRun: true (default) previews the route and the target; false uploads.")] bool dryRun=true)
            => _onlineDownload.UploadStationFromPlc(targetIpAddress,pgPcInterface,password,promptAnswersJson,confirmUpload,dryRun);
        [McpServerTool(Name="UploadDeviceParameters"), Description("[L2][PLC-Online][ONLINE-WRITE] Upload hardware parameters from a live device into the exact offline device/item (ParameterUploadProvider.ParameterUpload). Route is selected by exact pgPcInterface/targetIpAddress. Default preview; execution needs confirmUpload=true and exclusive access; returns before/after scalar properties. No save/compile/download.")]
        public ResponseMessage UploadDeviceParameters(
            string devicePathJson,
            string itemPathJson,
            [Description("targetIpAddress: IP address of the PLC as ScanAccessibleDevices lists it.")] string targetIpAddress,
            string pgPcInterface="",
            string password="",
            string promptAnswersJson="{}",
            bool confirmUpload=false,
            bool dryRun=true)
            => _onlineDownload.UploadDeviceParameters(devicePathJson,itemPathJson,targetIpAddress,pgPcInterface,password,promptAnswersJson,confirmUpload,dryRun);
        [McpServerTool(Name="DownloadPlcToFolder"), Description("[L2][PLC-Online][FILE] Write the PLC download as a memory-card image into a new or empty absolute directory (DownloadProvider.Download(DirectoryInfo)); targetForSoftware CPU or PlcSimulationAdvanced. No PLC is contacted. Prompts follow DownloadToPlc rules (keepActualValues, userManagementMode, promptAnswersJson); overwriteOnMemoryCard=false answers NoAction. Default preview; execution needs confirmDownload=true. Reports files/bytes written and unanswered prompts.")]
        public ResponseMessage DownloadPlcToFolder(
            string softwarePath,
            string destinationDirectory,
            [Description("targetForSoftware: folder on the TIA machine that receives the download files.")] string targetForSoftware="CPU",
            [Description("overwriteOnMemoryCard: true answers the OverwriteOnMemoryCard prompt with overwrite.")] bool overwriteOnMemoryCard=false,
            [Description("keepActualValues: true keeps the current data block values (safe default), false resets them to start values.")] bool keepActualValues=true,
            [Description("userManagementMode: how the UserManagementDownload prompt is answered (keep, updateKeepPassword, update).")] string userManagementMode="keep",
            string promptAnswersJson="{}",
            [Description("confirmDownload: must be true together with dryRun=false to write the download files.")] bool confirmDownload=false,
            bool dryRun=true)
            => _onlineDownload.DownloadPlcToFolder(softwarePath,destinationDirectory,targetForSoftware,overwriteOnMemoryCard,keepActualValues,userManagementMode,promptAnswersJson,confirmDownload,dryRun);
    }
}
