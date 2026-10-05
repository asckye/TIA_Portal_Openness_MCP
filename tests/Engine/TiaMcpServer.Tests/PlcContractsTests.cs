using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ModelContextProtocol.Protocol;
using TiaMcp.Logic.V4;
using TiaMcp.Logic.V4.Domain;
using TiaMcp.Logic.V4.Inputs;
using TiaMcpServer.ModelContextProtocol;
using Xunit;

namespace TiaMcpServer.Tests
{
    public sealed class PlcContractsTests
    {
        private static JsonObject Body(CallToolResult result)
        {
            var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
            Assert.Equal(result.StructuredContent!.ToJsonString(), JsonNode.Parse(text)!.ToJsonString());
            var envelope = V4Json.Deserialize<Envelope>(text);
            Assert.Equal(!envelope.Ok, result.IsError);
            return result.StructuredContent!.AsObject();
        }

        [Theory]
        [InlineData("{\"success\":true}", false, "succeeded", "read-only", "complete")]
        [InlineData("{\"success\":true}", true, "succeeded", "completed", "complete")]
        [InlineData("{\"success\":true,\"dataComplete\":false}", false, "succeeded", "read-only", "partial")]
        [InlineData("{\"success\":false}", false, "read-failed", "read-only", "none")]
        [InlineData("{}", false, "read-failed", "read-only", "none")]
        [InlineData("{}", true, "unknown", "unknown", "unknown")]
        [InlineData("{\"success\":true,\"verified\":false}", true, "unknown", "unknown", "unknown")]
        [InlineData("{\"success\":false,\"effectiveState\":\"Unknown\"}", true, "unknown", "unknown", "unknown")]
        [InlineData("{\"success\":false,\"effectiveState\":\"Error\"}", true, "failed", "completed", "none")]
        [InlineData("{\"success\":false,\"status\":\"DocumentReadbackMismatch\"}", true, "failed", "completed", "none")]
        [InlineData("{\"success\":false,\"mayHaveChanged\":false}", true, "rejected-before-operation", "not-started", "none")]
        [InlineData("{\"success\":false,\"v4Rejection\":\"PROJECT_NOT_BOUND\"}", true, "rejected-before-operation", "not-started", "none")]
        [InlineData("{\"success\":false,\"tool\":\"ManagePlcUserGroup\",\"status\":\"InvalidState\"}", true, "rejected-before-operation", "not-started", "none")]
        public void MapsExplicitEvidence(string metadata, bool writes, string outcome, string execution, string completeness)
        {
            var result = PlcToolContract.Map("GetPlcBlockInfo", new ResponseMessage { Message = "Success is just message text", Meta = JsonNode.Parse(metadata)!.AsObject() }, writes, true);
            var body = Body(result);
            Assert.Equal(outcome, (string?)body["meta"]!["outcome"]);
            Assert.Equal(execution, (string?)body["meta"]!["execution"]);
            Assert.Equal(completeness, (string?)body["meta"]!["completeness"]);
            Assert.Equal(outcome == "unknown", (bool)body["meta"]!["requiresSessionReset"]!);
            Assert.Equal("current", (string?)body["meta"]!["behaviorPolicy"]);
            Assert.Contains(body["meta"]!["warnings"]!.AsArray(), w => (string?)w!["code"] == "UNVERIFIED_BEHAVIOR");
            Assert.Null(body["Message"]);
            Assert.Equal("Success is just message text", (string?)body["data"]!["summary"]);
        }

        [Fact]
        public void UnknownTakesPrecedenceOverEarlierSuccessfulWrites()
        {
            var result = PlcToolContract.Map("ImportPlcBlocksFromDirectory", new {
                Imported = new[] { "A" }, Failed = new[] { new { Path = "B", Error = "interrupted" } },
                Meta = new JsonObject { ["success"] = false }
            }, true, true);
            var body = Body(result);
            Assert.Equal("unknown", (string?)body["meta"]!["outcome"]);
            Assert.Equal("A", (string?)body["data"]!["imported"]![0]);
            Assert.Equal("B", (string?)body["data"]!["failed"]![0]!["path"]);
        }

