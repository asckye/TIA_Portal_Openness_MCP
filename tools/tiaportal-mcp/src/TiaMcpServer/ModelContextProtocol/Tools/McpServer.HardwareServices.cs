using System.ComponentModel;
using ModelContextProtocol.Server;
namespace TiaMcpServer.ModelContextProtocol
{
    public static partial class McpServer
    {
        [McpServerTool(Name="ManagePlcProtection"), Description("[L2][Hardware][WRITE] Access level and confidential-configuration-data password of ONE CPU (PlcAccessLevelProvider / PlcMasterSecretConfigurator on the CPU device item; itemPathJson [] resolves the station's CPU). read reports accessLevel (FullAccess / ReadAccess / HMIAccess / NoAccess / FullAccessIncludingFailsafe), masterSecret (None = 'Protect confidential PLC configuration data' unchecked, WithoutPassword = checked without password, WithPassword, WithPasswordAllDataProtection) and the access-control mode. setAccessLevel accessLevel; setAccessPassword / resetAccessPassword accessLevel [+ password] (TIA only accepts passwords for levels less strict than the selected one); protectMasterSecret password; changeMasterSecret password newPassword; unprotectMasterSecret [password]; resetMasterSecret (certificates encrypted with it are lost); protectAllConfiguration [password]; unprotectAllConfiguration. Why: TIA V21 refuses the hardware download of an S7-1500 FW >= 2.9 CPU whose level is above FullAccess without a FullAccess password or whose confidential data has no password ('硬件配置编译完成，但出现错误' - see CompileDevice). Passwords go in as SecureString and are never echoed; state read back (meta.before / after). Default preview; real change needs dryRun=false AND confirmChange=true; no compile / save / download.")]
        public static ResponseMessage ManagePlcProtection(
            [Description("devicePathJson: JSON array naming the station, e.g. [\"PLC_1\"] (or [group, ..., station]).")] string devicePathJson,
            [Description("itemPathJson: JSON array of device-item names down to the CPU; [] (default) resolves the station's CPU.")] string itemPathJson="[]",
            [Description("action: read | setAccessLevel | setAccessPassword | resetAccessPassword | protectMasterSecret | changeMasterSecret | unprotectMasterSecret | resetMasterSecret | protectAllConfiguration | unprotectAllConfiguration.")] string action="read",
            [Description("accessLevel: for setAccessLevel / setAccessPassword / resetAccessPassword - FullAccess | ReadAccess | HMIAccess | NoAccess | FullAccessIncludingFailsafe.")] string accessLevel="",
            [Description("password: the password to set (setAccessPassword, protectMasterSecret) or the current one (changeMasterSecret); never logged.")] string password="",
            [Description("newPassword: the new password for changeMasterSecret.")] string newPassword="",
            [Description("confirmChange: must be true together with dryRun=false for every action except read.")] bool confirmChange=false,
            [Description("dryRun: true (default) previews; false applies the change.")] bool dryRun=true)
            => Portal.ManagePlcProtection(devicePathJson,itemPathJson,action,accessLevel,password,newPassword,confirmChange,dryRun);
        [McpServerTool(Name="CompileDevice"), Description("[L2][Hardware][EXECUTE] Hardware compile of one device (or device item) through ICompilable - what the TIA UI's 'Compile > Hardware (rebuild all)' does and what DownloadToPlc runs first; CompileSoftware / CompileAndDiagnosePlc only compile the program. Returns the compiler state, error / warning counts and the flattened diagnostics (errors[] / warnings[] / nodes) - e.g. the security errors of an S7-1500 FW >= 2.9 CPU that ManagePlcProtection fixes. No save / download.")]
        public static ResponseMessage CompileDevice(
            [Description("devicePathJson: JSON array naming the station to compile, e.g. [\"PLC_1\"].")] string devicePathJson,
            [Description("itemPathJson: JSON array of device-item names when one item (e.g. the CPU) is to be compiled; [] = the whole station.")] string itemPathJson="[]")
            => Portal.CompileDevice(devicePathJson,itemPathJson);
    }
}
