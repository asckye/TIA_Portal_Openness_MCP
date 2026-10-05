using System;
using ModelContextProtocol.Protocol;
using TiaMcp.Logic.V4;
using TiaMcp.Logic.V4.Inputs;
using System.ComponentModel;
using ModelContextProtocol.Server;
using TiaMcpServer.Siemens.Services;
namespace TiaMcpServer.ModelContextProtocol
{
    [McpServerToolType]
    internal sealed class SafetyManagementTools
    {
        private readonly SafetyManagementService _service;

        public SafetyManagementTools(SafetyManagementService service) => _service = service;

        [McpServerTool(Name="ManagePlcSafety"), Description("[L2][Safety][WRITE] Official Siemens.Engineering.Safety administration of one exact F-PLC. read: password/login state, SafetySettings incl. AssignmentOfBlockNumbers (ManagementMode, From/To FB/FC/DB), SafetySystemVersion + applicable versions, EnableConsistentUploadFromFCpu/EnableFCommunicationIdTag attributes, runtime groups incl. FOBNumber/FOBCycleTime/FOBPhaseShift/FOBPriority, collective/software/hardware/communication-address F-signatures (V21), portal GlobalSettings and CPU Failsafe_FCapabilityActivated. Writes: createRuntimeGroup (optional mainSafetyBlockPath FC, or FB + mainSafetyInstanceDbPath), deleteRuntimeGroup, updateRuntimeGroup/updateSettings (properties: scalar properties, AssignmentOfBlockNumbers.*, SafetySystemVersion exact string, documented attributes), generateGlobalFIOStatusBlock, cleanSystemGeneratedObjects, generateBaseId (V21, F-BaseID CPUs), login/logoff/setPassword/revokePassword (password passed straight to TIA, never stored or logged). dryRun=true default; program edits require Offline and F-login when protected; delete/generate/clean/password actions and SafetyModeCanBeDisabled require confirmSafetyChange=true. No F-compile, save or download; API access is not functional safety acceptance.")]
        public CallToolResult ManagePlcSafety(
            string softwarePath,
            [Description("read | createRuntimeGroup | deleteRuntimeGroup | updateRuntimeGroup | updateSettings | generateGlobalFIOStatusBlock | cleanSystemGeneratedObjects | generateBaseId | login | logoff | setPassword | revokePassword. ")] string action="read",
            [Description("runtimeGroup: exact name of the F-runtime group.")] string runtimeGroup="",
            AttributeMap<Scalar> properties=null!,
            bool dryRun=true,
            string password="",
            [Description("confirmSafetyChange: must be true together with dryRun=false to change safety settings.")] bool confirmSafetyChange=false,
            [Description("mainSafetyBlockPath: path of the main safety block ('Group/Name').")] string mainSafetyBlockPath="",
            [Description("mainSafetyInstanceDbPath: path of the main safety instance DB.")] string mainSafetyInstanceDbPath="")
            => SecurityToolContract.Invoke("ManagePlcSafety", dryRun || action == "read", true,
                () => _service.ManagePlcSafety(softwarePath,action,runtimeGroup,SecurityToolContract.Map(properties),dryRun,password,confirmSafetyChange,mainSafetyBlockPath,mainSafetyInstanceDbPath), password);
        [McpServerTool(Name="ManageSafetyGlobalSettings"), Description("[L2][Safety][WRITE] Official Safety GlobalSettings service of the connected TIA Portal (portal-wide, no project needed): read/update SafetyModificationsPossible, GenerationOfDefaultFailsafeProgram, ManagementOfFailsafeInSoftwareUnitsEnvironment, UsernameForFChangeHistory (<=256 chars; empty resets). properties holds only those four names. dryRun=true default; update reads every value back. SafetyModificationsPossible=false locks all F-program changes in TIA regardless of login.")]
        public CallToolResult ManageSafetyGlobalSettings([Description("read | update. ")] string action="read",AttributeMap<Scalar> properties=null!,bool dryRun=true)
            => SecurityToolContract.Invoke("ManageSafetyGlobalSettings", dryRun || action == "read", false,
                () => _service.ManageSafetyGlobalSettings(action,SecurityToolContract.Map(properties),dryRun));
        [McpServerTool(Name="GetSafetyBlockSignatures"), Description("[L2][Safety][READ] Per-block F-signatures via native PlcBlock.GetService<SafetySignatureProvider>() for one exact blockPath or every block of the PLC (recursive), plus the PLC collective/software/hardware/communication-address signatures (V21 ProgramSignatures) when includeProgramSignatures=true. Blocks without the service (non-F blocks, non-F-CPU) are counted, not listed. Value 0 = no valid signature (changed since the last F-compile). Offline project values, never read from the CPU. Paginated offset/limit<=500. No change.")]
        public CallToolResult GetSafetyBlockSignatures(
            string softwarePath,
            string blockPath="",
            [Description("includeProgramSignatures: true also returns the F-program signatures (V21).")] bool includeProgramSignatures=true,
            int offset=0,
            int limit=100)
            => SecurityToolContract.Invoke("GetSafetyBlockSignatures", true, false,
                () => _service.ReadSafetyBlockSignatures(softwarePath,blockPath,includeProgramSignatures,offset,limit));
        [McpServerTool(Name="ExportSafetyPrintout"), Description("[L2][Safety][FILE] Official STEP 7 Safety printout of one exact F-PLC via native SafetyPrintout.Print on the CPU device item: printer MicrosoftPrintToPdf (.pdf) or MicrosoftXpsDocumentWriter (.xps/.oxps), option All or Compact, documentLayout default DocuInfo_ISO_A4_Portrait. filePath is absolute on the MCP server, must not exist (native Print would overwrite) and its parent must exist; the Windows printer driver must be enabled on the TIA machine. dryRun=true default; execution returns the native bool, byte size and SHA-256. No project change.")]
        public CallToolResult ExportSafetyPrintout(
            string softwarePath,
            string filePath,
            [Description("printer: printer name ('' = the default printer).")] string printer="MicrosoftPrintToPdf",
            [Description("option: Openness print / export option name ('' = default).")] string option="All",
            [Description("documentLayout: document layout name ('' = default).")] string documentLayout="",
            bool dryRun=true)
            => SecurityToolContract.Invoke("ExportSafetyPrintout", dryRun, true,
                () => _service.ExportSafetyPrintout(softwarePath,filePath,printer,option,documentLayout,dryRun));
    }
}