        [Fact]
        public void KnownSkippedExportsRetainPartialEvidence()
        {
            var body = Body(PlcToolContract.Map("ExportPlcBlocks", new ResponseMessage {
                Meta = new JsonObject { ["success"] = true, ["totalBlocks"] = 3, ["exportedBlocks"] = 2 }
            }, true, true));
            Assert.Equal("partial", (string?)body["meta"]!["outcome"]);
            Assert.Equal("PARTIAL_FAILURE", (string?)body["error"]!["code"]);
            Assert.Equal(2, (int)body["error"]!["details"]!["succeeded"]!);
        }

        [Theory]
        [InlineData("[]")]
        [InlineData("\"[]\"")]
        [InlineData("[null]")]
        [InlineData("[{\"Action\":\"setBlockText\"}]")]
        [InlineData("[{\"action\":\"setBlockText\",\"field\":\"Title\",\"culture\":\"en-US\",\"expectedValue\":\"old\",\"value\":\"new\",\"extra\":1}]")]
        [InlineData("[{\"action\":\"setNetworkText\",\"networkIndex\":1.5,\"field\":\"Title\",\"culture\":\"en-US\",\"expectedValue\":\"old\",\"value\":\"new\"}]")]
        public void RejectsInvalidBlockEdits(string json)
            => Assert.NotNull(DomainValidation.Contract<BlockEdit[]>().Read(json, "changes").Error);

        [Theory]
        [InlineData("null")]
        [InlineData("\"{}\"")]
        [InlineData("{\"pollMs\":1.5}")]
        [InlineData("{\"pollMs\":2147483648}")]
        [InlineData("{\"PollMs\":1000}")]
        [InlineData("{\"pollMs\":1,\"pollMs\":2}")]
        [InlineData("{\"unknown\":true}")]
        public void RejectsInvalidMonitoringOptions(string json)
            => Assert.NotNull(DomainValidation.Contract<MonitoringOptions>().Read(json, "options").Error);

        [Fact]
        public void TypedValidationStopsBeforeTheLegacyOperation()
        {
            bool called = false;
            var result = PlcToolContract.Run("PlanOnlineReadOnlyMonitoring", false, false, () => {
                PlcToolContract.Input(NameListValidator.Create(new NameListPolicy(minimum: 1, trim: true)), new[] { " " }, "tagPaths");
                called = true;
                return new ResponseMessage();
            });
            Assert.False(called);
            Assert.Equal("INVALID_ARGUMENT", (string?)Body(result)["error"]!["code"]);
            Assert.Equal("not-started", (string?)Body(result)["meta"]!["execution"]);
        }

        [Fact]
        public void BlockEditLimitAndCanonicalRoundtripArePreserved()
        {
            const string change = "{\"action\":\"setBlockText\",\"field\":\"Title\",\"culture\":\"en-US\",\"expectedValue\":\"old\",\"value\":\"new\"}";
            var contract = DomainValidation.Contract<BlockEdit[]>();
            var valid = contract.Read("[" + change + "]", "changes");
            Assert.Null(valid.Error);
            Assert.IsType<BlockTextEdit>(Assert.Single(valid.Value!));
            Assert.Null(contract.Read(V4Json.Serialize(valid.Value), "changes").Error);
            Assert.Equal(ErrorCode.LimitExceeded, contract.Read("[" + string.Join(",", Enumerable.Repeat(change, 101)) + "]", "changes").Error!.Code);
        }

        [Fact]
        public void ConfirmedImportMismatchIsFailedAndRetainsWriteEvidence()
        {
            var body = Body(PlcToolContract.Failure("ImportPlcBlock", new PlcBlockVerificationException("fixture"), true, true));
            Assert.Equal("failed", (string?)body["meta"]!["outcome"]);
            Assert.True((bool)body["data"]!["importReturned"]!);
            Assert.Equal("mismatch", (string?)body["data"]!["verification"]);
        }

        [Fact]
        public void NestedCompilerFailureDoesNotEraseTheImportedBlock()
        {
            var body = Body(PlcToolContract.Map("RepairAndReimportPlcBlock", new {
                Imported = true, Meta = new JsonObject { ["success"] = false },
                Compile = new { State = "Error", Meta = new JsonObject { ["success"] = false, ["effectiveState"] = "Error" } }
            }, true, true));
            Assert.Equal("partial", (string?)body["meta"]!["outcome"]);
            Assert.True((bool)body["data"]!["imported"]!);
            Assert.Equal("Error", (string?)body["data"]!["compile"]!["state"]);
        }

