using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcpServer.Siemens.Services;
namespace TiaMcpServer.ModelContextProtocol;
[McpServerToolType]
internal sealed class ProjectUserManagementTools
{
    private readonly ProjectUserManagementPortService _service;
    public ProjectUserManagementTools(ProjectUserManagementPortService service) => _service = service;
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
}
