using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.Adapters.Contracts;
using TiaMcpServer.Siemens;
using TiaMcpServer.Siemens.Services;
namespace TiaMcpServer.ModelContextProtocol;
[McpServerToolType]
internal sealed class ProjectLifecycleDeviceTools
{
    private readonly ProjectLifecyclePortService _service;
    public ProjectLifecycleDeviceTools(ProjectLifecyclePortService service) => _service = service;
        [McpServerTool(Name = "ValidateAutomationContext"), Description("[L1][Diagnostics]Preflight current project for automation: devices, software, expected PLC/HMI paths, and project tree.")]
        public CallToolResult ValidateAutomationContextV4(
            [Description("expectedPlcSoftwarePath: expected PLC software path, empty to skip")] string expectedPlcSoftwarePath = "PLC_1",
            [Description("expectedHmiSoftwarePath: expected HMI software path, empty to skip")] string expectedHmiSoftwarePath = "HMI_RT_1")
            => HardwareContract.Run("ValidateAutomationContext", () =>
            {
                return ValidateAutomationContext(expectedPlcSoftwarePath, expectedHmiSoftwarePath);
            }, write: false, current: false);

        public ResponseMessage ValidateAutomationContext(
            [Description("expectedPlcSoftwarePath: expected PLC software path, empty to skip")] string expectedPlcSoftwarePath = "PLC_1",
            [Description("expectedHmiSoftwarePath: expected HMI software path, empty to skip")] string expectedHmiSoftwarePath = "HMI_RT_1")
        {
            try
            {
                return _service.ValidateAutomationContext(expectedPlcSoftwarePath, expectedHmiSoftwarePath);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error validating automation context: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

}
