using System.Diagnostics;
using System.Text.Json.Nodes;
using TiaMcp.Engine.Tests.Shared;
using Xunit;

public sealed partial class EngineHttpSessionTestsPlc
{
    public static IEnumerable<object[]> TakeoverReleases()
        => new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" }.Select(release => new object[] { release });

    [Fact]
    public async Task Initialize_environment_reports_the_session_transport_and_server_version()
    {
        using var host = await Fixture.Start("21");
        using var session = new Session(host);
        await session.Initialize();
        var report = await session.Tool("InitializeEnvironment", new() { ["limit"] = 1 });
        Assert.Equal("http", (string?)report["data"]?["environment"]?["transport"]);
        Assert.False(string.IsNullOrWhiteSpace((string?)report["data"]?["environment"]?["serverVersion"]));
        await session.End();
    }

    [Theory, MemberData(nameof(TakeoverReleases))]
    public async Task Abandoned_session_gives_way_to_connect_portal_without_a_host_restart(string release)
    {
        using var host = await Fixture.Start(release);
        using var holder = new Session(host);
        using var newcomer = new Session(host);
        await holder.Initialize(); await newcomer.Initialize();
        await holder.Tool("ConnectPortal", new() { ["processId"] = 331 });
        int holderPid = (int)(await holder.Tool(SessionFixtureTool.Name))["data"]!["fixturePid"]!;
        using var worker = Process.GetProcessById(holderPid);
        await host.AdvanceClock();
        var connected = await newcomer.Tool("ConnectPortal", new() { ["processId"] = 331 });
        Assert.True((bool?)connected["ok"]);
        Assert.False(worker.HasExited);
        var taken = await holder.Tool("GetProjectInfo", ok: false);
        Assert.Equal("taken-over", (string?)taken["error"]?["details"]?["condition"]);
        Assert.Contains("ConnectPortal", (string?)taken["error"]?["message"]);
        Assert.Null((await holder.Tool("GetSessionState"))["data"]?["sessionState"]?["workerRecovery"]);
        await newcomer.Tool("DisconnectPortal");
        await holder.Tool("ConnectPortal", new() { ["processId"] = 331 });
        Assert.True((bool?)(await holder.Tool("GetProjectInfo"))["ok"]);
        await holder.Tool("DisconnectPortal");
        await holder.End(); await newcomer.End();
    }

    [Theory, MemberData(nameof(TakeoverReleases))]
    public async Task Recently_active_session_requires_take_over_and_the_handover_is_audited(string release)
    {
        using var host = await Fixture.Start(release);
        using var holder = new Session(host);
        using var newcomer = new Session(host);
        await holder.Initialize(); await newcomer.Initialize();
        await holder.Tool("ConnectPortal", new() { ["processId"] = 332 });
        int holderPid = (int)(await holder.Tool(SessionFixtureTool.Name))["data"]!["fixturePid"]!;
        var refused = await newcomer.Tool("ConnectPortal", new() { ["processId"] = 332 }, ok: false);
        Assert.Equal("tia-held-by-session", (string?)refused["error"]?["details"]?["condition"]);
        Assert.Equal("not-started", (string?)refused["meta"]?["execution"]);
        Assert.Contains("engine-session-fixture", (string?)refused["error"]?["message"]);
        Assert.Contains("takeOver=true", (string?)refused["error"]?["message"]);
        await newcomer.Tool("CallTool", new() { ["name"] = "ConnectPortal",
            ["arguments"] = new JsonObject { ["processId"] = 332, ["takeOver"] = true } });
        var audit = Directory.EnumerateFiles(host.DataDirectory, "audit-*.jsonl", SearchOption.AllDirectories)
            .SelectMany(File.ReadAllLines).Select(line => JsonNode.Parse(line)!).ToArray();
        Assert.Single(audit, row => (string?)row["tool"] == "ConnectPortal" && (string?)row["outcome"] == "taken-over");
        string[] methods = File.ReadAllLines(Path.Combine(host.DataDirectory, "leases", "trace-" + holderPid + ".jsonl"))
            .Select(line => (string)JsonNode.Parse(line)!["method"]!).ToArray();
        Assert.Single(methods, method => method == "adapter.Disconnect");
        Assert.DoesNotContain(methods, method => method.Contains("Save", StringComparison.Ordinal) || method.Contains("Close", StringComparison.Ordinal));
        await newcomer.Tool("DisconnectPortal"); await holder.End(); await newcomer.End();
    }

    [Theory, MemberData(nameof(TakeoverReleases))]
    public async Task Takeover_carries_unsettled_calls_to_the_new_session(string release)
    {
        using var host = await Fixture.Start(release);
        using var holder = new Session(host);
        using var newcomer = new Session(host);
        await holder.Initialize(); await newcomer.Initialize();
        await holder.Tool("ConnectPortal", new() { ["processId"] = 333 });
        var unknown = await holder.Tool(SessionFixtureTool.Name, new() { ["fault"] = true }, ok: false);
        string id = (string)unknown["meta"]!["requestId"]!;
        var connected = await newcomer.Tool("ConnectPortal", new() { ["processId"] = 333, ["takeOver"] = true });
        Assert.Equal("RELEASED", (string?)connected["data"]?["previousOwner"]?["leaseState"]);
        Assert.Equal(id, (string?)connected["data"]?["previousOwner"]?["unsettledRequests"]?[0]?["requestId"]);
        var inherited = Assert.Single((await newcomer.Tool("GetSessionState"))["data"]!["sessionState"]!["unsettled"]!.AsArray())!;
        Assert.Equal(id, (string?)inherited["requestId"]);
        Assert.True((bool?)inherited["fromPreviousOwner"]);
        Assert.Equal("previous-owner", (string?)inherited["origin"]);
        var saved = await newcomer.Tool("SaveProject", new() { ["dryRun"] = true });
        Assert.Contains(saved["meta"]!["warnings"]!.AsArray(), warning => (string?)warning?["code"] == "UNSETTLED_CALLS");
        var recovered = await newcomer.Tool("GetCallResult", new() { ["requestId"] = id }, ok: false);
        Assert.Equal("original-call", (string?)recovered["data"]?["elapsedMsSource"]);
        Assert.Equal((long)unknown["meta"]!["elapsedMs"]!, (long?)recovered["meta"]?["elapsedMs"]);
        await newcomer.Tool("DisconnectPortal"); await holder.End(); await newcomer.End();
    }

