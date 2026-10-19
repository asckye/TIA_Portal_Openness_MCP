extern alias foundationhost;
extern alias enginehost;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using TiaMcp.FoundationHost;

internal static class WorkerClientTests
{
    internal static async Task Run(Action<bool,string> Check)
    {
        using(var disabled=new WorkerClient("17","intentionally-nonexistent.exe","intentionally-nonexistent-api",false))
        {
            try { await disabled.Call("Attach",new JsonObject { ["processId"]=123 },CancellationToken.None); throw new Exception("Native default gate failed"); }
            catch(InvalidOperationException ex) { Check(ex.Message.StartsWith("Native calls are disabled"),"Discovery denies before any process/file access"); }
        }
        using(var bundled=new WorkerClient("17","intentionally-nonexistent.exe","intentionally-nonexistent-api",false))
        using(var fixture=new WorkerClient("17","intentionally-nonexistent.exe","intentionally-nonexistent-api",false) { Bundled=false })
        {
            var gated=FoundationTools.Create(bundled,"17").OfType<FoundationTool>().Where(t=>t.RequiresTia).ToArray();
            Check(gated.Length>0 && gated.All(t=>t.UsesProductionWorker),"Bundled worker tools keep the pre-dispatch readiness gate");
            Check(!FoundationTools.Create(fixture,"17").OfType<FoundationTool>().Any(t=>t.UsesProductionWorker),"An explicit --worker-exe fixture skips the bundled-worker readiness gate");
        }
    }
}

