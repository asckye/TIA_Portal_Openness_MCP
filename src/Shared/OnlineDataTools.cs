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

        [McpServerTool(Name = "GetPlcBlockFingerprints"), Description("[L2][PLC-Online][ONLINE] Read fingerprint data (identifier/value pairs) from the CPU via native FingerprintDataProvider.GetFingerprintData using the configured route whose CPU address exactly equals targetIpAddress; several PG/PC adapters to that address require the exact pgPcInterface name. Optional CPU password is passed only to the native legitimation callback, and only over a TLS-secured connection where V21 reports IsSecureCommunication unless allowNonTlsLogin=true. FW >= 2.9 CPUs ask for certificate trust: trustDeviceCertificate=false (default) leaves an untrusted certificate untrusted (TIA refuses the read), true trusts it for this call. Paginated offset/limit<=500. Default preview resolves the route without contacting the PLC; dryRun=false contacts the PLC read-only. No project or PLC change. Current native policy; V4 safety behavior is not yet accepted.")]
        public CallToolResult ReadPlcBlockFingerprintsV4(
            string softwarePath,
            [Description("targetIpAddress: IP address of the PLC as ScanAccessibleDevices lists it.")] string targetIpAddress,
            string pgPcInterface="",
            string password="",
            int offset=0,
            int limit=100,
            bool dryRun=true,
            [Description("trustDeviceCertificate: false (default) leaves an untrusted certificate of FW >= 2.9 CPUs untrusted and TIA refuses the connection; true answers the TLS certificate prompt with Trusted for this call. Meta.tlsVerification reports the decision and, on V21, the certificates the device presented.")] bool trustDeviceCertificate=false,
            [Description("allowNonTlsLogin: false (default) sends the password only where the prompt reports IsSecureCommunication=true or the release cannot report it (OnlinePasswordConfiguration reports it on V21); true also sends it over a connection that is not TLS-secured.")] bool allowNonTlsLogin=false)
            => PlcToolContract.Run("GetPlcBlockFingerprints", false, true, () => ReadPlcBlockFingerprints(softwarePath, targetIpAddress, pgPcInterface, password, offset, limit, dryRun, trustDeviceCertificate, allowNonTlsLogin));

        public ResponseMessage ReadPlcBlockFingerprints(
            string softwarePath,
            string targetIpAddress,
            string pgPcInterface="",
            string password="",
            int offset=0,
            int limit=100,
            bool dryRun=true,
            bool trustDeviceCertificate=false,
            bool allowNonTlsLogin=false)
            => _onlineDownload.ReadPlcBlockFingerprints(softwarePath,targetIpAddress,pgPcInterface,password,offset,limit,dryRun,trustDeviceCertificate,allowNonTlsLogin);

}
