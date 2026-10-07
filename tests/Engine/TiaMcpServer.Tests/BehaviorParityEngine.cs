using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using TiaMcp.Logic.ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.Adapters.Contracts;
using TiaMcp.BehaviorParity;
using TiaMcp.Logic.V4.Inputs;
using TiaMcpServer.ModelContextProtocol;
using TiaOpenness.Shared;

namespace TiaMcpServer.Tests
{
    // The production CallTool binder, approval precheck, dispatch and outcome observer
    // execute here; only the Siemens-backed tool body is a fixture.
    public static class BehaviorParityEngine
    {
        private static string scenario = "";
        private static int writes, previews, waits;
        public static class Probe
        {
            [McpServerTool(Name = "CreatePlcTag"), ToolClassification("L1", "PLC-Software", "WRITE", batchWrite: true)]
            public static CallToolResult Write(string plc, string table, string name, string dataType, string address,
                bool dryRun = true, bool confirm = false, string expectedProjectFile = "")
            {
                if (dryRun) previews++; else writes++;
                BehaviorParityCases.ExportAdmission(scenario);
                if (scenario.StartsWith("blocked-export", StringComparison.Ordinal))
                {
                    var data = new JsonObject { ["kind"] = "technology-object", ["status"] = "inconsistent", ["planHash"] = new string('a',64) };
                    TiaMcp.Logic.V4.HostBehavior.ExportPreview(data);
                    if (!dryRun) NativeExportPolicy.RequireApply("inconsistent");
                    return McpServer.V4Result("CreatePlcTag", data);
                }
                if (scenario == "argument") throw new AdapterPreconditionException("Fixture argument refusal.", "name");
                if (scenario == "wrapped-argument") throw new InvalidOperationException("Private wrapper text.", new AdapterPreconditionException("Fixture argument refusal.", "name"));
                if (scenario == "existing-no-overwrite") throw new AdapterPreconditionException("Existing object; overwrite=false.", "overwrite");
                if (scenario == "precondition") throw new AdapterPreconditionException("Fixture state refusal.", "softwarePath", false);
                if (scenario == "cancellation") throw new OperationCanceledException();
                if (scenario == "missing-directory" || scenario == "file-access-denied")
                    NativeInputPolicy.Read<int>("filePath", () => throw (scenario == "missing-directory" ? (Exception)new DirectoryNotFoundException() : new UnauthorizedAccessException()));
                if (scenario == "no-effect") return McpServer.V4Result("CreatePlcTag", new JsonObject {
                    ["status"] = "not-found-not-deleted", ["attempted"] = false, ["executed"] = false, ["deleted"] = false, ["targetIdentity"] = "" });
                if (!dryRun && scenario == "unknown") { InvocationJournal.NativeCallStarted(); throw new IOException("Fixture native interruption."); }
                var software = new object(); var cpu = new object();
                var selected = Siemens.SoftwareContainerLookup.FindPlc(new[] { cpu }, _ => Array.Empty<object>(), _ => "CPU",
                    _ => software, _ => "PLC_1", _ => false, plc, _ => { });
                if (!ReferenceEquals(selected, software)) throw new AdapterPreconditionException("Fixture PLC missing.", "plc");
                return McpServer.V4Result("CreatePlcTag", new JsonObject { ["executed"] = !dryRun }, completed: !dryRun);
            }
            [McpServerTool(Name = "ImportPlcExternalSource"), ToolClassification("L1", "PLC-Software", "WRITE", batchWrite: true)]
            public static CallToolResult Import(string softwarePath, string groupPath, string filePath, bool dryRun = true)
                => McpServer.V4Result("ImportPlcExternalSource", new JsonObject());
            [McpServerTool(Name = "ImportPlcBlocksFromDirectory"), ToolClassification("L1", "PLC-Software", "WRITE", batchWrite: true)]
            public static CallToolResult ImportDirectory(string softwarePath, string groupPath, string dir, bool dryRun = true, bool overwrite = false,
                string[]? importOrder = null, string expectedPlanHash = "", bool confirm = false, string expectedProjectFile = "")
            {
                if (dryRun) previews++; else writes++;
                return McpServer.V4Result("ImportPlcBlocksFromDirectory", new JsonObject { ["projectFile"] = "C:/fixture.ap19", ["planHash"] = new string('a', 64) });
            }
            [McpServerTool(Name = "ListPlcTags"), ToolClassification("L1", "PLC-Software", "READ")]
            public static CallToolResult Read(string plc, string table)
            {
                previews++;
                InvocationJournal.NativeCallStarted();
                if (scenario == "native-read") throw new IOException("Fixture native interruption.");
                return McpServer.V4Result("ListPlcTags", new JsonObject());
            }
        }

