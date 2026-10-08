using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using TiaMcp.Logic.ModelContextProtocol;
using TiaMcp.Logic.V4.Inputs;
using TiaMcpServer.ModelContextProtocol;
using TiaOpenness.Shared;
using Xunit;

namespace TiaMcpServer.Tests
{
    public sealed class ImportStagingBridgeTests
    {
        [Fact]
        public void Actual_engine_tools_stage_without_a_native_session_after_approval_and_cleanup_is_owned()
        {
            using var fixture = new InfrastructureContractsTests();
            string bundle = Path.Combine(Path.GetTempPath(), "tia-stg-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(bundle);
            var store = new ImportStagingStore(bundle, McpServer.ReleaseKey, Guid.NewGuid().ToString("N"));
            var catalog = new ToolCatalog(new[] { typeof(McpServer), typeof(ImportStagingTools) });
            McpServer.ConfigureToolBridge(catalog, () => false, new HashSet<string>());
            EngineServices.SetServiceProvider(new ServiceCollection().AddSingleton(new ImportStagingTools(store)).AddEngine(false, catalog).BuildServiceProvider());
            var settings = ApprovalSettings.Load(ApprovalSettings.SettingsPath); var wait = McpServer.ApprovalWaitOverrideForTests;
            bool context = McpServer.EnterMcpApprovalContext(); int approvals = 0;
            var audit = new AuditLog(Path.Combine(bundle, "audit")); using var scope = AuditInvocation.UseLog(audit);
            JsonObject Call(string name, JsonObject args) => McpServer.ResultBody(McpServer.CallTool(name, new ToolArguments(JsonSerializer.SerializeToElement(args))))!.AsObject();
            JsonObject Args(string name) => new JsonObject { ["dryRun"] = false, ["files"] = new JsonArray(new JsonObject { ["fileName"] = name, ["kind"] = "scl", ["content"] = "FUNCTION Main : Void\nBEGIN\nEND_FUNCTION" }) };
            try
            {
                new ApprovalSettings(true, 1).Save(ApprovalSettings.SettingsPath);
                McpServer.ApprovalWaitOverrideForTests = async (pending, _, _) =>
                {
                    approvals++; audit.Approval(pending.RequestId, pending.Host, pending.ReleaseKey, pending.Tool, "granted", pending.PlanHash);
                    if (pending.Tool == "StageImportFiles") { Assert.Empty(store.List()["batches"]!.AsArray()); await ImportStagingTests.VerifyApproval(pending); }
                    return new ApprovalOutcome(pending, false, null);
                };
                var refused = Call("StageImportFiles", Args("../Main.scl"));
                Assert.False((bool?)refused["ok"]); Assert.Equal(0, approvals);
                Assert.Contains(refused["meta"]!["warnings"]!.AsArray(), x => (string?)x?["code"] == "RECOVERY_GUIDANCE");
                var large = Args("Main.scl"); large["files"]![0]!["content"] = new string('x', 4194304);
                var result = Call("StageImportFiles", large); Assert.True((bool?)result["ok"], result.ToJsonString()); Assert.Equal(1, approvals);
                var listed = Call("ListStagedImportFiles", new JsonObject()); Assert.True((bool?)listed["ok"]);
                var batch = listed["data"]!["batches"]![0]!;
                Assert.StartsWith(Path.Combine(bundle, "staging"), (string?)batch["directory"]);
                Assert.Equal(4194304, new FileInfo((string)batch["files"]![0]!["path"]!).Length);
                Assert.False((bool?)Call("CleanupStagedImportFiles", new JsonObject { ["batchId"] = Guid.NewGuid().ToString("N"), ["dryRun"] = false })["ok"]);
                Assert.Equal(1, approvals);
                var cleaned = Call("CleanupStagedImportFiles", new JsonObject { ["batchId"] = batch["batchId"]!.DeepClone(), ["dryRun"] = false });
                Assert.True((bool?)cleaned["ok"], cleaned.ToJsonString()); Assert.Equal(2, approvals); Assert.Empty(store.List()["batches"]!.AsArray());
                Assert.True(audit.Verify().Passed);
            }
            finally
            {
                McpServer.LeaveMcpApprovalContext(context); McpServer.ApprovalWaitOverrideForTests = wait; settings.Save(ApprovalSettings.SettingsPath);
                if (Directory.Exists(bundle)) Directory.Delete(bundle, true);
            }
        }
    }
}
