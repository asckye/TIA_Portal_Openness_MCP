using System.ComponentModel;
using ModelContextProtocol.Server;
namespace TiaMcpServer.ModelContextProtocol
{
    public static partial class McpServer
    {
        [McpServerTool(Name="ScanAccessibleDevices"), Description("[L2][PLC-Online][ONLINE] Live network scan of reachable devices (name, IP, MAC, device series) on one exact PG/PC interface via ConfigurationPcInterface.GetAccessibleDevices. pgPcInterface is the exact name or number (required when several exist); softwarePath optionally scans through an existing PLC's connection configuration. Read-only for the project; probes the network. Feeds UploadStationFromPlc.")]
        public static ResponseMessage ScanAccessibleDevices(
            [Description("pgPcInterface: exact PG/PC interface name or number (required when several exist), e.g. 'PLCSIM'.")] string pgPcInterface="",
            [Description("softwarePath: optional PLC software path - scans through that PLC's connection configuration instead of the project-level provider.")] string softwarePath="",
            [Description("offset: first device to return (paging).")] int offset=0,
            [Description("limit: maximum devices to return.")] int limit=100)
            => Portal.ScanAccessibleDevices(pgPcInterface,softwarePath,offset,limit);
        [McpServerTool(Name="UploadStationFromPlc"), Description("[L2][PLC-Online][ONLINE-WRITE] Upload a station from a live PLC into the project as a NEW device (StationUploadProvider.StationUpload). targetIpAddress must exactly match an IP address seen by ScanAccessibleDevices on the same PG/PC interface (ConfigurationAddressComposition.Create accepts IP addresses only - a device listed by MAC alone, such as a PLCSIM Advanced instance before its first download, cannot be uploaded from). Default preview; execution needs confirmUpload=true, takes exclusive access, answers upload prompts (missing products: TryUpload; password prompts from password) and verifies the uploaded device exists. No save/compile/download.")]
        public static ResponseMessage UploadStationFromPlc(
            [Description("targetIpAddress: IP address of the PLC exactly as ScanAccessibleDevices lists it on the same interface (IP only, no MAC).")] string targetIpAddress,
            [Description("pgPcInterface: PG/PC interface name substring, e.g. 'PLCSIM' or 'Realtek' ('' = the first interface).")] string pgPcInterface="",
            [Description("password: CPU access password when the CPU is protected; never logged.")] string password="",
            [Description("promptAnswersJson: JSON object of explicit answers to upload prompts by prompt type name.")] string promptAnswersJson="{}",
            [Description("confirmUpload: must be true together with dryRun=false to upload (takes exclusive access, creates a new device).")] bool confirmUpload=false,
            [Description("dryRun: true (default) previews the route and the target; false uploads.")] bool dryRun=true)
            => Portal.UploadStationFromPlc(targetIpAddress,pgPcInterface,password,promptAnswersJson,confirmUpload,dryRun);
        [McpServerTool(Name="UploadDeviceParameters"), Description("[L2][PLC-Online][ONLINE-WRITE] Upload hardware parameters from a live device into the exact offline device/item (ParameterUploadProvider.ParameterUpload). Route is selected by exact pgPcInterface/targetIpAddress. Default preview; execution needs confirmUpload=true and exclusive access; returns before/after scalar properties. No save/compile/download.")]
        public static ResponseMessage UploadDeviceParameters(string devicePathJson, string itemPathJson, string targetIpAddress, string pgPcInterface="", string password="", string promptAnswersJson="{}", bool confirmUpload=false, bool dryRun=true)
            => Portal.UploadDeviceParameters(devicePathJson,itemPathJson,targetIpAddress,pgPcInterface,password,promptAnswersJson,confirmUpload,dryRun);
        [McpServerTool(Name="DownloadPlcToFolder"), Description("[L2][PLC-Online][FILE] Write the PLC download as a memory-card image into a new or empty absolute directory (DownloadProvider.Download(DirectoryInfo)); targetForSoftware CPU or PlcSimulationAdvanced. No PLC is contacted. Prompts follow DownloadToPlc rules (keepActualValues, userManagementMode, promptAnswersJson); overwriteOnMemoryCard=false answers NoAction. Default preview; execution needs confirmDownload=true. Reports files/bytes written and unanswered prompts.")]
        public static ResponseMessage DownloadPlcToFolder(string softwarePath, string destinationDirectory, string targetForSoftware="CPU", bool overwriteOnMemoryCard=false, bool keepActualValues=true, string userManagementMode="keep", string promptAnswersJson="{}", bool confirmDownload=false, bool dryRun=true)
            => Portal.DownloadPlcToFolder(softwarePath,destinationDirectory,targetForSoftware,overwriteOnMemoryCard,keepActualValues,userManagementMode,promptAnswersJson,confirmDownload,dryRun);
    }
}
