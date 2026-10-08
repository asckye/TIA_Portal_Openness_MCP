using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.Logic.ModelContextProtocol;
using TiaMcp.Logic.V4.Inputs;
using TiaMcpServer.ModelContextProtocol;
using TiaOpenness.Shared;
using Xunit;

namespace TiaMcpServer.Tests
{
    public sealed class ImportStagingBridgeTests
    {
        [Theory]
        [InlineData(false)][InlineData(true)]
        public async Task Http_scope_keeps_the_owner_through_Sdk_dispatch_and_the_CallTool_bridge(bool bridge)
        {
            using var fixture = new InfrastructureContractsTests();
            string bundle = Path.Combine(Path.GetTempPath(), "tia-stg-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            const string selectionKey = "TiaOpenness.Shared.BundleLayout.v4";
            var selected = AppDomain.CurrentDomain.GetData(selectionKey);
            var settings = ApprovalSettings.Load(ApprovalSettings.SettingsPath);
            var wait = McpServer.ApprovalWaitOverrideForTests;
            bool transport = ImportStagingTools.HttpTransport;
            using var a = new ImportStagingSession("http-A"); using var b = new ImportStagingSession("http-B");
            var catalog = new ToolCatalog(new[] { typeof(McpServer), typeof(ImportStagingTools) });
            using var services = new ServiceCollection().AddEngine(false, catalog).BuildServiceProvider();
            EngineServices.SetServiceProvider(services); McpServer.ConfigureToolBridge(catalog, () => false, new HashSet<string>());
            var tools = catalog.Methods.Select(pair => ToolCatalog.CreateTool(pair.Value)).ToList();
            bool context = McpServer.EnterMcpApprovalContext();
            async Task<JsonObject> Call(ImportStagingSession session, string name, JsonObject args)
            {
                string target = bridge ? "CallTool" : name;
                var request = new RequestContext<CallToolRequestParams>(DispatchProxy.Create<IMcpServer, ToolCatalogServerProxy>())
                { Params = JsonSerializer.Deserialize<CallToolRequestParams>(new JsonObject { ["name"] = target,
                    ["arguments"] = bridge ? new JsonObject { ["name"] = name, ["arguments"] = args } : args,
                    ["_meta"] = new JsonObject { [ImportStagingTools.SessionMetadata] = session.SessionId } }.ToJsonString(), global::ModelContextProtocol.McpJsonUtilities.DefaultOptions) };
                using var scope = ImportStagingTools.UseSession(request);
                return McpServer.ResultBody(await tools.Single(t => t.ProtocolTool.Name == target).InvokeAsync(request))!.AsObject();
            }
            try
            {
                Directory.CreateDirectory(bundle); AppDomain.CurrentDomain.SetData(selectionKey, bundle);
                ImportStagingTools.HttpTransport = true; ImportStagingTools.RegisterHttpSession(a); ImportStagingTools.RegisterHttpSession(b);
                new ApprovalSettings(true, 1).Save(ApprovalSettings.SettingsPath); int approvals = 0;
                McpServer.ApprovalWaitOverrideForTests = (pending, _, _) =>
                { approvals++; return Task.FromResult(new ApprovalOutcome(pending, false, null)); };
                var stage = await Call(a, "StageImportFiles", new JsonObject { ["dryRun"] = false, ["files"] = new JsonArray(
                    new JsonObject { ["fileName"] = "F.scl", ["kind"] = "scl", ["content"] = "FUNCTION F : Void\nBEGIN\nEND_FUNCTION" }) });
                Assert.True((bool?)stage["ok"], stage.ToJsonString()); Assert.Equal("http-A", (string?)stage["data"]?["mcpSessionId"]);
                var list = await Call(b, "ListStagedImportFiles", new JsonObject());
                Assert.Equal("live-other", (string?)list["data"]?["batches"]?[0]?["ownerState"]);
                string id = (string)stage["data"]!["batchId"]!;
                var refused = await Call(b, "CleanupStagedImportFiles", new JsonObject { ["batchId"] = id, ["dryRun"] = false });
                Assert.False((bool?)refused["ok"]); Assert.Equal(bridge ? 1 : 0, approvals);
                a.Dispose(); ImportStagingTools.RemoveHttpSession(a);
                var cleaned = await Call(b, "CleanupStagedImportFiles", new JsonObject { ["batchId"] = id, ["dryRun"] = false });
                Assert.True((bool?)cleaned["ok"], cleaned.ToJsonString()); Assert.Equal(bridge ? 2 : 0, approvals);
                Assert.Empty((await Call(b, "ListStagedImportFiles", new JsonObject()))["data"]!["batches"]!.AsArray());
            }
            finally
            {
                McpServer.LeaveMcpApprovalContext(context);
                ImportStagingTools.RemoveHttpSession(a); ImportStagingTools.RemoveHttpSession(b); ImportStagingTools.HttpTransport = transport;
                AppDomain.CurrentDomain.SetData(selectionKey, selected); McpServer.ApprovalWaitOverrideForTests = wait; settings.Save(ApprovalSettings.SettingsPath);
                if (Directory.Exists(bundle)) Directory.Delete(bundle, true);
            }
        }
        [Fact]
        public void Disposing_the_engine_host_ends_its_stdio_staging_lifetime()
        {
            string bundle = Path.Combine(Path.GetTempPath(), "tia-stg-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(bundle);
            try
            {
                var services = new ServiceCollection().AddEngine(false, new ToolCatalog(new[] { typeof(ImportStagingTools) })).BuildServiceProvider();
                var lifetime = services.GetRequiredService<ImportStagingHostLifetime>();
                var old = new ImportStagingStore(bundle, "18", lifetime.Session);
                var batch = old.Stage(new[] { new StagedTextFile { FileName = "F.scl", Kind = "scl", Content = "FUNCTION F : Void\nBEGIN\nEND_FUNCTION" } }, false);
                using var current = new ImportStagingStore(bundle, "18", Guid.NewGuid().ToString("N"));
                Assert.Equal("live-other", (string?)current.List()["batches"]![0]!["ownerState"]);
                Assert.Throws<ArgumentException>(() => current.Cleanup((string)batch["batchId"]!, false));
                services.Dispose();
                Assert.Equal("ended", (string?)current.List()["batches"]![0]!["ownerState"]);
                current.Cleanup((string)batch["batchId"]!, false); Assert.Empty(current.List()["batches"]!.AsArray());
            }
            finally { Directory.Delete(bundle, true); }
        }
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
