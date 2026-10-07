using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.Logic.V4;
using TiaMcp.Logic.V4.Inputs;
using TiaMcpServer.ModelContextProtocol;
using TiaOpenness.Shared;
using Xunit;

namespace TiaMcpServer.Tests
{
    public sealed class ApprovalPrecheckTests
    {
        public static class Probe
        {
            internal static int Previews, Writes;
            internal static string Fault = "";
            [McpServerTool(Name = "CreatePlcTag"), ToolClassification("L1", "PLC-Software", "WRITE", batchWrite: true)]
            public static CallToolResult Write(string condition = "", bool dryRun = true)
            {
                if (dryRun) Previews++; else Writes++;
                if (condition == "cancel") throw new OperationCanceledException();
                if (condition.Length > 0 || Fault == "changed") throw new TiaMcp.Adapters.Contracts.AdapterPreconditionException("Fixture precondition.", condition.Length > 0 ? condition : "name");
                // "unknown" reaches a journaled native boundary first; "unknown-unissued" fails before any native call.
                if (!dryRun && Fault == "unknown") InvocationJournal.NativeCallStarted();
                if (!dryRun && Fault is "unknown" or "unknown-unissued") return McpServer.V4Result("CreatePlcTag", null, new Error("Fixture unknown.", new OutcomeUnknownDetails("fixture", new Dictionary<string, JsonElement>())), Outcome.Unknown, Execution.Unknown, Completeness.Unknown);
                return McpServer.V4Result("CreatePlcTag", new JsonObject { ["planned"] = dryRun }, completed: !dryRun);
            }
            [McpServerTool(Name = "CompileDevice"), ToolClassification("L2", "Hardware", "EXECUTE")]
            public static CallToolResult Compile()
            {
                Writes++;
                return McpServer.V4Result("CompileDevice", new JsonObject { ["evidence"] = new JsonObject { ["nativeOutcomeUnknown"] = true } }, new Error("Fixture unknown compile.", new OutcomeUnknownDetails("fixture", new Dictionary<string, JsonElement>())), Outcome.Unknown, Execution.Unknown, Completeness.Unknown);
            }
            [McpServerTool(Name = "ListPlcTags"), ToolClassification("L1", "PLC-Software", "READ")]
            public static CallToolResult Read() => McpServer.V4Result("ListPlcTags", new JsonObject());
        }
        private static ToolArguments Args(string json) => new ToolArguments(JsonSerializer.Deserialize<JsonElement>(json));
        private static void Configure()
        {
            McpServer.ConfigureToolBridge(new ToolCatalog(new[] { typeof(McpServer), typeof(Probe) }), () => false, new HashSet<string>());
            Probe.Previews = Probe.Writes = 0; Probe.Fault = "";
        }
        [Theory]
        [InlineData("plc")][InlineData("table")][InlineData("name")][InlineData("overwrite")][InlineData("importPath")]
        [InlineData("expectedProjectFile")][InlineData("ownership")][InlineData("exportPath")][InlineData("cancel")]
        public void Bridge_precheck_rejects_without_approval_and_has_only_request_and_end(string condition)
        {
            using var fixture = new InfrastructureContractsTests(); Configure();
            var previousSettings = ApprovalSettings.Load(ApprovalSettings.SettingsPath);
            var previousWait = McpServer.ApprovalWaitOverrideForTests; bool context = McpServer.EnterMcpApprovalContext();
            var audit = new AuditLog(Path.GetFullPath(Path.Combine("bin-build/P6-60/engine-audit", Guid.NewGuid().ToString("N"))));
            using var log = AuditInvocation.UseLog(audit); int waits = 0;
            try
            {
                new ApprovalSettings(true, 1).Save(ApprovalSettings.SettingsPath);
                McpServer.ApprovalWaitOverrideForTests = (pending, _, _) => { waits++; return Task.FromResult(new ApprovalOutcome(pending, false, null)); };
                var body = McpServer.ResultBody(McpServer.CallTool("CreatePlcTag", Args(JsonSerializer.Serialize(new { condition, dryRun = false }))))!;
                _ = V4Json.Deserialize<Envelope>(body.ToJsonString());
                Assert.Equal("rejected-before-operation", (string?)body["meta"]?["outcome"]); Assert.Equal("not-started", (string?)body["meta"]?["execution"]);
                Assert.Equal(condition == "cancel" ? "CANCELLED" : "INVALID_ARGUMENT", (string?)body["error"]?["code"]);
                Assert.Single(body["meta"]!["warnings"]!.AsArray(), w => (string?)w?["details"]?["stage"] == "approval-precheck");
                Assert.Equal(0, waits); Assert.Equal(1, Probe.Previews); Assert.Equal(0, Probe.Writes);
                Assert.Equal(new[] { "request", "end" }, audit.Read().Select(r => r.Event));
                Assert.All(audit.Read(), r => Assert.Equal((string?)body["meta"]?["requestId"], r.RequestId));
                Assert.True(audit.Verify().Passed);
            }
            finally { McpServer.LeaveMcpApprovalContext(context); McpServer.ApprovalWaitOverrideForTests = previousWait; previousSettings.Save(ApprovalSettings.SettingsPath); }
        }
        [Theory]
        [InlineData(true, false)][InlineData(true, true)][InlineData(false, false)]
        public void Valid_bridge_waits_once_and_real_call_rechecks_while_disabled_skips_preview(bool enabled, bool changed)
        {
            using var fixture = new InfrastructureContractsTests(); Configure();
            var settings = ApprovalSettings.Load(ApprovalSettings.SettingsPath); var wait = McpServer.ApprovalWaitOverrideForTests;
            bool context = McpServer.EnterMcpApprovalContext(); int waits = 0;
            try
            {
                new ApprovalSettings(enabled, 1).Save(ApprovalSettings.SettingsPath);
                McpServer.ApprovalWaitOverrideForTests = (pending, _, _) => { waits++; if (changed) Probe.Fault = "changed"; return Task.FromResult(new ApprovalOutcome(pending, !enabled, null)); };
                var watch = Stopwatch.StartNew(); var body = McpServer.ResultBody(McpServer.CallTool("CreatePlcTag", Args("{\"dryRun\":false}")))!; watch.Stop();
                Assert.Equal(changed ? "rejected-before-operation" : "succeeded", (string?)body["meta"]?["outcome"]);
                Assert.Equal(enabled ? 1 : 0, Probe.Previews); Assert.Equal(1, Probe.Writes); Assert.Equal(1, waits);
                Directory.CreateDirectory("bin-build/P6-60"); File.AppendAllText("bin-build/P6-60/latency.txt", $"engine enabled={enabled} changed={changed}: {watch.Elapsed.TotalMilliseconds:F3} ms; previews={Probe.Previews}\n");
            }
            finally { McpServer.LeaveMcpApprovalContext(context); McpServer.ApprovalWaitOverrideForTests = wait; settings.Save(ApprovalSettings.SettingsPath); }
        }
        [Theory]
        [InlineData("CreatePlcTag")][InlineData("CompileDevice")]
        public void Unknown_native_write_blocks_next_reads_and_writes_before_approval(string original)
        {
            using var fixture = new InfrastructureContractsTests(); Configure(); Probe.Fault = "unknown";
            var settings = ApprovalSettings.Load(ApprovalSettings.SettingsPath); var wait = McpServer.ApprovalWaitOverrideForTests;
            var key = McpServer.ApprovalSessionKeyForTests; var session = new object();
            bool context = McpServer.EnterMcpApprovalContext(); int waits = 0;
            try
            {
                McpServer.ApprovalSessionKeyForTests = () => session; new ApprovalSettings(true, 1).Save(ApprovalSettings.SettingsPath);
                McpServer.ApprovalWaitOverrideForTests = (pending, _, _) => { waits++; return Task.FromResult(new ApprovalOutcome(pending, false, null)); };
                Assert.Equal("OUTCOME_UNKNOWN", (string?)McpServer.ResultBody(McpServer.CallTool(original, Args(original == "CreatePlcTag" ? "{\"dryRun\":false}" : "{}")))?["error"]?["code"]);
                foreach (var name in new[] { "CreatePlcTag", "ListPlcTags" })
                {
                    var body = McpServer.ResultBody(McpServer.CallTool(name, Args(name == "CreatePlcTag" ? "{\"dryRun\":false}" : "{}")))!;
                    Assert.Equal("SESSION_RESET_REQUIRED", (string?)body["error"]?["code"]); Assert.Equal("previous-outcome-unknown", (string?)body["error"]?["details"]?["reason"]);
                    Assert.Equal("not-started", (string?)body["meta"]?["execution"]);
                }
                Assert.Equal(original == "CreatePlcTag" ? 1 : 0, waits); Assert.Equal(original == "CreatePlcTag" ? 1 : 0, Probe.Previews); Assert.Equal(1, Probe.Writes);
            }
            finally { McpServer.LeaveMcpApprovalContext(context); McpServer.ApprovalWaitOverrideForTests = wait; McpServer.ApprovalSessionKeyForTests = key; settings.Save(ApprovalSettings.SettingsPath); }
        }
        [Fact]
        public void Unknown_write_without_a_native_call_keeps_the_session_usable()
        {
            using var fixture = new InfrastructureContractsTests(); Configure(); Probe.Fault = "unknown-unissued";
            var settings = ApprovalSettings.Load(ApprovalSettings.SettingsPath); var key = McpServer.ApprovalSessionKeyForTests; var session = new object();
            bool context = McpServer.EnterMcpApprovalContext();
            try
            {
                McpServer.ApprovalSessionKeyForTests = () => session; new ApprovalSettings(false, 1).Save(ApprovalSettings.SettingsPath);
                Assert.Equal("OUTCOME_UNKNOWN", (string?)McpServer.ResultBody(McpServer.CallTool("CreatePlcTag", Args("{\"dryRun\":false}")))?["error"]?["code"]);
                var read = McpServer.ResultBody(McpServer.CallTool("ListPlcTags", Args("{}")))!;
                Assert.True((bool?)read["ok"] == true, read.ToJsonString());
            }
            finally { McpServer.LeaveMcpApprovalContext(context); McpServer.ApprovalSessionKeyForTests = key; settings.Save(ApprovalSettings.SettingsPath); }
        }
        [Fact]
        public void Warm_fixture_reports_added_preview_latency()
        {
            var settings = ApprovalSettings.Load(ApprovalSettings.SettingsPath); var wait = McpServer.ApprovalWaitOverrideForTests;
            bool context = McpServer.EnterMcpApprovalContext();
            using var scope = AuditInvocation.UseLog(new AuditLog(Path.GetFullPath(Path.Combine("bin-build/P6-60/timing-audit", Guid.NewGuid().ToString("N")))));
            try
            {
                Configure();
                // Exclude pipe completion from the fixture precheck measurement.
                McpServer.ApprovalWaitOverrideForTests = (pending, current, _) => Task.FromResult(new ApprovalOutcome(pending, true, null));
                var samples = new Dictionary<string, List<double>> { ["enabled"] = new(), ["disabled"] = new() };
                for (int i = 0; i < 35; i++) foreach (bool enabled in new[] { false, true })
                {
                    new ApprovalSettings(enabled, 1).Save(ApprovalSettings.SettingsPath);
                    var watch = Stopwatch.StartNew(); var result = McpServer.CallTool("CreatePlcTag", Args("{\"dryRun\":false}")); watch.Stop();
                    Assert.True((bool?)McpServer.ResultBody(result)?["ok"]);
                    if (i >= 5) samples[enabled ? "enabled" : "disabled"].Add(watch.Elapsed.TotalMilliseconds);
                }
                File.WriteAllText("bin-build/P6-60/engine-latency.json", JsonSerializer.Serialize(samples));
            }
            finally { McpServer.LeaveMcpApprovalContext(context); McpServer.ApprovalWaitOverrideForTests = wait; settings.Save(ApprovalSettings.SettingsPath); }
        }
        [Theory]
        [InlineData("SW.Tags.PlcTagTable")][InlineData("SW.Types.PlcStruct")][InlineData("SW.Blocks.FC")]
        public void Single_xml_import_reads_object_names_and_refuses_existing_without_overwrite(string kind)
        {
            var document = System.Xml.Linq.XDocument.Parse($"<Document><{kind}><AttributeList><Name>Existing</Name></AttributeList></{kind}><{kind}><AttributeList><Name>Other</Name></AttributeList></{kind}></Document>");
            string prefix = kind.StartsWith("SW.Blocks.", StringComparison.Ordinal) ? "SW.Blocks." : kind.StartsWith("SW.Types.", StringComparison.Ordinal) ? "SW.Types." : "SW.Tags.PlcTagTable";
            var ex = Assert.Throws<TiaMcp.Adapters.Contracts.AdapterPreconditionException>(() => TiaMcp.PlcFoundation.PlcFoundationPolicy.RequireImportAvailable(document, prefix, false, name => name == "Existing"));
            Assert.Equal("overwrite", ex.ParamName); Assert.Contains("Existing", ex.Message);
            TiaMcp.PlcFoundation.PlcFoundationPolicy.RequireImportAvailable(document, prefix, true, _ => throw new Exception("Overwrite does not check existence."));
            TiaMcp.PlcFoundation.PlcFoundationPolicy.RequireImportAvailable(document, prefix, false, _ => false);
        }
        [Theory]
        [InlineData(true)][InlineData(false)]
        public void Symbol_lookup_preserves_mixed_case_and_avoids_full_scan_when_find_is_case_insensitive(bool insensitive)
        {
            string[] members = Enumerable.Range(0, 10000).Select(i => "Tag" + i).ToArray(); int names = 0;
            string? Find(string name) => members.FirstOrDefault(s => string.Equals(s, name, insensitive ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
            string Name(string s) { names++; return s; }
            Assert.True(TiaMcp.PlcFoundation.PlcFoundationPolicy.SymbolExists(members, Find, Name, "tAg9999"));
            names = 0; Assert.False(TiaMcp.PlcFoundation.PlcFoundationPolicy.SymbolExists(members, Find, Name, "Missing"));
            Assert.Equal(insensitive ? 2 : 10001, names);
        }
    }
}