public sealed class WorkerDispatchTests
{
    [Xunit.Theory]
    [Xunit.InlineData(false, false)] [Xunit.InlineData(true, false)]
    [Xunit.InlineData(false, true)] [Xunit.InlineData(true, true)]
    public async Task Both_clients_journal_the_served_tool_and_host_request_id(bool engine, bool observed)
    {
        string exe = Path.Combine(AppContext.BaseDirectory, "TiaMcp.FoundationHost.Tests.exe");
        string release = engine ? "21" : "19";
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "TiaMcp.Adapter." + release + ".dll"), "offline session fixture");
        var rows = new List<string>();
        using IFoundationWorker? plc = engine ? null : new WorkerClient(release, exe, AppContext.BaseDirectory, true) { Bundled = false };
        using var worker = engine ? new foundationhost::TiaMcp.FoundationHost.EngineWorkerClient(
            new foundationhost::TiaMcp.FoundationHost.HostOptions { ReleaseKey = release, BundleRoot = AppContext.BaseDirectory,
                WorkerExe = exe, ApiDirectory = AppContext.BaseDirectory, EngineWorkerExe = exe, NativeEnabled = true, BundledWorker = false },
            foundationhost::TiaMcp.FoundationHost.EngineWorkerClient.Hash(exe)) : null;
        TiaMcpServer.ModelContextProtocol.InvocationJournal.ConfigureOutput(rows.Add);
        enginehost::TiaMcpServer.ModelContextProtocol.InvocationJournal.ConfigureOutput(rows.Add);
        var previousBinding = TiaMcpServer.ModelContextProtocol.InvocationJournal.BindingSnapshot;
        var previousEngineBinding = enginehost::TiaMcpServer.ModelContextProtocol.InvocationJournal.BindingSnapshot;
        TiaMcpServer.ModelContextProtocol.InvocationJournal.BindingSnapshot = null;
        enginehost::TiaMcpServer.ModelContextProtocol.InvocationJournal.BindingSnapshot = null;
        try
        {
            using IDisposable request = observed
                ? engine ? enginehost::TiaMcpServer.ModelContextProtocol.InvocationJournal.Observe("served-request", "GetSessionState", "host", release, false, () => "{}")
                    : TiaMcpServer.ModelContextProtocol.InvocationJournal.Observe("served-request", "GetSessionState", "host", release, false, () => "{}")
                : engine ? foundationhost::TiaMcp.FoundationHost.FoundationWorkerRequest.Use("served-request", "GetSessionState")
                    : FoundationWorkerRequest.Use("served-request", "GetSessionState");
            var result = engine ? await worker!.Call("FixtureCorrelation", new(), CancellationToken.None)
                : await plc!.Call("FixtureCorrelation", new(), CancellationToken.None);
            Xunit.Assert.Equal("served-request", (string?)result!["id"]);
            Xunit.Assert.Equal("GetSessionState", (string?)result["tool"]);
            var dispatch = rows.Select(row => JsonNode.Parse(row)!).Where(row => (string?)row["tool"] == "worker:GetSessionState").ToArray();
            Xunit.Assert.Equal(new[] { "BEFORE", "RETURNED" }, dispatch.Select(row => (string?)row["phase"]));
            Xunit.Assert.All(dispatch, row => Xunit.Assert.Equal("served-request", (string?)row["id"]));
            rows.Clear();
            await Xunit.Assert.ThrowsAnyAsync<Exception>(() => engine
                ? worker!.Call("Attach", new() { ["processId"] = 901 }, CancellationToken.None)
                : plc!.Call("Attach", new() { ["processId"] = 901 }, CancellationToken.None));
            dispatch = rows.Select(row => JsonNode.Parse(row)!).Where(row => (string?)row["tool"] == "worker:GetSessionState").ToArray();
            Xunit.Assert.Equal(new[] { "BEFORE", "THREW" }, dispatch.Select(row => (string?)row["phase"]));
            Xunit.Assert.All(dispatch, row => Xunit.Assert.Equal("served-request", (string?)row["id"]));
        }
        finally
        {
            TiaMcpServer.ModelContextProtocol.InvocationJournal.ConfigureOutput(null);
            enginehost::TiaMcpServer.ModelContextProtocol.InvocationJournal.ConfigureOutput(null);
            TiaMcpServer.ModelContextProtocol.InvocationJournal.BindingSnapshot = previousBinding;
            enginehost::TiaMcpServer.ModelContextProtocol.InvocationJournal.BindingSnapshot = previousEngineBinding;
        }
    }

    [Xunit.Fact]
    public async Task Foundation_client_consumes_the_full_spill_and_removes_only_the_validated_file()
    {
        string exe = Path.Combine(AppContext.BaseDirectory, "TiaMcp.FoundationHost.Tests.exe");
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "TiaMcp.Adapter.19.dll"), "offline session fixture");
        string directory = TiaOpenness.Shared.DataLocations.Current.WorkerSpillsDirectory;
        var before = Directory.GetFiles(directory).OrderBy(path => path, StringComparer.Ordinal).ToArray();
        using var worker = new WorkerClient("19", exe, AppContext.BaseDirectory, true) { Bundled = false };
        var result = await worker.Call("FixtureSpill", new(), CancellationToken.None);
        Xunit.Assert.Equal(TiaMcp.WorkerChannel.ChannelLimits.ResponseBytes, ((string)result!["text"]!).Length);
        Xunit.Assert.Equal(before, Directory.GetFiles(directory).OrderBy(path => path, StringComparer.Ordinal).ToArray());
        Xunit.Assert.False(worker.Poisoned);
        Xunit.Assert.NotNull(await worker.Call("ReadState", new(), CancellationToken.None));
    }

    [Xunit.Fact]
    public void Foundation_result_parking_retains_the_complete_response_and_small_results()
    {
        using var worker = new WorkerClient("19", "unused", "unused", false);
        IFoundationWorkerResult parking = worker;
        var small = new CallToolResult { Content = new[] { new TextContentBlock { Text = "{}" } } };
        Xunit.Assert.Same(small, parking.ParkResult(small, "GetSessionState"));
        string text = "{\"large\":\"" + new string('x', TiaMcp.WorkerChannel.ChannelLimits.ResponseBytes) + "\"}";
        var large = new CallToolResult { Content = new[] { new TextContentBlock { Text = text } } };
        var result = parking.ParkResult(large, "GetSessionState");
        var body = JsonNode.Parse(((TextContentBlock)result.Content.Single()).Text)!;
        string id = (string)body["meta"]!["exportId"]!;
        Xunit.Assert.Equal(text, TiaMcpServer.ModelContextProtocol.ExportStore.Get(id)!.Content);
        Xunit.Assert.True(((TextContentBlock)result.Content.Single()).Text.Length < text.Length);
        Xunit.Assert.False(((IFoundationSessionWorker)worker).SharedSession);
    }

    // A launch refused inside the client's state lock must leave that lock free: the next call,
    // from another thread, enters it and is refused the same way instead of blocking.
    [Xunit.Theory, Xunit.InlineData("19"), Xunit.InlineData("21")]
    public async Task Failed_worker_launch_releases_the_state_lock_for_the_next_call(string release)
    {
        using var worker = new foundationhost::TiaMcp.FoundationHost.WorkerClient(release,
            Path.Combine(AppContext.BaseDirectory, "missing-launch-worker.exe"), AppContext.BaseDirectory, true) { Bundled = false };
        object sync = typeof(foundationhost::TiaMcp.FoundationHost.WorkerClient)
            .GetField("stateSync", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(worker)!;
        // The refusal happens before the first real await, on this thread.
        await Xunit.Assert.ThrowsAsync<FileNotFoundException>(() => worker.Call("ReadState", new JsonObject(), CancellationToken.None));
        bool taken = false;
        var probe = new Thread(() => { taken = Monitor.TryEnter(sync, TimeSpan.FromSeconds(2)); if (taken) Monitor.Exit(sync); });
        probe.Start(); Xunit.Assert.True(probe.Join(TimeSpan.FromSeconds(10)));
        Xunit.Assert.True(taken);
        Exception? second = null;
        var next = new Thread(() => {
            try { worker.Call("ReadState", new JsonObject(), CancellationToken.None).GetAwaiter().GetResult(); }
            catch (Exception error) { second = error; }
        });
        next.Start(); Xunit.Assert.True(next.Join(TimeSpan.FromSeconds(10)));
        Xunit.Assert.IsType<FileNotFoundException>(second);
        Xunit.Assert.False(worker.Poisoned);
    }

    // A request the worker client sent and then lost (unknown native outcome) is never reported as
    // a pre-operation rejection by the ported optional-package path, for reads as well as writes.
    [Xunit.Theory, Xunit.InlineData("ListTestSuiteCases", false), Xunit.InlineData("ExchangeTestSuiteCase", true)]
    public void Lost_worker_acknowledgement_maps_to_unknown_for_reads_and_writes(string tool, bool writes)
    {
        enginehost::TiaMcp.FoundationHost.EngineHostConfiguration.ReleaseKey = "21";
        var lost = new IOException("Worker exited; native outcome is unknown.");
        lost.Data["foundationRequestSent"] = true; lost.Data["workerOutcomeUnknown"] = true;
        var body = enginehost::TiaMcpServer.ModelContextProtocol.OptionalPackageContract.Run(tool, writes, () => throw lost).StructuredContent!;
        Xunit.Assert.Equal("unknown", (string?)body["meta"]?["outcome"]);
        Xunit.Assert.Equal("unknown", (string?)body["meta"]?["execution"]);
        Xunit.Assert.True((bool?)body["meta"]?["requiresSessionReset"]);
        Xunit.Assert.Equal("OUTCOME_UNKNOWN", (string?)body["error"]?["code"]);
        Xunit.Assert.Null(body["data"]?["operationNotStarted"]);
        // The same failure before the request was sent stays a pre-operation rejection.
        var unsent = new IOException("Worker exited before the request was sent.");
        unsent.Data["foundationRequestSent"] = false;
        var refused = enginehost::TiaMcpServer.ModelContextProtocol.OptionalPackageContract.Run(tool, writes, () => throw unsent).StructuredContent!;
        Xunit.Assert.Equal("rejected-before-operation", (string?)refused["meta"]?["outcome"]);
        Xunit.Assert.False((bool?)refused["meta"]?["requiresSessionReset"]);
    }

    // A request over the worker frame is never sent; both the shared (FoundationV4Result) and the
    // ported (optional-package) mappings report LIMIT_EXCEEDED before operation.
    [Xunit.Fact]
    public async Task Over_frame_request_is_limit_exceeded_on_shared_and_ported_paths()
    {
        enginehost::TiaMcp.FoundationHost.EngineHostConfiguration.ReleaseKey = "21";
        using var worker = new foundationhost::TiaMcp.FoundationHost.WorkerClient("21",
            Path.Combine(AppContext.BaseDirectory, "missing-launch-worker.exe"), AppContext.BaseDirectory, true) { Bundled = false };
        var args = new JsonObject { ["softwarePath"] = "PLC", ["regexName"] = new string('x', TiaMcp.WorkerChannel.ChannelLimits.RequestBytes) };
        var error = await Xunit.Assert.ThrowsAsync<TiaMcp.WorkerChannel.ChannelLimitException>(() => worker.Call("ReadBlocks", args, CancellationToken.None));
        Xunit.Assert.Equal(false, error.Data["foundationRequestSent"]);
        Xunit.Assert.Equal(TiaMcp.WorkerChannel.ChannelLimits.RequestBytes, error.Data["workerLimitBytes"]);
        Xunit.Assert.False(worker.Poisoned);
        var shared = foundationhost::TiaMcp.FoundationHost.FoundationV4Result.Failure("21", "ListPlcBlocks", "fixture", true, false, null, error).StructuredContent!;
        var ported = enginehost::TiaMcpServer.ModelContextProtocol.OptionalPackageContract.Run("ListTestSuiteCases", false, () => throw error).StructuredContent!;
        foreach (var body in new[] { shared, ported })
        {
            Xunit.Assert.Equal("LIMIT_EXCEEDED", (string?)body["error"]?["code"]);
            Xunit.Assert.Equal("rejected-before-operation", (string?)body["meta"]?["outcome"]);
            Xunit.Assert.Equal("not-started", (string?)body["meta"]?["execution"]);
            Xunit.Assert.False((bool?)body["meta"]?["requiresSessionReset"]);
        }
    }
}
