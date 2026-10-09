using ModelContextProtocol.Protocol;
using TiaMcp.Logic.V4.Inputs;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using TiaMcpServer.Siemens;

namespace TiaMcpServer.ModelContextProtocol
{
    [McpServerToolType]
    internal sealed class DiagnosticsTools
    {
        private readonly IEngineeringSession _session;

        public DiagnosticsTools(IEngineeringSession session) => _session = session;

        [McpServerTool(Name = "RunCapabilitySelfTest"), Description("[L0][Diagnostics]Run a read-only MCP/TIA readiness self-test. It checks Openness group membership, connection state, visible portal processes, optional automation context, and optional project tree readback without writing to the project.")]
        public Task<CallToolResult> RunCapabilitySelfTestV4(
            [Description("When true, call ConnectPortal before checks if the server is not connected. This is read-only but attaches to TIA Portal.")] bool connectIfNeeded = false,
            [Description("When true, include GetProjectTree output if a project is open.")] bool includeProjectTree = false,
            [Description("When true, enumerate TIA Portal process/project details. This may attach to running TIA processes and can be slow on a contended workstation.")] bool inspectPortalProcesses = false,
            [Description("Expected PLC software path for ValidateAutomationContext.")] string expectedPlcSoftwarePath = "PLC_1",
            [Description("Expected HMI software path for ValidateAutomationContext.")] string expectedHmiSoftwarePath = "HMI_RT_1")
            => SessionToolContract.RunAsync("RunCapabilitySelfTest", connectIfNeeded, false, () => RunCapabilitySelfTest(connectIfNeeded, includeProjectTree, inspectPortalProcesses, expectedPlcSoftwarePath, expectedHmiSoftwarePath));

        public Task<ResponseCapabilitySelfTest> RunCapabilitySelfTest(
            [Description("When true, call ConnectPortal before checks if the server is not connected. This is read-only but attaches to TIA Portal.")] bool connectIfNeeded = false,
            [Description("When true, include GetProjectTree output if a project is open.")] bool includeProjectTree = false,
            [Description("When true, enumerate TIA Portal process/project details. This may attach to running TIA processes and can be slow on a contended workstation.")] bool inspectPortalProcesses = false,
            [Description("Expected PLC software path for ValidateAutomationContext.")] string expectedPlcSoftwarePath = "PLC_1",
            [Description("Expected HMI software path for ValidateAutomationContext.")] string expectedHmiSoftwarePath = "HMI_RT_1")
        => CapabilitySelfTestLogic.Run(Siemens.Openness.IsUserInGroupNoFix, _session.ListPortalProcessProjects,
                _session.GetState, _session.ConnectPortal, _session.ValidateAutomationContext, _session.GetProjectTree,
                connectIfNeeded, includeProjectTree, inspectPortalProcesses, expectedPlcSoftwarePath, expectedHmiSoftwarePath);

    }
}
