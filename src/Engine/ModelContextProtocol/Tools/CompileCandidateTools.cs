using System;
using System.ComponentModel;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.Adapters.Contracts.Candidates;
using TiaMcp.Logic.V4;
using TiaMcpServer.Siemens;

namespace TiaMcpServer.ModelContextProtocol
{
    [McpServerToolType]
    internal sealed class CompileCandidateTools
    {
        private readonly Portal portal;
        public CompileCandidateTools(Portal portal) { this.portal = portal; }
        private CallToolResult Run(CompileRequest request, string password, string mode, bool confirm, string hash, string file)
        {
            Envelope result;
            if (BehaviorCapabilities.Select(typeof(CompileCandidateTools).Assembly, McpServer.ReleaseKey, "P6-COMPILE") != BehaviorPolicy.SafeV4)
                result = CompileSession.Result(McpServer.ReleaseKey, request.Entry, Meta.Correlate(InvocationJournal.CorrelationId), null,
                    new Error("Compile candidate is not selected.", new UnsupportedCapabilityDetails(McpServer.ReleaseKey, "P6-COMPILE", "candidate")), Outcome.RejectedBeforeOperation, Execution.NotStarted);
            else result = portal.RunCompileCandidate(request, password, mode, confirm, hash, file);
            var wire = McpResult.From(result);
            return new CallToolResult { IsError = wire.IsError, StructuredContent = JsonNode.Parse(wire.StructuredContent.GetRawText()), Content = new[] { new TextContentBlock { Text = wire.Content[0].Text } } };
        }
        [BehaviorCandidate("CompilePlcSoftware", "P6-COMPILE", typeof(CompileContract))]
        [Description(CompileContract.Description)]
        public CallToolResult CompilePlcSoftware(string softwarePath = "", string password = "", string offlinePolicy = "require", string mode = "preview", bool confirm = false, string expectedPlanHash = "", string expectedProjectFile = "")
            => Run(new CompileRequest { Entry = "CompilePlcSoftware", SoftwarePath = softwarePath, OfflinePolicy = offlinePolicy, PasswordProvided = password.Length > 0 }, password, mode, confirm, expectedPlanHash, expectedProjectFile);
        [BehaviorCandidate("CompilePlcDiagnostics", "P6-COMPILE", typeof(CompilePlcDiagnosticsContract))]
        [Description(CompileContract.Description)]
        public CallToolResult CompilePlcDiagnostics(string softwarePath = "", string password = "", string offlinePolicy = "require", string mode = "preview", bool confirm = false, string expectedPlanHash = "", string expectedProjectFile = "")
            => Run(new CompileRequest { Entry = "CompilePlcDiagnostics", SoftwarePath = softwarePath, OfflinePolicy = offlinePolicy, PasswordProvided = password.Length > 0 }, password, mode, confirm, expectedPlanHash, expectedProjectFile);
        [BehaviorCandidate("CompileHmiDiagnostics", "P6-COMPILE", typeof(CompileHmiContract))]
        [Description(CompileContract.Description)]
        public CallToolResult CompileHmiDiagnostics(string softwarePath = "", string offlinePolicy = "require", string mode = "preview", bool confirm = false, string expectedPlanHash = "", string expectedProjectFile = "")
            => Run(new CompileRequest { Entry = "CompileHmiDiagnostics", SoftwarePath = softwarePath, OfflinePolicy = offlinePolicy }, "", mode, confirm, expectedPlanHash, expectedProjectFile);
        [BehaviorCandidate("CompileDevice", "P6-COMPILE", typeof(CompileHardwareContract))]
        [Description(CompileContract.Description)]
        public CallToolResult CompileDevice(string[] devicePath, string[]? itemPath = null, string offlinePolicy = "require", string mode = "preview", bool confirm = false, string expectedPlanHash = "", string expectedProjectFile = "")
            => Run(new CompileRequest { Entry = "CompileDevice", DevicePath = devicePath, ItemPath = itemPath ?? Array.Empty<string>(), OfflinePolicy = offlinePolicy }, "", mode, confirm, expectedPlanHash, expectedProjectFile);
    }
}
