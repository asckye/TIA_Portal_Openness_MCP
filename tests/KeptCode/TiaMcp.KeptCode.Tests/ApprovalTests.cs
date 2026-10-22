using System;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TiaMcp.Logic.V4;
using TiaOpenness.Shared;
using Xunit;

namespace TiaMcp.Engine.Tests
{
    internal static class ApprovalTestDataRoot
    {
        internal static string Root { get; private set; } = "";

        [ModuleInitializer]
        internal static void Initialize()
        {
            Root = Path.Combine(Path.GetTempPath(), "tia-engine-approval-tests-" + Guid.NewGuid().ToString("N"));
            var config = Path.Combine(Root, "config"); Directory.CreateDirectory(config);
            File.WriteAllText(Path.Combine(config, "approval.settings"), "enabled=true\ntimeoutSeconds=1\n", new UTF8Encoding(false));
            Environment.SetEnvironmentVariable("TIA_MCP_DATA_DIRECTORY", Root);
            AppDomain.CurrentDomain.ProcessExit += (_, _) =>
            {
                try { Directory.Delete(Root, true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            };
        }
    }

    public sealed class ApprovalTests
    {
        public sealed class BatchGateProbe
        {
            [global::ModelContextProtocol.Server.McpServerTool(Name = "CreateApprovalFixture"), TiaMcpServer.ModelContextProtocol.ToolClassification("L2", "Meta", "WRITE", batchWrite: true)]
            public static global::ModelContextProtocol.Protocol.CallToolResult Write(string target, bool dryRun = true, string outcome = "success")
            {
                InfrastructureContractsTests.BatchProbes.Calls.Add(target + ":" + dryRun);
                if (!dryRun && outcome == "throw") throw new InvalidOperationException("private fixture input");
                if (!dryRun && outcome == "cancel") throw new OperationCanceledException("private fixture input");
                if (!dryRun && outcome == "refusal") return TiaMcpServer.ModelContextProtocol.McpServer.V4Reject("CreateApprovalFixture",
                    new Error("Fixture rejected.", new PreconditionFailedDetails("fixture", null)));
                if (!dryRun && outcome == "failure") return TiaMcpServer.ModelContextProtocol.McpServer.V4Result("CreateApprovalFixture", null,
                    new Error("Fixture failed.", new InternalErrorDetails(null)), Outcome.Failed, Execution.Completed, Completeness.None);
                if (!dryRun && outcome == "unknown") return TiaMcpServer.ModelContextProtocol.McpServer.TargetFailure("CreateApprovalFixture", new IOException("Fixture I/O interruption; token=private-fixture-input"), true);
                return TiaMcpServer.ModelContextProtocol.McpServer.V4Result("CreateApprovalFixture", new System.Text.Json.Nodes.JsonObject { ["target"] = target }, completed: !dryRun);
            }
        }
        public static class SessionApprovalProbe
        {
            internal static int Calls;
            [global::ModelContextProtocol.Server.McpServerTool(Name = "SaveProject"), TiaMcpServer.ModelContextProtocol.ToolClassification("L0", "Project", "SESSION")]
            public static global::ModelContextProtocol.Protocol.CallToolResult SaveProject(string mode = "preview", bool confirm = false,
                string expectedPlanHash = "", string expectedProjectFile = "") => Entered("SaveProject");
            [global::ModelContextProtocol.Server.McpServerTool(Name = "SaveProjectCopy"), TiaMcpServer.ModelContextProtocol.ToolClassification("L0", "Project", "SESSION")]
            public static global::ModelContextProtocol.Protocol.CallToolResult SaveProjectCopy(string newProjectPath, string mode = "preview", bool confirm = false,
                string expectedPlanHash = "", string expectedProjectFile = "") => Entered("SaveProjectCopy");
            [global::ModelContextProtocol.Server.McpServerTool(Name = "CloseProject"), TiaMcpServer.ModelContextProtocol.ToolClassification("L0", "Project", "SESSION")]
            public static global::ModelContextProtocol.Protocol.CallToolResult CloseProject(bool saveChanges = false, bool discardChanges = false,
                bool confirmDiscard = false, string mode = "preview", bool confirm = false, string expectedPlanHash = "", string expectedProjectFile = "")
                => Entered("CloseProject");
            private static global::ModelContextProtocol.Protocol.CallToolResult Entered(string name)
            { Calls++; return TiaMcpServer.ModelContextProtocol.McpServer.V4Result(name, new System.Text.Json.Nodes.JsonObject { ["called"] = true }); }
        }
        private static string Scratch() => Path.GetFullPath(Path.Combine("bin-build", "P6-44", "approval-tests", Guid.NewGuid().ToString("N")));
        private static PendingApproval Request(int seconds = 3) => PendingApproval.Create("engine", "21", "WriteFixture",
            "{\"blockPath\":\"PLC/Block\",\"password\":\"private-input\"}", "{\"projectFile\":\"P.ap21\",\"bindingEpoch\":1}", seconds);

        private static Task<global::ModelContextProtocol.Protocol.CallToolResult> CallToolWithDecision(string name, string arguments)
        {
            var previous = TiaMcpServer.ModelContextProtocol.McpServer.ApprovalWaitOverrideForTests;
            TiaMcpServer.ModelContextProtocol.McpServer.ApprovalWaitOverrideForTests = (pending, _, _) =>
            {
                Assert.Equal(name, pending.Tool);
                return Task.FromResult(new ApprovalOutcome(pending, false, null));
            };
            try { return Task.FromResult(TiaMcpServer.ModelContextProtocol.McpServer.CallTool(name, Args(arguments))); }
            finally { TiaMcpServer.ModelContextProtocol.McpServer.ApprovalWaitOverrideForTests = previous; }
        }

        private static TiaMcp.Logic.V4.Inputs.ToolArguments Args(string json)
            => new TiaMcp.Logic.V4.Inputs.ToolArguments(System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(json));

        [Theory]
        [InlineData("--isolate-openness")]
        [InlineData("--no-isolate-openness")]
        [InlineData("--openness-worker-child")]
        [InlineData("--transport")]
        [InlineData("--http-prefix")]
        public void Engine_refuses_retired_host_modes(string option)
        {
            var error = Assert.Throws<ArgumentException>(() => TiaMcpServer.CliOptions.ParseArgs(new[] { option }));
            Assert.Contains("FoundationHost", error.Message);
        }
        [Fact]
        public async Task Approval_frames_refuse_oversize_truncation_and_self_decision_shape()
        {
            using var oversized = new MemoryStream(BitConverter.GetBytes(ApprovalFrames.MaximumBytes + 1));
            await Assert.ThrowsAsync<InvalidDataException>(() => ApprovalFrames.Read<ApprovalDecision>(oversized, CancellationToken.None));
            using var shortFrame = new MemoryStream(new byte[] { 10, 0, 0, 0, 1 });
            await Assert.ThrowsAsync<EndOfStreamException>(() => ApprovalFrames.Read<ApprovalDecision>(shortFrame, CancellationToken.None));
            using var selfApprove = new MemoryStream(); await ApprovalFrames.Write(selfApprove, new ApprovalDecision { Decision = "granted" }, CancellationToken.None);
            selfApprove.Position = 0;
            await Assert.ThrowsAsync<InvalidDataException>(() => ApprovalFrames.Read<PendingApproval>(selfApprove, CancellationToken.None));
            using var missingVersion = new MemoryStream();
            byte[] missing = System.Text.Encoding.UTF8.GetBytes("{\"RequestId\":\"r1\",\"PlanHash\":\"hash\",\"ArgumentDigest\":\"digest\",\"Decision\":\"granted\"}");
            missingVersion.Write(BitConverter.GetBytes(missing.Length)); missingVersion.Write(missing); missingVersion.Position = 0;
            await Assert.ThrowsAsync<InvalidDataException>(() => ApprovalFrames.Read<ApprovalDecision>(missingVersion, CancellationToken.None));
        }
        [Theory]
        [InlineData("request")]
        [InlineData("hash")]
        [InlineData("arguments")]
        [InlineData("version")]
        public void A_decision_cannot_authorize_another_request_or_plan(string mismatch)
        {
            var request = Request(); var decision = new ApprovalDecision { RequestId = request.RequestId, PlanHash = request.PlanHash,
                ArgumentDigest = request.ArgumentDigest, Decision = "granted" };
            Assert.True(decision.Matches(request));
            switch (mismatch) { case "request": decision.RequestId = "other"; break; case "hash": decision.PlanHash = new string('b', 64); break;
                case "arguments": decision.ArgumentDigest = new string('c', 64); break; default: decision.Version = 2; break; }
            Assert.False(decision.Matches(request));
        }
        [Theory]
        [InlineData("denied")]
        [InlineData("timeout")]
        [InlineData("workbench-unavailable")]
        [InlineData("plan-confirmation")]
        public void Confirmation_details_are_closed_and_roundtrip(string reason)
        {
            var details = new ConfirmationRequiredDetails(reason, new string('a', 64), "r1");
            var error = V4Json.Deserialize<Error>(V4Json.Serialize(new Error("Refused.", details)));
            Assert.Equal(reason, Assert.IsType<ConfirmationRequiredDetails>(error.Details).Reason);
            Assert.Throws<ArgumentException>(() => new ConfirmationRequiredDetails("unknown", details.PlanHash, "r1"));
            Assert.Throws<ArgumentException>(() => new ConfirmationRequiredDetails("denied", null, "r1"));
            Assert.Throws<System.Text.Json.JsonException>(() => V4Json.Deserialize<Error>("{\"code\":\"CONFIRMATION_REQUIRED\",\"message\":\"Refused.\",\"details\":{\"planHash\":null}}"));
        }
        [Fact]
        public void Digest_binds_identity_and_redacted_arguments_and_redacts_display_only()
        {
            var request = Request();
            Assert.DoesNotContain("private-input", request.ParametersJson);
            Assert.Equal(request.ArgumentDigest, PendingApproval.Create("engine", "21", "WriteFixture", "{\"blockPath\":\"PLC/Block\",\"password\":\"changed\"}", "{\"projectFile\":\"P.ap21\",\"bindingEpoch\":1}", 3).ArgumentDigest);
            Assert.NotEqual(request.ArgumentDigest, PendingApproval.Create("engine", "21", "WriteFixture", "{\"blockPath\":\"PLC/Block\",\"password\":\"private-input\"}", "{\"projectFile\":\"Other.ap21\",\"bindingEpoch\":1}", 3).ArgumentDigest);
            Assert.Equal(PendingApproval.Create("engine", "21", "WriteFixture", "{\"a\":1,\"b\":2}", null, 3).PlanHash,
                PendingApproval.Create("engine", "21", "WriteFixture", "{\"b\":2,\"a\":1}", null, 3).PlanHash);
        }
        [Theory]
        [InlineData("granted", "none")]
        [InlineData("denied", "none")]
        [InlineData("granted", "request")]
        [InlineData("granted", "hash")]
        [InlineData("granted", "arguments")]
        [InlineData("granted", "version")]
        public async Task Pipe_decision_is_bound_once(string decision, string mismatch)
        {
            if (!OperatingSystem.IsWindows()) return;
            string name = "tia-approval-test-" + Guid.NewGuid().ToString("N");
            using var server = ApprovalPipe.CreateServer(name, ApprovalPipe.CurrentSid, true);
            var request = Request(); var audit = new AuditLog(Scratch());
            using var traceText = new StringWriter(); using var trace = new System.Diagnostics.TextWriterTraceListener(traceText);
            System.Diagnostics.Trace.Listeners.Add(trace);
            var response = Task.Run(async () =>
            {
                await server.WaitForConnectionAsync();
                var frame = await ApprovalFrames.Read<PendingApproval>(server, CancellationToken.None);
                Assert.True(ApprovalPipe.PeerIsCurrentUser(server, ApprovalPipe.CurrentSid));
                await ApprovalFrames.Write(server, new ApprovalDecision { RequestId = mismatch == "request" ? "other" : frame.RequestId,
                    PlanHash = mismatch == "hash" ? new string('b', 64) : frame.PlanHash,
                    ArgumentDigest = mismatch == "arguments" ? new string('c', 64) : frame.ArgumentDigest,
                    Version = mismatch == "version" ? 2 : 1, Decision = decision }, CancellationToken.None);
            });
            var result = await ApprovalClient.Wait(request, new ApprovalSettings(), CancellationToken.None, name, audit);
            System.Diagnostics.Trace.Listeners.Remove(trace);
            Assert.False(result.Reason == "workbench-unavailable", traceText.ToString());
            await response.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(decision == "granted" && mismatch == "none" ? null : "denied", result.Reason);
            Assert.Equal("denied", (await ApprovalClient.Wait(request, new ApprovalSettings(), CancellationToken.None, name, audit)).Reason);
            Assert.True(audit.Verify().Passed);
        }
        [Fact]
        public void Protected_acl_grants_only_current_sid_and_refuses_another_sid_simulation()
        {
            if (!OperatingSystem.IsWindows()) return;
            string sid = ApprovalPipe.CurrentSid;
            var security = new RawSecurityDescriptor(ApprovalPipe.SecurityDescriptor(sid));
            Assert.True((security.ControlFlags & ControlFlags.DiscretionaryAclProtected) != 0);
            Assert.Equal(new SecurityIdentifier(sid), security.Owner);
            var ace = Assert.IsType<CommonAce>(Assert.Single(security.DiscretionaryAcl!.Cast<GenericAce>()));
            Assert.Equal(new SecurityIdentifier(sid), ace.SecurityIdentifier);
            Assert.Equal(AceQualifier.AccessAllowed, ace.AceQualifier);
            Assert.NotEqual(new SecurityIdentifier("S-1-5-21-1-2-3-1001"), ace.SecurityIdentifier);
            Assert.NotEqual(ApprovalPipe.Name(Scratch(), sid), ApprovalPipe.Name(Scratch(), "S-1-5-21-1-2-3-1001"));
        }
    }
}
