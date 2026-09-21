using System.ComponentModel;
using ModelContextProtocol.Server;
namespace TiaMcpServer.ModelContextProtocol
{
    public static partial class McpServer
    {
        [McpServerTool(Name="ManageSyslogServers"), Description("[L2][Security][WRITE] Syslog configuration. scope=project (Siemens.Engineering.Security.SyslogServerProvider.Servers, project-global servers): read (all or exact name: Name, Address, Port, Tls, Comment, AssignedModules with owner paths), create (PREVIEW ONLY: SyslogServerComposition.Create crashed TIA Portal V21 twice on the reference project, so dryRun=false is refused; create the server in the TIA UI), update (propertiesJson), delete (needs confirmDelete, verified absent), assignModule/unassignModule (DeviceItemAssociation.Add/Remove of the exact devicePathJson/itemPathJson module). scope=plc (HW.Features.SysLogConfigurationManager on the exact CPU item, S7-1500 FW 3.1+): read (EnableSystemLogging, TransportProtocol, SysLogServerConfiguration rows, dynamic SysLogAutoAcceptClient/SysLogClientCertificateId/SysLogTrustedCertificateIds), update (propertiesJson EnableSystemLogging/TransportProtocol None|TLSServerAndClientAuthentication|TLSOnlyServerAuthentication|UDP + attributesJson for the three dynamic attributes), createServer (serverAddress + serverPort via SysLogServerConfigurationComposition.Create), deleteServer (serverAddress, needs confirmDelete). Default dryRun=true; no save/compile/download.")]
        public static ResponseMessage ManageSyslogServers(
            [Description("scope: project | plc.")] string scope="project",
            string action="read",
            string name="",
            string propertiesJson="{}",
            string attributesJson="{}",
            string devicePathJson="[]",
            string itemPathJson="[]",
            [Description("serverAddress: syslog server address (host name or IP).")] string serverAddress="",
            [Description("serverPort: syslog server port.")] int serverPort=0,
            bool confirmDelete=false,
            bool dryRun=true)
            => Portal.ManageSyslogServers(scope,action,name,propertiesJson,attributesJson,devicePathJson,itemPathJson,serverAddress,serverPort,confirmDelete,dryRun);

        [McpServerTool(Name="ManagePasswordPolicy"), Description("[L2][Security][WRITE] Project password policies. read (all targets or one): umac = Umac.PasswordPolicyConfigurator (IncludesLowerCaseAndUpperCaseCharacters, MinimumLength, MinimumNumericCharacterLength, MinimumSpecialCharacterLength, EnablePasswordAging, MinimumUserPasswordsBlockedForReuse, PasswordValidity, PasswordValidityPrewarningTime), plc = Security.PlcPasswordPolicyService (PasswordPolicyEnabled: S7-1200/1500 passwords follow the UMAC complexity), legacyPlc = Security.LegacyPlcPasswordPolicyService (PasswordPolicyEnabled, MinimumLength 5..8, MinimumNumericCharacterLength 0..8, MinimumSpecialCharacterLength 0..8, IncludesLowerCaseAndUpperCaseCharacters). update (target + propertiesJson, every write read back; TIA raises PasswordPolicySettingsException outside its ranges) needs confirmChange with dryRun=false. Passwords are never readable; no save.")]
        public static ResponseMessage ManagePasswordPolicy(
            [Description("action: the operation to perform - read | update.")] string action="read",
            [Description("target: umac | plc | legacyPlc.")] string target="",
            string propertiesJson="{}",
            bool confirmChange=false,
            bool dryRun=true)
            => Portal.ManagePasswordPolicy(action,target,propertiesJson,confirmChange,dryRun);

        [McpServerTool(Name="ManageUmcUsers"), Description("[L2][Security][WRITE] UMC (central) users and groups of a protected project (UmacConfigurator.UmcUsers/UmcUserGroups) and the UMC server (UmcServerConfigurator). kind=user|group: read (all or exact name, paginated: Name, IsActive, DomainId, Description, roles), createOffline (UmcUserComposition.CreateOfflineUmcUser(name) / CreateOfflineUmcUserGroup()+SetName), importFromServer (UmcServer.GetUserByName/GetUserGroupByName then Create(info); needs serverUserName + serverPassword for the Authentication event, a UMC account with the UMC View right), rename (newName via SetName), activate/deactivate, delete (verified absent), assignRole/unassignRole (roleName = system or custom role, RoleAssociation.Add/Remove). kind=server: read (UmcServer scalars/attribute names), checkConsistency (UmcServerConfigurator.CheckConsistency), synchronize (Synchronize with the server; optional credentials). Real changes need confirmChange with dryRun=false; passwords go to the API as SecureString and are never echoed; no save. ManageProjectUserManagement handles project (local) users.")]
        public static ResponseMessage ManageUmcUsers(
            [Description("kind: user | group | server.")] string kind="user",
            [Description("action: the operation to perform - read | checkConsistency | synchronize | createOffline | importFromServer | rename | activate | deactivate | delete | assignRole | unassignRole.")] string action="read",
            string name="",
            string newName="",
            [Description("roleName: exact role name.")] string roleName="",
            [Description("serverUserName: user name of the UMC server account.")] string serverUserName="",
            [Description("serverPassword: password of the UMC server account; never logged.")] string serverPassword="",
            bool confirmChange=false,
            bool dryRun=true,
            int offset=0,
            int limit=100)
            => Portal.ManageUmcUsers(kind,action,name,newName,roleName,serverUserName,serverPassword,confirmChange,dryRun,offset,limit);
    }
}
