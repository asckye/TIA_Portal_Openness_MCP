using System;
using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.Logic.V4;
using TiaMcp.Logic.V4.Inputs;
using TiaMcpServer.Siemens.Services;

namespace TiaMcpServer.ModelContextProtocol;

[McpServerToolType]
internal sealed class OnlineDataTools
{
    private readonly OnlineDownloadService _onlineDownload;
    public OnlineDataTools(OnlineDownloadService onlineDownload) => _onlineDownload = onlineDownload;
        [McpServerTool(Name = "ManagePlcDataBlockSnapshot"), Description("[L2][PLC-Online][ONLINE-WRITE] read/createSnapshot/loadSnapshotAsActualValues/loadStartValuesAsActualValues/exportSnapshot on one exact data block via native ValueService/InterfaceSnapshot. Siemens semantics: createSnapshot reads actual values from the CPU and load actions write values INTO the running CPU, so the PLC must already be online in TIA; this tool never goes online/offline. Load actions require confirmValueChange=true besides dryRun=false; no value readback exists, only native return. exportSnapshot writes a NEW absolute file and returns its SHA-256. ValueService is V21+ (V20 returns NotSupported). Default preview; no save/compile/download. Current native policy; V4 safety behavior is not yet accepted.")]
        public CallToolResult ManagePlcDataBlockSnapshotV4(
            string softwarePath,
            string blockPath,
            [Description("action: the operation to perform - read | createSnapshot | loadSnapshotAsActualValues | loadStartValuesAsActualValues | exportSnapshot.")] string action,
            string filePath="",
            [Description("confirmValueChange: must be true together with dryRun=false to change data block values.")] bool confirmValueChange=false,
            bool dryRun=true)
            => PlcToolContract.Run("ManagePlcDataBlockSnapshot", !dryRun && action != "read", true, () => ManagePlcDataBlockSnapshot(softwarePath, blockPath, action, filePath, confirmValueChange, dryRun));

        public ResponseMessage ManagePlcDataBlockSnapshot(
            string softwarePath,
            string blockPath,
            string action,
            string filePath="",
            bool confirmValueChange=false,
            bool dryRun=true)
            => _onlineDownload.ManagePlcDataBlockSnapshot(softwarePath,blockPath,action,filePath,confirmValueChange,dryRun);

        [McpServerTool(Name = "GetPlcBlockFingerprints"), Description("[L2][PLC-Online][ONLINE] Read fingerprint data (identifier/value pairs) from the CPU via native FingerprintDataProvider.GetFingerprintData using the configured route whose CPU address exactly equals targetIpAddress; several PG/PC adapters to that address require the exact pgPcInterface name. Optional CPU password is passed only to the native legitimation callback. Paginated offset/limit<=500. Default preview resolves the route without contacting the PLC; dryRun=false contacts the PLC read-only. No project or PLC change. Current native policy; V4 safety behavior is not yet accepted.")]
        public CallToolResult ReadPlcBlockFingerprintsV4(
            string softwarePath,
            [Description("targetIpAddress: IP address of the PLC as ScanAccessibleDevices lists it.")] string targetIpAddress,
            string pgPcInterface="",
            string password="",
            int offset=0,
            int limit=100,
            bool dryRun=true)
            => PlcToolContract.Run("GetPlcBlockFingerprints", false, true, () => ReadPlcBlockFingerprints(softwarePath, targetIpAddress, pgPcInterface, password, offset, limit, dryRun));

        public ResponseMessage ReadPlcBlockFingerprints(
            string softwarePath,
            string targetIpAddress,
            string pgPcInterface="",
            string password="",
            int offset=0,
            int limit=100,
            bool dryRun=true)
            => _onlineDownload.ReadPlcBlockFingerprints(softwarePath,targetIpAddress,pgPcInterface,password,offset,limit,dryRun);

}
