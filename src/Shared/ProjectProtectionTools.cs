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
        [McpServerTool(Name="GetProjectProtection"), Description("[L2][Security][READ] Read project protection indicators of the bound project: UMAC availability plus EngineeringFunctionRights.Any() (projectProtectedFlag on V17+, unknown on V15.1/V16; no IsProtected scalar exists), UMAC object counts, anonymous user, password policy, UMC server configurator. ProtectionProviderBase concerns block protection and cannot detect project protection. Never enables/disables protection.")]
        public CallToolResult GetProjectProtection()
            => SecurityToolContract.Invoke("GetProjectProtection", true, false,
                () => _service.ReadProjectProtection());
}