    [Theory, MemberData(nameof(TakeoverReleases))]
    public async Task Running_call_protects_its_session_and_the_refusal_clears_when_it_returns(string release)
    {
        using var host = await Fixture.Start(release);
        using var holder = new Session(host);
        using var newcomer = new Session(host);
        await holder.Initialize(); await newcomer.Initialize();
        await holder.Tool("ConnectPortal", new() { ["processId"] = 334 });
        string gate = Path.Combine(host.DataDirectory, "running-gate");
        Directory.CreateDirectory(gate);
        var dispatched = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var watcher = new FileSystemWatcher(gate, "dispatched");
        watcher.Created += (_, _) => dispatched.TrySetResult();
        watcher.EnableRaisingEvents = true;
        var running = holder.Tool(SessionFixtureTool.Name, new() { ["hang"] = true });
        if (File.Exists(Path.Combine(gate, "dispatched"))) dispatched.TrySetResult();
        await dispatched.Task.WaitAsync(TestDeadlines.Hang);
        await host.AdvanceClock();
        foreach (bool takeOver in new[] { false, true })
        {
            var refused = await newcomer.Tool("ConnectPortal", new() { ["processId"] = 334, ["takeOver"] = takeOver }, ok: false);
            Assert.Equal("tia-held-by-session", (string?)refused["error"]?["details"]?["condition"]);
            Assert.Contains(SessionFixtureTool.Name, (string?)refused["error"]?["message"]);
            Assert.Contains("request", (string?)refused["error"]?["message"]);
            Assert.Contains("started", (string?)refused["error"]?["message"]);
            Assert.Throws<IOException>(() => File.ReadAllText(Path.Combine(host.DataDirectory, "leases", "334-1.lease")));
        }
        host.ReleaseRunning();
        Assert.True((bool?)(await running)["ok"]);
        await newcomer.Tool("ConnectPortal", new() { ["processId"] = 334, ["takeOver"] = true });
        await newcomer.Tool("DisconnectPortal"); await holder.End(); await newcomer.End();
    }

    [Theory, MemberData(nameof(TakeoverReleases))]
    public async Task Taken_over_disconnect_settles_locally_and_other_native_calls_stay_refused(string release)
    {
        using var host = await Fixture.Start(release);
        using var holder = new Session(host);
        using var newcomer = new Session(host);
        await holder.Initialize(); await newcomer.Initialize();
        await holder.Tool("ConnectPortal", new() { ["processId"] = 335 });
        int holderPid = (int)(await holder.Tool(SessionFixtureTool.Name))["data"]!["fixturePid"]!;
        await newcomer.Tool("ConnectPortal", new() { ["processId"] = 335, ["takeOver"] = true });
        int newcomerPid = (int)(await newcomer.Tool(SessionFixtureTool.Name))["data"]!["fixturePid"]!;
        string holderTrace = Path.Combine(host.DataDirectory, "leases", "trace-" + holderPid + ".jsonl");
        string newcomerTrace = Path.Combine(host.DataDirectory, "leases", "trace-" + newcomerPid + ".jsonl");
        string beforeHolder = File.ReadAllText(holderTrace), beforeNewcomer = File.ReadAllText(newcomerTrace);
        foreach (int attempt in new[] { 1, 2 })
        {
            var disconnected = await holder.Tool("DisconnectPortal");
            Assert.Equal("disconnected", (string?)disconnected["data"]?["sessionState"]);
            Assert.Contains(disconnected["meta"]!["warnings"]!.AsArray(), warning => (string?)warning?["code"] == "SESSION_TAKEN_OVER");
            Assert.Contains(disconnected["meta"]!["warnings"]!.AsArray(), warning =>
                (string?)warning?["details"]?["condition"] == "taken-over" && ((string?)warning?["message"])!.Contains("had been taken over"));
            Assert.Equal(beforeHolder, File.ReadAllText(holderTrace));
            Assert.Equal(beforeNewcomer, File.ReadAllText(newcomerTrace));
            Assert.Throws<IOException>(() => File.ReadAllText(Path.Combine(host.DataDirectory, "leases", "335-1.lease")));
        }
        var refused = await holder.Tool("GetProjectInfo", ok: false);
        Assert.Equal("taken-over", (string?)refused["error"]?["details"]?["condition"]);
        Assert.Equal(beforeHolder, File.ReadAllText(holderTrace));
        Assert.Equal(beforeNewcomer, File.ReadAllText(newcomerTrace));
        Assert.True((bool?)(await newcomer.Tool("GetProjectInfo"))["ok"]);
        await newcomer.Tool("DisconnectPortal"); await holder.End(); await newcomer.End();
    }
}