        [Fact]
        public void ReadPagingAndIncompleteEvidenceRemainSeparate()
        {
            var body = Body(PlcToolContract.Map("GetPlcTagTableConstants", new ResponseMessage {
                Meta = new JsonObject { ["success"] = true, ["dataComplete"] = false,
                    ["data"] = new JsonObject { ["offset"] = 0, ["limit"] = 2, ["total"] = 3 } }
            }, false, true));
            Assert.Equal(2, (int)body["meta"]!["paging"]!["nextOffset"]!);
            Assert.False((bool)body["meta"]!["paging"]!["complete"]!);
            Assert.Equal("partial", (string?)body["meta"]!["completeness"]);
        }

        [Fact]
        public void CurrentNamesHaveOneRegistrationAndCompilationScopesRemainSeparate()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "AGENTS.md"))) dir = dir.Parent;
            Assert.NotNull(dir);
            var files = new[] { "PlcBlocksTools.cs", "PlcSoftwareTools.cs", "PlcTablesTools.cs", "TypesTools.cs" };
            var sources = files.Select(f => File.ReadAllText(Path.Combine(dir!.FullName, "src", "Engine", "ModelContextProtocol", "Tools", f))).ToArray();
            var names = sources.SelectMany(s => Regex.Matches(s, "McpServerTool\\(Name\\s*=\\s*\"([^\"]+)\"").Cast<Match>().Select(m => m.Groups[1].Value)).ToArray();
            string[] expected = {
            "GetPlcBlockInfo",
            "ListPlcBlocks",
            "GetPlcBlockHierarchy",
            "ExportPlcBlock",
            "ImportPlcBlock",
            "ImportPlcBlocksFromDirectory",
            "ImportPlcProgramFromDirectory",
            "CompilePlcDiagnostics",
            "RepairAndReimportPlcBlock",
            "ExportPlcBlocks",
            "DescribePlcBlockLogic",
            "ManagePlcBlockProtection",
            "ManagePlcDataBlockSnapshot",
            "SetPlcProgram",
            "GetPlcBlockFingerprints",
            "GetPlcBlockEditCapabilities",
            "AnalyzePlcReferences",
            "PatchPlcBlockDocument",
            "ImportPlcBlockVerified",
            "DeletePlcBlock",
            "DeletePlcTagTable",
            "DeletePlcType",
            "CreatePlcTypeGroup",
            "DeleteEmptyPlcBlockGroup",
            "CreatePlcBlockGroup",
            "MovePlcBlockToGroup",
            "ManagePlcUserGroup",
            "GetSoftwareInfo",
            "CompilePlcSoftware",
            "GetSoftwareTree",
            "ListPlcTagTables",
            "ExportPlcTagTable",
            "ImportPlcTagTable",
            "ImportPlcTagTablesFromDirectory",
            "ListPlcWatchTables",
            "ListPlcForceTables",
            "SetPlcWatchTableModifyValue",
            "ExportPlcWatchTable",
            "ExportPlcWatchTablesToDirectory",
            "ProbePlcMonitorOnlineCapabilities",
            "GetPlcWatchTableCurrentValuesReadOnly",
            "PlanOnlineReadOnlyMonitoring",
            "PlanOnlineReadOnlyDataProvider",
            "MonitorPlcWatchTableS7",
            "ImportPlcWatchTableOffline",
            "GetPlcTagTableConstants",
            "ManagePlcTableEntries",
            "GetPlcTypeInfo",
            "ListPlcTypes",
            "ExportPlcType",
            "ImportPlcType",
            "SeedProjectFromReference",
            "ExportPlcTypes",
            };
            Assert.Equal(expected.OrderBy(n => n), names.OrderBy(n => n));
            Assert.Equal(53, names.Distinct().Count());
            Assert.Contains("PlcCompilation.CompileAndDiagnoseCore(softwarePath, password)", sources[0]);
            Assert.Contains("_session.CompileSoftware(softwarePath, password)", sources[1]);
        }
    }
}
