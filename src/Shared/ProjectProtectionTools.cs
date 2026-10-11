using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcpServer.Siemens.Services;
namespace TiaMcpServer.ModelContextProtocol;
[McpServerToolType]
internal sealed class ProjectProtectionTools
{
    private readonly ProjectProtectionPortService _service;
    public ProjectProtectionTools(ProjectProtectionPortService service) => _service = service;
        [McpServerTool(Name="GetProjectProtection"), Description("[L2][Security][READ] Read project protection indicators of the bound project: UMAC service availability (the only native indicator, no IsProtected scalar exists), UMAC object counts, anonymous user, password policy, UMC server configurator and advanced protection provider scalars. Never enables/disables protection.")]
        public CallToolResult GetProjectProtection()
            => SecurityToolContract.Invoke("GetProjectProtection", true, false,
                () => _service.ReadProjectProtection());
}
