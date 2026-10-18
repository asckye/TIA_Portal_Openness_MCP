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
        [InlineData(18)]
        [InlineData(19)]
        [InlineData(20)]
        [InlineData(21)]
        public void CfcGuardDoesNotRunUnsafeInventoryOrChartCalls(int major)
        {
            foreach (string action in new[] { "read", "add", "change", "remove", "export", "selectiveExport", "exportInstructionData" })
                Assert.Throws<NotSupportedException>(() => TiaMcp.Adapters.Contracts.CfcSafetyPolicy.RequireSafeChartInventory(major, action));
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
    }
}
