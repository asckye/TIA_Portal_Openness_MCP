extern alias toolpipeline;

using System.Text.Json.Nodes;
using TiaMcp.Logic.V4;
using TiaOpenness.Shared;
using McpServer = toolpipeline::TiaMcpServer.ModelContextProtocol.McpServer;
using Xunit;

public sealed class ApprovalOperationTests
{
    [Fact]
    public void Every_settings_category_has_an_applied_call_and_the_expected_confirmation_policy()
    {
        using var actor = ActorScope.EnterCall("approval-scope-test");
        var covered = new HashSet<WriteApprovalOperation>();
        void Check(string tool, WriteApprovalOperation expected, bool always, JsonObject? args = null)
        {
            args ??= new JsonObject();
            args["dryRun"] = false;
            bool classifiedWrite = McpServer.IsWriteTool(tool);
            var candidate = HostBehavior.ApprovalWriteCandidate(tool, args, classifiedWrite, hasDryRun: true, defaultPreview: true);
            Assert.NotNull(candidate);
            Assert.Equal(expected, HostBehavior.AppliedWriteOperation(tool, args, candidate));
            Assert.Equal(always, HostBehavior.ApprovalRequired(false, tool, args, candidate));
            Assert.True(HostBehavior.ApprovalRequired(true, tool, args, candidate));
            covered.Add(expected);
        }

        Check("DeletePlcBlock", WriteApprovalOperation.OfflineWrite, false);
        Check("SaveProjectCopy", WriteApprovalOperation.SaveOrCloseProject, false);
        Check("CloseProject", WriteApprovalOperation.SaveOrCloseProject, false);
        Check("StageImportFiles", WriteApprovalOperation.ImportStaging, false);
        Check("SaveExportContent", WriteApprovalOperation.OutsideOutputFolder, false,
            new JsonObject { ["outputPath"] = Path.Combine(Path.GetTempPath(), "export.xml") });
        Check("ManagePlcGitRepository", WriteApprovalOperation.GitStageOrCommit, false, new JsonObject { ["action"] = "stage" });
        Check("GetEnvironmentDiagnostics", WriteApprovalOperation.EnvironmentRepair, false, new JsonObject { ["fix"] = true });
        Check("EnsureOpennessUserGroup", WriteApprovalOperation.OpennessGroupChange, false);
        Check("CompilePlcSoftware", WriteApprovalOperation.CompileWithSafetyPassword, false,
            new JsonObject { ["softwarePath"] = "devices/D/CPU", ["password"] = "safety-fixture" });
        Assert.Null(HostBehavior.ApprovalWriteCandidate("CompilePlcSoftware",
            new JsonObject { ["softwarePath"] = "devices/D/CPU", ["dryRun"] = false }, McpServer.IsWriteTool("CompilePlcSoftware"),
            hasDryRun: true, defaultPreview: true));
        Check("WritePlcSimAdvancedTags", WriteApprovalOperation.SimulatorWrite, false);
        Check("ManagePlcDataBlockSnapshot", WriteApprovalOperation.SnapshotReadExportOrCapture, false, new JsonObject { ["action"] = "createSnapshot" });
        Check("InvokeUnifiedOpenPipe", WriteApprovalOperation.OpenPipeRead, false,
            new JsonObject { ["request"] = new JsonObject { ["message"] = "BrowseTags", ["params"] = new JsonObject() } });
        Check("CloseProject", WriteApprovalOperation.DiscardOnClose, true, new JsonObject { ["discardChanges"] = true });
        Check("WritePlcWebVars", WriteApprovalOperation.OnlineWrite, true);
        Assert.Equal(Enum.GetValues<WriteApprovalOperation>(), covered.Order());
    }
}
