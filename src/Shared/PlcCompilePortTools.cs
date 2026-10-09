using System;
using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcpServer.Siemens.Services;
namespace TiaMcpServer.ModelContextProtocol
{
    [McpServerToolType]
    internal sealed class PlcCompilePortTools
    {
        private readonly PlcOrganisationPortService _service;
        public PlcCompilePortTools(PlcOrganisationPortService service) => _service = service;
        [McpServerTool(Name="CompileDevice"), Description("[L2][Hardware][EXECUTE] Hardware compile of one device (or device item) through ICompilable - what the TIA UI's 'Compile > Hardware (rebuild all)' does and what DownloadPlc runs first; CompilePlcSoftware / CompilePlcDiagnostics only compile the program. Returns the compiler state, error / warning counts and the flattened diagnostics (errors[] / warnings[] / nodes) - e.g. the security errors of an S7-1500 FW >= 2.9 CPU that ManagePlcProtection fixes. No save / download. Native behaviorPolicy=current; V4 native acceptance is pending.")]
        public CallToolResult CompileDevice(
            [Description("devicePath: array naming the station to compile, e.g. [\"PLC_1\"].")] string[] devicePath,
            [Description("itemPath: array of device-item names when one item (e.g. the CPU) is to be compiled; [] = the whole station.")] string[] itemPath = null!)
            => HardwareToolContract.Invoke("CompileDevice", false, true, () =>
            {
                string devicePathJson = HardwareToolContract.Path(devicePath, "devicePath", rootAllowed: false, optional: false);
                string itemPathJson = HardwareToolContract.Path(itemPath, "itemPath", rootAllowed: true, optional: true);
                return _service.CompileDevice(devicePathJson,itemPathJson);
            });

        [McpServerTool(Name = "CompileHmiDiagnostics"), Description("[L1][HMI] Compile an HMI and return structured errors/warnings, the HMI counterpart of CompilePlcDiagnostics. Use it after generating screens/tags so you can read the diagnostics and fix them yourself instead of asking the engineer to compile in the TIA UI. WinCC Unified: HmiSoftware is not compilable on its own, so the owning device is compiled (same as the TIA UI does) and hardware diagnostics may appear alongside screen ones. Classic (Comfort/KTP): the HMI software itself is compiled. Requires: ConnectPortal + OpenProject. softwarePath from GetProjectTree, e.g. 'HMI_RT_1'. Native behaviorPolicy=current; V4 native acceptance is pending.")]
        public CallToolResult CompileAndDiagnoseHmiV4(
            [Description("softwarePath: HMI software path, e.g. 'HMI_RT_1'")] string softwarePath)
            => HmiInspectionContract.Run("CompileHmiDiagnostics", true, true, () => CompileAndDiagnoseHmi(softwarePath));

        public ResponseCompileDiagnose CompileAndDiagnoseHmi(
            [Description("softwarePath: HMI software path, e.g. 'HMI_RT_1'")] string softwarePath)
        => new PlcArtifactPortSession(_service, "CompileHmiDiagnostics").CompileAndDiagnose(softwarePath);
    }
}
