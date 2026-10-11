using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using TiaMcp.Logic.V4;
using TiaMcp.Logic.V4.Inputs;
using System.ComponentModel;
using ModelContextProtocol.Server;
using TiaMcpServer.Siemens.Services;
namespace TiaMcpServer.ModelContextProtocol
{
    // Security tools keep their native service boundary and expose its evidence in
    // the V4 envelope. Typed input admission belongs to the shared catalog.
    [McpServerToolType]
    internal sealed class ProjectSecurityTools
    {
        private readonly ProjectSecurityService _service;

        public ProjectSecurityTools(ProjectSecurityService service) => _service = service;

        [McpServerTool(Name="GetProjectUserManagement"), Description("[L2][Security][READ] Paginated UMAC listing of the bound project: category users/anonymousUser/systemRoles/customRoles/engineeringRights/customDeviceRights/umcUsers/umcUserGroups/passwordPolicy/deviceRights/roleDeviceRights. Optional exact name filter; deviceRights/roleDeviceRights need devicePath (+itemPath) arrays of exact names. Requires the native UmacConfigurator service (protected project); passwords never readable. Nothing modified.")]
        public CallToolResult GetProjectUserManagement(string category="users", string name="", string[] devicePath=null!, string[] itemPath=null!, int offset=0, int limit=100)
            => SecurityToolContract.Invoke("GetProjectUserManagement", true, false,
                () => _service.ReadProjectUserManagement(category,name,SecurityToolContract.Path(devicePath),SecurityToolContract.Path(itemPath),offset,limit));
        [McpServerTool(Name="ManageProjectUserManagement"), Description("[L2][Security][WRITE] UMAC mutation by exact names: createUser/deleteUser/setUserPassword/activateUser/deactivateUser/assignRole/unassignRole (name=user, roleName), createRole/deleteRole/assignEngineeringRight/unassignEngineeringRight/assignDeviceRight/unassignDeviceRight (name=custom role, rightName, devicePath), createDeviceRight/deleteDeviceRight (name, group, comment), activateAnonymousUser/deactivateAnonymousUser (no name; UmacConfigurator.ActivateAnonymousUser/DeactivateAnonymousUser, single anonymous user per protected project - roles are assigned with assignRole name=Anonymous). Default preview; real execution needs dryRun=false AND confirmChange=true. Password goes to the API as SecureString and is never logged. Readback verified; no save/compile/download; system roles and project protection itself are never changed.")]
        public CallToolResult ManageProjectUserManagement(
            [Description("createUser | deleteUser | setUserPassword | activateUser | deactivateUser | assignRole | unassignRole | createRole | deleteRole | assignEngineeringRight | unassignEngineeringRight | assignDeviceRight | unassignDeviceRight | createDeviceRight | deleteDeviceRight | activateAnonymousUser | deactivateAnonymousUser. ")] string action,
            string name,
            string password="",
            [Description("roleName: exact role name.")] string roleName="",
            [Description("rightName: exact name of the engineering / device right.")] string rightName="",
            [Description("comment: comment text.")] string comment="",
            [Description("group: group name.")] string group="",
            string[] devicePath=null!,
            string[] itemPath=null!,
            bool confirmChange=false,
            bool dryRun=true)
            => SecurityToolContract.Invoke("ManageProjectUserManagement", dryRun, false,
                () => _service.ManageProjectUserManagement(action,name,password,roleName,rightName,comment,group,SecurityToolContract.Path(devicePath),SecurityToolContract.Path(itemPath),confirmChange,dryRun), password);
        [McpServerTool(Name="GetProjectProtection"), Description("[L2][Security][READ] Read project protection indicators of the bound project: UMAC service availability (the only native indicator, no IsProtected scalar exists), UMAC object counts, anonymous user, password policy, UMC server configurator and advanced protection provider scalars. Never enables/disables protection.")]
        public CallToolResult GetProjectProtection()
            => SecurityToolContract.Invoke("GetProjectProtection", true, false,
                () => _service.ReadProjectProtection());
        [McpServerTool(Name="ManageMultiuserSession"), Description("[L2][Project][WRITE] Multiuser/project-server access by exact alias: read (servers, open local sessions, bound session up-to-date flag and markings), listServerProjects, readLockState, listLocalSessions (serverName + projectName), connectServer (serverName, protocol Https/Http, host, port), disconnectServer, commit (CloseAndCommit with commitComment; closes the bound session). Mutations default to preview and need dryRun=false AND confirmChange=true. Never opens/creates local sessions or saves implicitly.")]
        public CallToolResult ManageMultiuserSession(
            [Description("read | listServerProjects | readLockState | listLocalSessions | connectServer | disconnectServer | commit. ")] string action="read",
            [Description("serverName: multiuser server name.")] string serverName="",
            [Description("projectName: exact project name.")] string projectName="",
            [Description("protocol: Https or Http.")] string protocol="Https",
            [Description("host: server host name or IP address.")] string host="",
            [Description("port: TCP port (0 = default).")] int port=0,
            [Description("commitComment: comment text of the commit.")] string commitComment="",
            [Description("offset: zero-based starting row for bound-session markings, server projects or local sessions; must be non-negative.")] int offset=0,
            [Description("limit: maximum page size for bound-session markings, server projects or local sessions (1..500, default 100).")] int limit=100,
            [Description("confirmChange: true is required together with dryRun=false to connect/disconnect a server or commit the bound session.")] bool confirmChange=false,
            [Description("dryRun: true previews connectServer, disconnectServer and commit; false executes those mutations only with confirmChange=true. Read actions remain read-only.")] bool dryRun=true)
            => SecurityToolContract.Invoke("ManageMultiuserSession", dryRun || action == "read" || action == "listServerProjects" || action == "readLockState" || action == "listLocalSessions", action == "commit",
                () => _service.ManageMultiuserSession(action,serverName,projectName,protocol,host,port,commitComment,offset,limit,confirmChange,dryRun));
    }
}
