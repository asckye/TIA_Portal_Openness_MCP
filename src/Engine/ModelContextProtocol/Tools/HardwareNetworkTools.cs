using System.Text.Json;
using TiaMcp.Logic.V4;
using TiaMcp.Logic.V4.Inputs;
using TiaMcp.Logic.V4.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml.Linq;
using TiaMcpServer.Siemens;
using System.ComponentModel;
using ModelContextProtocol.Server;
using TiaMcpServer.Siemens.Services;
namespace TiaMcpServer.ModelContextProtocol
{
    [McpServerToolType]
    internal sealed class HardwareSecurityTools
    {
        private readonly HardwareNetworkService _hardware;
        public HardwareSecurityTools(HardwareNetworkService hardware) => _hardware = hardware;
        [McpServerTool(Name="ManageDeviceUsers"), Description("[L2][Hardware][WRITE] Users of the exact hardware object's official services: family=webserver (CPU WebserverUserManagement.WebserverUsers: read, create with permissions flag names such as [\"ReadTag\",\"DoDiagnosis\"] and password, delete, setPermissions, setPassword), family=simpleWebserver (SIWAREX SimpleWebserverUserManagement: fixed user slots; setActive, rename via newName, setPermissions None/ReadOnly/ReadWrite, setPassword) and family=opcUa (OpcUaUserManagement.OpcUaUsers: read, create with password, delete, setPassword). Passwords are converted to SecureString and never echoed; TIA refuses writes while the web server / OPC UA authentication is disabled. Deletes need confirmDelete. Default dryRun=true; no save/compile/download. Native behaviorPolicy=current; V4 native acceptance is pending.")]
        public CallToolResult ManageDeviceUsers(
            string[] devicePath,
            string[] itemPath,
            [Description("family: webserver | simpleWebserver | opcUa.")] string family,
            [Description("action: the operation to perform - read | create | delete | setPassword | setPermissions | setActive | rename.")] string action="read",
            [Description("userName: user name.")] string userName="",
            string password="",
            [Description("permissions: array of permission names (see the tool description).")] string[] permissions = null!,
            [Description("active: true activates, false deactivates.")] bool active=true,
            string newName="",
            bool confirmDelete=false,
            bool dryRun=true)
            => HardwareToolContract.Invoke("ManageDeviceUsers", dryRun || action == "read", action != "read", () =>
            {
                string devicePathJson = HardwareToolContract.Path(devicePath, "devicePath", rootAllowed: false, optional: false);
                string itemPathJson = HardwareToolContract.Path(itemPath, "itemPath", rootAllowed: true, optional: false);
                string permissionsJson = HardwareToolContract.Names(permissions, "permissions", 32, optional: true);
                HardwareToolContract.Check(() =>
                {
                    HardwareNetworkLogic.ValidateUserRequest(family, action, userName, password, permissions ?? Array.Empty<string>(), newName);
                    if (action == "delete") HardwareToolContract.Confirm(dryRun, confirmDelete);
                });
                return _hardware.ManageDeviceUsers(devicePathJson,itemPathJson,family,action,userName,password,permissionsJson,active,newName,confirmDelete,dryRun);
            });
    }
}
