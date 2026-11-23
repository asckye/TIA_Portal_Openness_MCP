using System.Reflection;
using System.Text.Json;
using TiaMcp.Adapters.Contracts;
using TiaMcpServer.ModelContextProtocol;
using TiaMcpServer.Siemens.Services;
using Xunit;

namespace PlcExchangeTests;

public sealed class HmiDirectoryImportTests
{
    [Theory]
    [InlineData(true, 0, false)]
    [InlineData(true, 1, false)]
    [InlineData(true, 1, true)]
    [InlineData(false, 0, false)]
    [InlineData(false, 1, false)]
    [InlineData(false, 1, true)]
    public void Batch_reply_only_leaves_a_dispatched_unconfirmed_import_unknown(bool screens, int successfulFiles, bool unconfirmed)
    {
        string root = Directory.CreateTempSubdirectory().FullName;
        try
        {
            string tool = screens ? "ImportHmiScreensFromDirectory" : "ImportHmiTagTablesFromDirectory";
            string kind = screens ? "Hmi.Screen.Screen" : "Hmi.Tag.TagTable";
            string otherKind = screens ? "Hmi.Tag.TagTable" : "Hmi.Screen.Screen";
            var adapter = PlcExchangeTarget.Adapter;
            const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Static;
            var beforeImport = adapter.GetType("TiaMcp.Adapters.Native.Hmi.HmiExchangePolicy", true)!.GetMethod("BeforeImport", flags)!;
            var importFiles = adapter.GetType("TiaMcp.Adapters.Native.Hmi.HmiExchangeAdapter", true)!.GetMethod("ImportFiles", flags)!;
            int dispatched = 0, visited = 0;
            // The first failure in the screen batch stops enumeration. The tag-table batch continues.
            int fileCount = successfulFiles + 2;
            for (int i = 0; i < fileCount; i++) File.WriteAllText(Path.Combine(root, $"tagtable__默认变量表{i}.xml"), "");
            void Import(string file)
            {
                int index = visited++;
                bool fail = index >= successfulFiles;
                string xmlKind = !fail || unconfirmed && index == successfulFiles ? kind : otherKind;
                File.WriteAllText(file,
                    $"<Document><Engineering version='V15.1'/><{xmlKind} ID='0'><AttributeList><Name>默认变量表</Name></AttributeList></{xmlKind}></Document>");
                try { beforeImport.Invoke(null, [Array.Empty<object>(), file, kind, true, "overwrite=true"]); }
                catch (TargetInvocationException error) when (error.InnerException is PlcSoftwareException refusal) { throw refusal; }
                dispatched++;
                if (fail) throw new PlcSoftwareException("ImportFailed", "Native import did not return a confirmed result.");
            }
            var batch = Assert.IsType<HmiImportBatch>(importFiles.Invoke(null, [root, "", (Action<string>)Import, screens,
                (Func<bool>)(() => false), (Func<int>)(() => dispatched)]));
            int failures = screens ? 1 : 2;
            Assert.Equal(successfulFiles + failures, visited);
            Assert.Equal(successfulFiles + (unconfirmed ? 1 : 0), dispatched);
            Assert.Equal(unconfirmed, batch.Meta!.TryGetValue("writeOutcomeUnknown", out var unknown) && unknown is true);
            using var release = HardwareContract.UseRelease(PlcExchangeTarget.Release);
            var reply = new HmiReply { Value = batch, MayHaveChanged = dispatched > 0, OutcomeUnknown = unconfirmed };
            var service = new HmiExchangeService((_, _) => JsonSerializer.SerializeToNode(reply), () => "Fixture.ap15_1");
            var tools = new HmiExchangeTools(service);
            var body = HmiExchangeContract.Run(tool, true, true, () => screens
                ? tools.ImportHmiScreensFromDirectory("HMI_RT_1", "", root)
                : tools.ImportHmiTagTablesFromDirectory("HMI_RT_1", "", root)).StructuredContent!;
            Assert.Equal(unconfirmed ? "unknown" : successfulFiles > 0 ? "partial" : "rejected-before-operation", (string?)body["meta"]?["outcome"]);
            Assert.Equal(unconfirmed ? "unknown" : successfulFiles > 0 ? "partial" : "not-started", (string?)body["meta"]?["execution"]);
            Assert.Equal(successfulFiles, body["data"]!["imported"]!.AsArray().Count);
            Assert.Equal(failures, body["data"]!["failed"]!.AsArray().Count);
            var items = body["data"]!["items"]!.AsArray();
            Assert.Equal(visited, items.Count);
            foreach (var row in items.Skip(successfulFiles))
            {
                var child = row!["result"]!;
                bool itemUnknown = unconfirmed && (int?)row["index"] == successfulFiles;
                Assert.Equal(itemUnknown ? "OUTCOME_UNKNOWN" : "INVALID_ARGUMENT", (string?)child["error"]?["code"]);
                if (!itemUnknown) Assert.Contains("Cannot verify the import target identities for " + kind, (string?)child["error"]?["message"]);
                Assert.Equal(screens, (bool?)child["data"]?["evidence"]?["batchStopped"] == true);
            }
            if (unconfirmed) Assert.Equal("OUTCOME_UNKNOWN", (string?)body["error"]?["code"]);
            else if (successfulFiles > 0)
            {
                Assert.Equal("PARTIAL_FAILURE", (string?)body["error"]?["code"]);
                Assert.Equal(failures, (int?)body["error"]?["details"]?["failed"]);
            }
            else if (screens) Assert.Equal("INVALID_ARGUMENT", (string?)body["error"]?["code"]);
        }
        finally { Directory.Delete(root, true); }
    }
}
