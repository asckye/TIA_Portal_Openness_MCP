using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using ModelContextProtocol.Protocol;
using TiaMcp.Logic.V4;
using TiaMcpServer.ModelContextProtocol;
using TiaMcpServer.Siemens;
using Xunit;

namespace TiaMcp.Engine.Tests
{
    public sealed class P707bTests
    {
        private static JsonObject Body(CallToolResult result) => result.StructuredContent!.AsObject();

        [Fact]
        public void SaveAsUsesTheSameSessionClassificationAsSaveAndClose()
        {
            foreach (string tool in new[] { "SaveProject", "SaveProjectCopy", "CloseProject" })
                Assert.Equal("SESSION", ToolMetadata.Find(tool)!.Operation);
            Assert.Equal("SESSION", ToolTaxonomy.OperationOf("SaveProjectCopy", "").Operation);
        }

        [Theory]
        [InlineData("Error", false)]
        [InlineData("Failure", false)]
        [InlineData("Failed", false)]
        [InlineData("Error", true)]
        [InlineData("Failure", true)]
        public void ReturnedNativeFailuresPreserveEvidenceAcrossContracts(string state, bool changesProject)
        {
            string directory = Path.Combine(Path.GetTempPath(), "p707b-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                foreach (bool fileExists in new[] { false, true })
                {
                    string path = Path.Combine(directory, "target.xlsx");
                    if (fileExists) File.WriteAllBytes(path, new byte[] { 1, 2, 3 });
                    var evidence = new JsonObject { ["success"] = false, ["mayHaveWrittenFiles"] = !changesProject };
                    NativeResultState.Record(evidence, state, changesProject, "native.log", path, new JsonArray("native message"));
                    var response = new ResponseMessage { Meta = evidence };
                    foreach (var result in new[] {
                        EngineeringToolContract.Map("ExportAlarmInstanceTexts", response, true),
                        LibraryToolContract.Map("ExchangeSivarcScreenLayouts", response, true),
                        OptionalPackageContract.Map("ExchangeCfcCharts", response, true),
                        RuntimeToolContract.Map("DownloadPlcToFolder", response, false, true),
                        PlcToolContract.Map("ManagePlcBlockDocuments", response, true, true) })
                    {
                        var body = Body(result);
                        Assert.Equal(changesProject ? "OUTCOME_UNKNOWN" : "NATIVE_OPERATION_FAILED", (string?)body["error"]?["code"]);
                        Assert.Equal(changesProject, (bool?)body["meta"]?["requiresSessionReset"]);
                        var native = body["error"]!["details"]!["evidence"]!;
                        Assert.Equal(state, (string?)native["nativeState"]);
                        Assert.Equal("native.log", (string?)native["logFilePath"]);
                        Assert.Equal(fileExists, (bool?)native["targetFile"]?["exists"]);
                        Assert.Equal(fileExists ? 3 : (long?)null, (long?)native["targetFile"]?["sizeBytes"]);
                        Assert.Equal("native message", (string?)native["nativeMessages"]?[0]);
                    }
                }
            }
            finally { Directory.Delete(directory, true); }
        }

        [Theory]
        [InlineData("Success")]
        [InlineData("Warning")]
        [InlineData("Information")]
        [InlineData("Unknown")]
        [InlineData(null)]
        public void SuccessWarningsAndMissingResultsAreNotExplicitFailures(string? state)
        {
            var evidence = new JsonObject();
            NativeResultState.Record(evidence, state, true);
            Assert.False(NativeResultState.TryFailure(evidence, true, out _, out _));
            Assert.False(NativeResultState.TryFailure(new JsonObject { ["nativeState"] = "Error", ["nativeCompleted"] = true }, true, out _, out _));
        }

        [Theory]
        [InlineData("OB", false)]
        [InlineData("DB", false)]
        [InlineData("unknown", false)]
        [InlineData("FB", true)]
        [InlineData("FC", true)]
        public void SivarcRejectsUnsupportedClassesBeforeProviderAccess(string blockClass, bool supported)
        {
            int calls = 0;
            object ReadProvider() { calls++; return new object(); }
            if (supported) Assert.NotNull(SivarcLogic.BlockProvider(blockClass, ReadProvider));
            else Assert.Throws<NotSupportedException>(() => SivarcLogic.BlockProvider(blockClass, ReadProvider));
            Assert.Equal(supported ? 1 : 0, calls);
        }

