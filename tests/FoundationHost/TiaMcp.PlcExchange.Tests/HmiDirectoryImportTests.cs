using System.Reflection;
using System.Text.Json;
using TiaMcp.Adapters.Contracts;
using TiaMcpServer.ModelContextProtocol;
using TiaMcpServer.Siemens.Services;
using Xunit;

namespace PlcExchangeTests;

public sealed class HmiDirectoryImportTests
{
    public class ImportSession : DispatchProxy
    {
        public object Software { get; set; } = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => method?.Name switch
        {
            "IsProjectNull" => false,
            "GetHmiImportSoftwareReader" => (Func<object?>)(() => Software),
            _ => throw new InvalidOperationException("Unexpected native session call: " + method?.Name)
        };
    }

    public sealed class ThrowingCollection<TOptions>(List<string> calls)
    {
        public void Import(FileInfo file, TOptions options)
        {
            calls.Add(file.FullName);
            throw new IOException("Native Import entered without confirmation.");
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public async Task Worker_stops_after_the_native_collection_throws(bool screens)
    {
        string root = Directory.CreateTempSubdirectory().FullName;
        string release = PlcExchangeTarget.Release;
        string relative = release == "14sp1" ? "TIA_V14SP1_PublicAPI/V14 SP1" : $"TIA_V{release}_PublicAPI/V{release}";
        string api = Path.Combine(Environment.GetEnvironmentVariable("TIA_MCP_TEST_PUBLIC_API_ROOT")!, relative);
        if (release == "21") api = Path.Combine(api, "net48");
        Assembly? Resolve(object? sender, ResolveEventArgs args)
        {
            var name = new AssemblyName(args.Name).Name!;
            var path = Path.Combine(api, name + ".dll");
            return name.StartsWith("Siemens.Engineering", StringComparison.Ordinal) && File.Exists(path) ? Assembly.LoadFrom(path) : null;
        }
        AppDomain.CurrentDomain.AssemblyResolve += Resolve;
        try
        {
            string kind = screens ? "Hmi.Screen.Screen" : "Hmi.Tag.TagTable";
            var files = new[] { "match-A", "match-B", "match-C", "unselected" }.Select(name => Path.Combine(root, name + ".xml")).ToArray();
            foreach (string file in files) File.WriteAllText(file,
                $"<Document><Engineering version='V15.1'/><{kind} ID='0'><AttributeList><Name>{Path.GetFileNameWithoutExtension(file)}</Name></AttributeList></{kind}></Document>");
            var calls = new List<string>();
            HmiReply InvokeWorker()
            {
                var core = Assembly.LoadFrom(Path.Combine(api, release == "21" ? "Siemens.Engineering.Base.dll" : "Siemens.Engineering.dll"));
                var collectionType = typeof(ThrowingCollection<>).MakeGenericType(core.GetType("Siemens.Engineering.ImportOptions", true)!);
                var collection = Activator.CreateInstance(collectionType, calls)!;
                var software = new { ScreenFolder = new { Screens = collection }, TagTables = collection };
                var engineType = PlcExchangeTarget.Adapter.GetType("TiaMcp.Adapters.PlcFoundationEngine", true)!;
                using var engine = (IDisposable)Activator.CreateInstance(engineType, release, api)!;
                var port = engineType.GetProperty("EngineeringPlcOrganisationSession")!;
                var session = (ImportSession)DispatchProxy.Create(port.PropertyType, typeof(ImportSession));
                session.Software = software;
                port.SetValue(engine, session);
                engineType.GetProperty("ResolveEngineeringHmiSoftware")!.SetValue(engine, (Func<string, object>)(_ => software));
                var request = new HmiRequest { Operation = screens ? "ImportHmiScreensFromDirectory" : "ImportHmiTagTablesFromDirectory",
                    SoftwarePath = "HMI_RT_1", Dir = root, RegexName = "^match-", Overwrite = true, DryRun = false };
                return (HmiReply)engineType.GetMethod("ExecuteHmi")!.Invoke(engine, [request, false])!;
            }
            var result = new TaskCompletionSource<HmiReply>(TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(() => {
                try { result.SetResult(InvokeWorker()); }
                catch (Exception error) { result.SetException(error); }
            });
            thread.SetApartmentState(release is "20" or "21" ? ApartmentState.MTA : ApartmentState.STA);
            thread.Start();
            var reply = await result.Task;
            Assert.Null(reply.Failure);
            var attempted = Assert.Single(calls);
            Assert.Contains(attempted, files.Take(3));
            Assert.True(reply.MayHaveChanged);
            Assert.True(reply.OutcomeUnknown);
            var batch = Assert.IsType<HmiImportBatch>(reply.Value);
            Assert.Empty(batch.Imported);
            Assert.Equal(attempted, Assert.Single(batch.Failed).Path);
            var meta = JsonSerializer.SerializeToNode(batch.Meta)!;
            var row = Assert.Single(meta["items"]!.AsArray())!;
            Assert.Equal("ImportUnconfirmed", (string?)row["evidence"]?["status"]);
            Assert.True((bool?)row["evidence"]?["batchStopped"]);
            Assert.Contains("Native Import entered without confirmation.", (string?)row["evidence"]?["nativeMessage"]);
            Assert.Equal(files.Take(3).Except(calls).Order(), meta["notAttempted"]!.AsArray().Select(path => (string)path!).Order());
        }
        finally
        {
            AppDomain.CurrentDomain.AssemblyResolve -= Resolve;
            Directory.Delete(root, true);
        }
    }

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
            // Tag-table admission refusals allow the next file. An unconfirmed import stops both tools.
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
            int failures = screens || unconfirmed ? 1 : 2;
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
                Assert.Equal(screens || itemUnknown, (bool?)child["data"]?["evidence"]?["batchStopped"] == true);
            }
            if (unconfirmed)
            {
                Assert.Equal("OUTCOME_UNKNOWN", (string?)body["error"]?["code"]);
                var pending = Assert.Single(body["data"]!["evidence"]!["notAttempted"]!.AsArray());
                Assert.DoesNotContain(items, row => (string?)row!["target"] == (string?)pending);
            }
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