        public static string Run(string value)
        {
            if (value.StartsWith("staging-", StringComparison.Ordinal)) return RunStaging(value);
            using var fixture = new InfrastructureContractsTests();
            scenario = value; writes = previews = waits = 0;
            McpServer.ConfigureToolBridge(new ToolCatalog(new[] { typeof(McpServer), typeof(Probe) }), () => false, new HashSet<string>());
            var settings = ApprovalSettings.Load(ApprovalSettings.SettingsPath);
            var wait = McpServer.ApprovalWaitOverrideForTests; var session = McpServer.ApprovalSessionKeyForTests;
            bool context = McpServer.EnterMcpApprovalContext(); var key = new object();
            try
            {
                new ApprovalSettings(true, 1).Save(ApprovalSettings.SettingsPath);
                McpServer.ApprovalSessionKeyForTests = () => key;
                McpServer.ApprovalWaitOverrideForTests = (pending, _, _) => { waits++; return Task.FromResult(new ApprovalOutcome(pending, false,
                    scenario == "refused-approval" ? "denied" : null)); };
                JsonNode Call(string tool, JsonObject args) => McpServer.ResultBody(McpServer.CallTool(tool,
                    new ToolArguments(JsonSerializer.SerializeToElement(args))))!;
                var results = new JsonArray(BehaviorParityCases.Project(Call(value == "native-read" ? "ListPlcTags"
                    : value == "missing-directory" || value.StartsWith("batch-", StringComparison.Ordinal) && value != "batch-alias" ? "ImportPlcBlocksFromDirectory" : value == "missing-file" ? "ImportPlcExternalSource" : "CreatePlcTag", BehaviorParityCases.Arguments(value))));
                if (value.StartsWith("blocked-export", StringComparison.Ordinal) || value == "typed-export-refusal")
                {
                    scenario = "followup";
                    results.Add(BehaviorParityCases.Project(Call("ListPlcTags", new JsonObject { ["plc"] = "PLC_1", ["table"] = "T" })));
                    results.Add(BehaviorParityCases.Project(Call("CreatePlcTag", BehaviorParityCases.Arguments("followup"))));
                }
                if (value == "unknown")
                {
                    results.Add(BehaviorParityCases.Project(Call("ListPlcTags", new JsonObject { ["plc"] = "PLC_1", ["table"] = "T" })));
                    results.Add(BehaviorParityCases.Project(Call("CreatePlcTag", BehaviorParityCases.Arguments(value))));
                }
                return new JsonObject { ["engineRelease"] = McpServer.ReleaseKey, ["results"] = results, ["waits"] = waits, ["writes"] = writes, ["previews"] = previews }.ToJsonString();
            }
            finally { McpServer.LeaveMcpApprovalContext(context); McpServer.ApprovalWaitOverrideForTests = wait;
                McpServer.ApprovalSessionKeyForTests = session; settings.Save(ApprovalSettings.SettingsPath); }
        }
        private static string RunStaging(string value)
        {
            using var fixture = new InfrastructureContractsTests();
            string bundle = Path.GetFullPath(Path.Combine("bin-build/P6-67r/parity-stage", Guid.NewGuid().ToString("N"))); Directory.CreateDirectory(bundle);
            var store = new ImportStagingStore(bundle, McpServer.ReleaseKey, Guid.NewGuid().ToString("N"));
            var catalog = new ToolCatalog(new[] { typeof(McpServer), typeof(ImportStagingTools) });
            McpServer.ConfigureToolBridge(catalog, () => false, new HashSet<string>());
            EngineServices.SetServiceProvider(new ServiceCollection().AddSingleton(new ImportStagingTools(store)).AddEngine(false, catalog).BuildServiceProvider());
            var settings = ApprovalSettings.Load(ApprovalSettings.SettingsPath); var wait = McpServer.ApprovalWaitOverrideForTests;
            bool context = McpServer.EnterMcpApprovalContext(); int approvals = 0;
            try
            {
                new ApprovalSettings(true, 1).Save(ApprovalSettings.SettingsPath);
                McpServer.ApprovalWaitOverrideForTests = (pending, _, _) => { approvals++; return Task.FromResult(new ApprovalOutcome(pending, false, value == "staging-refused" ? "denied" : null)); };
                var body = McpServer.ResultBody(McpServer.CallTool(value == "staging-cleanup" ? "CleanupStagedImportFiles" : "StageImportFiles", new ToolArguments(JsonSerializer.SerializeToElement(BehaviorParityCases.Arguments(value)))))!;
                return new JsonObject { ["engineRelease"] = McpServer.ReleaseKey, ["results"] = new JsonArray(BehaviorParityCases.Project(body)),
                    ["waits"] = approvals, ["writes"] = store.List()["batches"]!.AsArray().Count, ["previews"] = 0 }.ToJsonString();
            }
            finally { McpServer.LeaveMcpApprovalContext(context); McpServer.ApprovalWaitOverrideForTests = wait; settings.Save(ApprovalSettings.SettingsPath); Directory.Delete(bundle, true); }
        }
    }
}