        [Theory]
        [InlineData(20)]
        [InlineData(21)]
        public void CfcGuardDoesNotRunUnsafeInventoryOrChartCalls(int major)
        {
            foreach (string action in new[] { "read", "add", "change", "remove", "export", "selectiveExport", "exportInstructionData" })
                Assert.Throws<NotSupportedException>(() => CfcLogic.RequireSafeChartInventory(major, action));
        }

        [Fact]
        public async Task ExistingNonRepositoryIsRefusedAndSubdirectoryPreviewRemainsSupported()
        {
            string directory = Path.Combine(Path.GetTempPath(), "p707b-git-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                var tool = new GitWorkflowTools();
                var body = Body(await tool.ManagePlcGitRepositoryV4(directory));
                Assert.Equal("INVALID_ARGUMENT", (string?)body["error"]?["code"]);
                Directory.CreateDirectory(Path.Combine(directory, ".git"));
                string child = Directory.CreateDirectory(Path.Combine(directory, "child")).FullName;
                body = Body(await tool.ManagePlcGitRepositoryV4(child, "stage", new[] { "selected.xml" }, dryRun: true));
                Assert.True((bool)body["ok"]!);
                Assert.False((bool)body["data"]!["executed"]!);
            }
            finally { Directory.Delete(directory, true); }
        }

        [Fact]
        public void JournalReaderMergesUtcAcrossRotationsAndCountsMalformedRows()
        {
            string directory = Path.Combine(Path.GetTempPath(), "p707b-journal-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                File.WriteAllText(Path.Combine(directory, "calls-one.jsonl.previous"), "{\"utc\":\"2026-10-09T01:00:00Z\",\"phase\":\"BEFORE\"}\npartial\n42\n");
                File.WriteAllText(Path.Combine(directory, "calls-two.jsonl"), "{\"utc\":\"2026-10-09T00:00:00Z\",\"phase\":\"RETURNED\"}\n");
                var body = TiaMcp.Logic.ModelContextProtocol.NativeJournalReader.Read(directory, 1);
                Assert.Equal("BEFORE", (string?)body["records"]?[0]?["phase"]);
                Assert.Equal(2, (int?)body["malformedLines"]);
                Assert.Equal(2, (int?)body["filesRead"]);
                Assert.Throws<ArgumentException>(() => TiaMcp.Logic.ModelContextProtocol.NativeJournalReader.Read(directory, 0));
                Assert.Throws<ArgumentException>(() => TiaMcp.Logic.ModelContextProtocol.NativeJournalReader.Read(directory, 501));
            }
            finally { Directory.Delete(directory, true); }
        }

        [Theory]
        [InlineData("status")]
        [InlineData("history")]
        [InlineData("diff")]
        [InlineData("show")]
        [InlineData("stage")]
        [InlineData("commit")]
        public async Task MissingGitRepositoryIsRefusedForEveryAction(string action)
        {
            var tool = new GitWorkflowTools();
            string missing = Path.Combine(Path.GetTempPath(), "missing-p707b-" + Guid.NewGuid().ToString("N"));
            foreach (bool preview in new[] { false, true })
            {
                var body = Body(await tool.ManagePlcGitRepositoryV4(missing, action, dryRun: preview));
                Assert.Equal("NOT_FOUND", (string?)body["error"]?["code"]);
                Assert.Contains("repositoryPath", (string?)body["error"]?["message"]);
                Assert.Equal("not-started", (string?)body["meta"]?["execution"]);
                Assert.False((bool)body["meta"]!["requiresSessionReset"]!);
                body = Body(await tool.ManagePlcGitRepositoryV4("relative-path", action, dryRun: preview));
                Assert.Equal("INVALID_ARGUMENT", (string?)body["error"]?["code"]);
                Assert.Equal("repositoryPath", (string?)body["error"]?["details"]?["parameter"]);
            }
        }
    }
}
