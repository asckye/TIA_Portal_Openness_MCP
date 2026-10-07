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
        private static BatchReplacementPolicyTests.AdmissionFixture? batchFixture;
        public static class Probe
        {
            [McpServerTool(Name = "CreatePlcTag"), ToolClassification("L1", "PLC-Software", "WRITE", batchWrite: true)]
            public static CallToolResult Write(string plc, string table, string name, string dataType, string address,
                bool dryRun = true, bool confirm = false, string expectedProjectFile = "")
            {
                if (dryRun) previews++; else writes++;
                if (scenario.StartsWith("single-", StringComparison.Ordinal))
                {
                    var data = BehaviorParityCases.SingleBackup(scenario, dryRun);
                    var baseResult = !dryRun && scenario == "single-import-failed" ? McpServer.TargetFailure("CreatePlcTag", new IOException("Fixture native interruption."), true) : McpServer.V4Result("CreatePlcTag", data, completed: !dryRun);
                    var body = McpServer.ResultBody(baseResult)!.AsObject();
                    body["data"] = data;
                    if (TiaMcp.Logic.V4.HostBehavior.BackupSkipped(data) is TiaMcp.Logic.V4.Warning backup)
                        body["meta"]!["warnings"]!.AsArray().Add(JsonNode.Parse(TiaMcp.Logic.V4.V4Json.Serialize(backup)));
                    if (!dryRun && scenario == "single-native-warning") body["meta"]!["warnings"]!.AsArray().Add(new JsonObject { ["code"] = "NATIVE_WARNING", ["message"] = "Native diagnostics include warnings; see data.warnings.", ["details"] = new JsonObject() });
                    return new CallToolResult { IsError = baseResult.IsError, StructuredContent = body, Content = new[] { new TextContentBlock { Text = body.ToJsonString() } } };
                }
                BehaviorParityCases.ExportAdmission(scenario);
                if (scenario.StartsWith("blocked-export", StringComparison.Ordinal))
                {
                    var data = new JsonObject { ["kind"] = "technology-object", ["status"] = "inconsistent", ["planHash"] = new string('a',64) };
                    TiaMcp.Logic.V4.HostBehavior.ExportPreview(data);
                    NativeExportPolicy.RequireApply("inconsistent");
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
            [McpServerTool(Name = "ImportPlcBlock"), ToolClassification("L1", "PLC-Software", "WRITE", batchWrite: true)]
            public static CallToolResult LegacyImport(string softwarePath, string groupPath, string importPath)
            {
                if (McpServer.IsReadOnlyApprovalPreview) previews++; else writes++;
                BehaviorParityCases.SingleBackup(scenario, McpServer.IsReadOnlyApprovalPreview);
                return McpServer.V4Result("ImportPlcBlock", new JsonObject { ["executed"] = !McpServer.IsReadOnlyApprovalPreview });
            }
            [McpServerTool(Name="ImportPlcType"),ToolClassification("L1","PLC-Software","WRITE",batchWrite:true)]
            public static CallToolResult LegacyType(string softwarePath,string groupPath,string importPath) => LegacyImport(softwarePath,groupPath,importPath);
            [McpServerTool(Name="ImportPlcTagTable"),ToolClassification("L1","PLC-Software","WRITE",batchWrite:true)]
            public static CallToolResult LegacyTable(string softwarePath,string folderPath,string importPath) => LegacyImport(softwarePath,folderPath,importPath);
            [McpServerTool(Name = "ImportPlcExternalSource"), ToolClassification("L1", "PLC-Software", "WRITE", batchWrite: true)]
            public static CallToolResult Import(string softwarePath, string groupPath, string filePath, bool dryRun = true)
                => McpServer.V4Result("ImportPlcExternalSource", new JsonObject());
            [McpServerTool(Name = "ImportPlcBlocksFromDirectory"), ToolClassification("L1", "PLC-Software", "WRITE", batchWrite: true)]
            public static CallToolResult ImportDirectory(string softwarePath, string groupPath, string dir, bool dryRun = true, bool overwrite = false,
                string[]? importOrder = null, string expectedPlanHash = "", bool confirm = false, string expectedProjectFile = "")
            {
                if (batchFixture != null) return BatchApply("ImportPlcBlocksFromDirectory", new JsonObject {
                    ["dryRun"]=dryRun,["confirm"]=confirm,["expectedPlanHash"]=expectedPlanHash,["expectedProjectFile"]=expectedProjectFile,["importOrder"]=JsonSerializer.SerializeToNode(importOrder) });
                if (dryRun) previews++; else writes++;
                if (scenario is "batch-inconsistent" or "batch-protected" or "batch-unknown-consistency")
                {
                    var envelope = TiaMcp.Logic.V4.PlcBatchImportResultMapping.Result(BehaviorParityCases.BatchData(scenario), McpServer.ReleaseKey, "ImportPlcBlocksFromDirectory", "fixture", dryRun);
                    var wire = TiaMcp.Logic.V4.McpResult.From(envelope);
                    return new CallToolResult { IsError = wire.IsError, StructuredContent = JsonNode.Parse(wire.StructuredContent.GetRawText()), Content = new[] { new TextContentBlock { Text = wire.Content[0].Text } } };
                }
                return McpServer.V4Result("ImportPlcBlocksFromDirectory", new JsonObject { ["projectFile"] = "C:/fixture.ap19", ["planHash"] = new string('a', 64) });
            }
            [McpServerTool(Name = "ImportPlcProgramFromDirectory"), ToolClassification("L1", "PLC-Software", "WRITE", batchWrite: true)]
            public static CallToolResult ImportProgram(string softwarePath,string sourceDir,string blockGroupPath="",bool dryRun=true,bool overwrite=false,
                string[]? importOrder=null,string expectedPlanHash="",bool confirm=false,string expectedProjectFile="",bool compileAfter=false,bool stopOnImportFailure=true,string technologyFolderPath="")
                => BatchApply("ImportPlcProgramFromDirectory",new JsonObject { ["dryRun"]=dryRun,["confirm"]=confirm,["expectedPlanHash"]=expectedPlanHash,["expectedProjectFile"]=expectedProjectFile,
                    ["importOrder"]=JsonSerializer.SerializeToNode(importOrder),["compileAfter"]=compileAfter,["stopOnImportFailure"]=stopOnImportFailure,["technologyFolderPath"]=technologyFolderPath });
            private static CallToolResult BatchApply(string tool,JsonObject args)
            {
                var data=JsonSerializer.SerializeToNode(batchFixture!.Execute(args),new JsonSerializerOptions { PropertyNamingPolicy=JsonNamingPolicy.CamelCase })!.AsObject();
                var result=TiaMcp.Logic.V4.McpResult.From(TiaMcp.Logic.V4.PlcBatchImportResultMapping.Result(data,McpServer.ReleaseKey,tool,"fixture",false));
                return new CallToolResult { IsError=result.IsError,StructuredContent=JsonNode.Parse(result.StructuredContent.GetRawText()),Content=new[]{new TextContentBlock { Text=result.Content[0].Text }} };
            }
            [McpServerTool(Name = "CompilePlcSoftware"), ToolClassification("L1", "PLC-Software", "EXECUTE")]
            public static CallToolResult Compile(string softwarePath, bool dryRun = true)
            {
                if (dryRun) { previews++; return McpServer.V4Result("CompilePlcSoftware", new JsonObject { ["executed"] = false }); }
                writes++; var data = BehaviorParityCases.CompileData(); if (scenario == "compile-zero-count") data["errorCount"] = 0;
                return PlcToolContract.Map("CompilePlcSoftware", data, true, true);
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
            if (value.StartsWith("disabled-",StringComparison.Ordinal)) return RunDisabledBatch(value);
            bool enabled=!value.EndsWith("-disabled",StringComparison.Ordinal);
            if(!enabled) value=value.Substring(0,value.Length-9);
            if (value.StartsWith("staging-", StringComparison.Ordinal)) return RunStaging(value,enabled);
            using var fixture = new InfrastructureContractsTests();
            scenario = value; writes = previews = waits = 0;
            McpServer.ConfigureToolBridge(new ToolCatalog(new[] { typeof(McpServer), typeof(Probe) }), () => false, new HashSet<string>());
            var settings = ApprovalSettings.Load(ApprovalSettings.SettingsPath);
            var wait = McpServer.ApprovalWaitOverrideForTests; var session = McpServer.ApprovalSessionKeyForTests;
            bool context = McpServer.EnterMcpApprovalContext(); var key = new object();
            try
            {
                new ApprovalSettings(enabled, 1).Save(ApprovalSettings.SettingsPath);
                McpServer.ApprovalSessionKeyForTests = () => key;
                McpServer.ApprovalWaitOverrideForTests = (pending, _, _) => { waits++; return Task.FromResult(new ApprovalOutcome(pending, !enabled,
                    scenario == "refused-approval" ? "denied" : null)); };
                JsonNode Call(string tool, JsonObject args) => McpServer.ResultBody(McpServer.CallTool(tool,
                    new ToolArguments(JsonSerializer.SerializeToElement(args))))!;
                var results = new JsonArray(BehaviorParityCases.Project(Call(value=="single-type-group-missing" ? "ImportPlcType" : value=="single-table-group-missing" ? "ImportPlcTagTable" : value == "single-legacy-group-missing" ? "ImportPlcBlock" : value.StartsWith("compile-", StringComparison.Ordinal) ? "CompilePlcSoftware" : value == "native-read" ? "ListPlcTags"
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
        private static string RunDisabledBatch(string value)
        {
            using var infrastructure=new InfrastructureContractsTests();
            bool program=value.StartsWith("disabled-program-",StringComparison.Ordinal);
            using var fixture=new BatchReplacementPolicyTests.AdmissionFixture(value.Substring(program ? 17 : 15),program);
            batchFixture=fixture;scenario="followup";writes=previews=waits=0;
            McpServer.ConfigureToolBridge(new ToolCatalog(new[]{typeof(McpServer),typeof(Probe)}),()=>false,new HashSet<string>());
            var settings=ApprovalSettings.Load(ApprovalSettings.SettingsPath);var previousKey=McpServer.ApprovalSessionKeyForTests;var wait=McpServer.ApprovalWaitOverrideForTests;
            bool context=McpServer.EnterMcpApprovalContext();var key=new object();
            try
            {
                new ApprovalSettings(false,1).Save(ApprovalSettings.SettingsPath);McpServer.ApprovalSessionKeyForTests=()=>key;
                McpServer.ApprovalWaitOverrideForTests=(pending,current,_)=> { if(current.Enabled) throw new Exception("Fixture must disable approval.");return Task.FromResult(new ApprovalOutcome(pending,true,null)); };
                JsonNode Call(string tool,JsonObject args)=>McpServer.ResultBody(McpServer.CallTool(tool,new ToolArguments(JsonSerializer.SerializeToElement(args))))!;
                var body=Call(program ? "ImportPlcProgramFromDirectory" : "ImportPlcBlocksFromDirectory",fixture.Arguments());
                var results=new JsonArray(BehaviorParityCases.Project(body));
                results.Add(BehaviorParityCases.Project(Call("ListPlcTags",new JsonObject { ["plc"]="PLC_1",["table"]="T" })));
                results.Add(BehaviorParityCases.Project(Call("CreatePlcTag",BehaviorParityCases.Arguments("followup"))));
                return new JsonObject { ["engineRelease"]=McpServer.ReleaseKey,["results"]=results,["calls"]=JsonSerializer.SerializeToNode(fixture.Calls) }.ToJsonString();
            }
            finally { McpServer.LeaveMcpApprovalContext(context);McpServer.ApprovalSessionKeyForTests=previousKey;McpServer.ApprovalWaitOverrideForTests=wait;settings.Save(ApprovalSettings.SettingsPath);batchFixture=null; }
        }
        private static string RunStaging(string value,bool enabled=true)
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
                new ApprovalSettings(enabled, 1).Save(ApprovalSettings.SettingsPath);
                McpServer.ApprovalWaitOverrideForTests = (pending, _, _) => { approvals++; return Task.FromResult(new ApprovalOutcome(pending, !enabled, value == "staging-refused" ? "denied" : null)); };
                var args = BehaviorParityCases.StagingArguments(value, store, bundle, McpServer.ReleaseKey);
                var body = McpServer.ResultBody(McpServer.CallTool(BehaviorParityCases.StagingTool(value), new ToolArguments(JsonSerializer.SerializeToElement(args))))!;
                return new JsonObject { ["engineRelease"] = McpServer.ReleaseKey, ["results"] = new JsonArray(BehaviorParityCases.Project(body)),
                    ["waits"] = approvals, ["writes"] = store.List()["batches"]!.AsArray().Count, ["previews"] = 0 }.ToJsonString();
            }
            finally { McpServer.LeaveMcpApprovalContext(context); McpServer.ApprovalWaitOverrideForTests = wait; settings.Save(ApprovalSettings.SettingsPath); Directory.Delete(bundle, true); }
        }
    }
}
