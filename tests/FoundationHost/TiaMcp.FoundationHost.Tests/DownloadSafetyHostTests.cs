extern alias toolpipeline;
using System.Text.Json.Nodes;
using TiaMcp.FoundationHost;
using TiaOpenness.Shared;
using Xunit;
using Host = toolpipeline::TiaMcpServer.ModelContextProtocol.McpServer;

public sealed class DownloadSafetyHostTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Physical_confirmation_refuses_before_approval_or_worker(bool nested)
    {
        using var settings = new OnlineTargetSettingsScope(null);
        int waits = 0;
        using var worker = new BoundWorker();
        using var fixture = new VersionControlAdmissionTests.FileToolFixture("21", worker: worker,
            approvalWait: (pending, current, token) => { waits++; return Task.FromResult(new ApprovalOutcome(pending, false, "denied")); });
        using var session = fixture.EnterSession();
        var args = Arguments("Intel(R) Ethernet", false);
        args["dryRun"] = false;
        var result = await Call(fixture, nested, args);
        Assert.False((bool?)result["ok"]);
        Assert.Equal("not-started", (string?)result["meta"]?["execution"]);
        Assert.Contains("confirmPhysicalTarget=true", result.ToJsonString(), StringComparison.Ordinal);
        Assert.Equal(0, waits);
        Assert.Equal(0, worker.Calls);
    }

    [Theory]
    [InlineData("15.1", false)]
    [InlineData("16", false)]
    [InlineData("17", false)]
    [InlineData("18", false)]
    [InlineData("19", false)]
    [InlineData("20", false)]
    [InlineData("21", false)]
    [InlineData("21", true)]
    public async Task Approval_off_physical_download_still_waits_and_unavailable_refuses(string release, bool nested)
    {
        using var settings = new OnlineTargetSettingsScope(null);
        var before = ApprovalSettings.Load(ApprovalSettings.SettingsPath);
        int waits = 0;
        try
        {
            new ApprovalSettings(false, 1).Save(ApprovalSettings.SettingsPath);
            Task<ApprovalOutcome> Wait(PendingApproval pending, ApprovalSettings current, CancellationToken token)
            {
                Assert.True(current.Enabled);
                waits++;
                return Task.FromResult(new ApprovalOutcome(pending, false, "workbench-unavailable"));
            }
            using var waitScope = new ApprovalWaitScope(Wait);
            using var worker = new BoundWorker();
            using var fixture = new VersionControlAdmissionTests.FileToolFixture(release, worker: worker, approvalWait: Wait);
            using var session = fixture.EnterSession();
            var args = Arguments("", true);
            var preview = await Call(fixture, nested, args);
            Assert.True((bool?)preview["ok"], preview.ToJsonString());
            Assert.Equal("physical", (string?)preview["data"]?["targetKind"]);
            Assert.True((bool?)preview["data"]?["requiresPhysicalConfirmation"]);
            Assert.Contains("confirmPhysicalTarget=true", (string?)preview["data"]?["executionRequirement"], StringComparison.Ordinal);
            Assert.NotNull(preview["data"]?["localNetwork"]?["adapters"]);
            Assert.Equal(0, waits);
            args["dryRun"] = false;
            args["expectedPlanHash"] = (string?)preview["data"]?["planHash"];
            args["expectedProjectFile"] = BoundWorker.Project;
            var result = await Call(fixture, nested, args);
            Assert.Equal("CONFIRMATION_REQUIRED", (string?)result["error"]?["code"]);
            Assert.Equal("workbench-unavailable", (string?)result["error"]?["details"]?["reason"]);
            Assert.Equal(1, waits);
            Assert.Equal(0, worker.OnlineCalls);
            Assert.DoesNotContain("APPROVAL_DISABLED", result.ToJsonString(), StringComparison.Ordinal);
        }
        finally { before.Save(ApprovalSettings.SettingsPath); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Approval_off_PLCSIM_download_has_no_enabled_wait(bool nested)
    {
        using var settings = new OnlineTargetSettingsScope(null);
        var before = ApprovalSettings.Load(ApprovalSettings.SettingsPath);
        int waits = 0;
        try
        {
            new ApprovalSettings(false, 1).Save(ApprovalSettings.SettingsPath);
            Task<ApprovalOutcome> Wait(PendingApproval pending, ApprovalSettings current, CancellationToken token)
            {
                if (current.Enabled) waits++;
                return Task.FromResult(new ApprovalOutcome(pending, !current.Enabled, null));
            }
            using var waitScope = new ApprovalWaitScope(Wait);
            using var worker = new BoundWorker();
            using var fixture = new VersionControlAdmissionTests.FileToolFixture("21", worker: worker, approvalWait: Wait);
            using var session = fixture.EnterSession();
            var args = Arguments("Siemens PLCSIM Virtual Ethernet Adapter", false);
            var preview = await Call(fixture, nested, args);
            Assert.Equal("plcsim", (string?)preview["data"]?["targetKind"]);
            args["dryRun"] = false;
            args["expectedPlanHash"] = (string?)preview["data"]?["planHash"];
            args["expectedProjectFile"] = BoundWorker.Project;
            var result = await Call(fixture, nested, args);
            Assert.True((bool?)result["ok"], result.ToJsonString());
            Assert.Equal(0, waits);
            Assert.Equal(1, worker.OnlineCalls);
            Assert.Contains("APPROVAL_DISABLED", result.ToJsonString(), StringComparison.Ordinal);
        }
        finally { before.Save(ApprovalSettings.SettingsPath); }
    }

    [Fact]
    public async Task Simulator_only_write_keeps_disabled_effective_settings()
    {
        var before = ApprovalSettings.Load(ApprovalSettings.SettingsPath);
        var previous = Host.ApprovalWaitOverrideForTests;
        try
        {
            new ApprovalSettings(false, 1).Save(ApprovalSettings.SettingsPath);
            using var fixture = new VersionControlAdmissionTests.FileToolFixture("21");
            using var session = fixture.EnterSession();
            int enabledWaits = 0;
            Host.ApprovalWaitOverrideForTests = (pending, current, token) =>
            {
                if (current.Enabled) enabledWaits++;
                return Task.FromResult(new ApprovalOutcome(pending, !current.Enabled, null));
            };
            var outcome = await Host.WaitForApproval("WritePlcSimAdvancedTags", "{\"dryRun\":false}", CancellationToken.None);
            Assert.True(outcome!.Disabled);
            Assert.Equal(0, enabledWaits);
        }
        finally { Host.ApprovalWaitOverrideForTests = previous; before.Save(ApprovalSettings.SettingsPath); }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Expired_plan_refuses_before_write_even_when_it_expires_during_approval(bool nested, bool duringApproval)
    {
        using var settings = new OnlineTargetSettingsScope(null);
        var before = ApprovalSettings.Load(ApprovalSettings.SettingsPath);
        var previousClock = Host.OnlinePlanClockForTests;
        var now = DateTimeOffset.Parse("2026-11-17T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        int waits = 0;
        try
        {
            new ApprovalSettings(false, 1).Save(ApprovalSettings.SettingsPath);
            Host.OnlinePlanClockForTests = () => now;
            Task<ApprovalOutcome> Wait(PendingApproval pending, ApprovalSettings current, CancellationToken token)
            {
                waits++;
                Assert.True(current.Enabled);
                now = now.AddMinutes(15);
                return Task.FromResult(new ApprovalOutcome(pending, false, null));
            }
            using var waitScope = new ApprovalWaitScope(Wait);
            using var worker = new BoundWorker();
            using var fixture = new VersionControlAdmissionTests.FileToolFixture("21", worker: worker, approvalWait: Wait);
            using var session = fixture.EnterSession();
            var args = Arguments("Intel(R) Ethernet", true);
            var preview = await Call(fixture, nested, args);
            Assert.True((bool?)preview["ok"], preview.ToJsonString());
            Assert.NotNull(preview["data"]?["expiresUtc"]);
            args["dryRun"] = false;
            args["expectedPlanHash"] = (string?)preview["data"]?["planHash"];
            args["expectedProjectFile"] = BoundWorker.Project;
            if (!duringApproval) now = now.AddMinutes(15);
            var result = await Call(fixture, nested, args);
            Assert.Equal("PLAN_STALE", (string?)result["error"]?["code"]);
            Assert.Equal("expired", (string?)result["error"]?["details"]?["reason"]);
            Assert.Equal("not-started", (string?)result["meta"]?["execution"]);
            Assert.Equal(duringApproval ? 1 : 0, waits);
            Assert.Equal(0, worker.OnlineCalls);
        }
        finally { Host.OnlinePlanClockForTests = previousClock; before.Save(ApprovalSettings.SettingsPath); }
    }

    [Theory]
    [InlineData("SetPlcWatchTableModifyValue", true)]
    [InlineData("ManageOnlineDriveFunctions", true)]
    [InlineData("ManageOnlineDriveFunctions", false)]
    public void Every_online_plan_hash_uses_the_same_expiry_rule(string tool, bool writing)
    {
        var previousClock = Host.OnlinePlanClockForTests;
        var now = DateTimeOffset.Parse("2026-11-17T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        try
        {
            Host.OnlinePlanClockForTests = () => now;
            using var worker = new BoundWorker();
            using var fixture = new VersionControlAdmissionTests.FileToolFixture("21", worker: worker);
            using var session = fixture.EnterSession();
            var args = new { softwarePath = "PLC_1", action = writing ? "change" : "read" };
            var preview = Host.RunWritePreview(tool, true, args, () => Host.V4Result(tool, new JsonObject()), writing: writing);
            string hash = (string)preview.StructuredContent!["data"]!["planHash"]!;
            Assert.NotNull(preview.StructuredContent["data"]!["expiresUtc"]);
            now = now.AddMinutes(15);
            int issued = 0;
            var result = Host.RunWritePreview(tool, false, args, () => { issued++; return Host.V4Result(tool, new JsonObject()); },
                hash, BoundWorker.Project, writing);
            Assert.Equal("PLAN_STALE", (string?)result.StructuredContent?["error"]?["code"]);
            Assert.Equal("expired", (string?)result.StructuredContent?["error"]?["details"]?["reason"]);
            Assert.Equal(0, issued);
        }
        finally { Host.OnlinePlanClockForTests = previousClock; }
    }

    [Fact]
    public void A_second_preview_does_not_renew_the_first_preview_hash()
    {
        var previousClock = Host.OnlinePlanClockForTests;
        var now = DateTimeOffset.Parse("2026-11-17T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        try
        {
            Host.OnlinePlanClockForTests = () => now;
            using var worker = new BoundWorker();
            using var fixture = new VersionControlAdmissionTests.FileToolFixture("21", worker: worker);
            using var session = fixture.EnterSession();
            var args = new { softwarePath = "PLC_1", pgPcInterface = "PLCSIM" };
            var first = Host.RunWritePreview("DownloadPlc", true, args, () => throw new InvalidOperationException());
            string firstHash = (string)first.StructuredContent!["data"]!["planHash"]!;
            now = now.AddMinutes(15);
            var second = Host.RunWritePreview("DownloadPlc", true, args, () => throw new InvalidOperationException());
            string secondHash = (string)second.StructuredContent!["data"]!["planHash"]!;
            Assert.NotEqual(firstHash, secondHash);
            int issued = 0;
            ModelContextProtocol.Protocol.CallToolResult Execute() { issued++; return Host.V4Result("DownloadPlc", new JsonObject()); }
            var expired = Host.RunWritePreview("DownloadPlc", false, args, Execute, firstHash, BoundWorker.Project);
            Assert.Equal("expired", (string?)expired.StructuredContent?["error"]?["details"]?["reason"]);
            Assert.Equal(0, issued);
            var current = Host.RunWritePreview("DownloadPlc", false, args, Execute, secondHash, BoundWorker.Project);
            Assert.True((bool?)current.StructuredContent?["ok"], current.StructuredContent?.ToJsonString());
            Assert.Equal(1, issued);
        }
        finally { Host.OnlinePlanClockForTests = previousClock; }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Download_service_carries_the_physical_confirmation_to_the_worker(bool confirmed)
    {
        using var fixture = new VersionControlAdmissionTests.FileToolFixture("21");
        using var session = fixture.EnterSession();
        JsonNode? request = null;
        var service = new toolpipeline::TiaMcpServer.Siemens.Services.OnlineDownloadService((operation, arguments) =>
        {
            Assert.Equal("plc-online.ExecuteOnline", operation);
            request = arguments["request"]!.DeepClone();
            return new JsonObject { ["Value"] = new JsonObject { ["Ok"] = true, ["State"] = "Success" } };
        }, () => BoundWorker.Project);
        service.DownloadToPlc("PLC_1", targetIpAddress: "192.0.2.10", confirmPhysicalTarget: confirmed);
        Assert.Equal(confirmed, (bool?)request?["ConfirmPhysicalTarget"]);
        Assert.Equal("192.0.2.10", (string?)request?["TargetIpAddress"]);
    }

    [Theory]
    [InlineData("14sp1")]
    [InlineData("15.1")]
    [InlineData("16")]
    [InlineData("17")]
    [InlineData("18")]
    [InlineData("19")]
    [InlineData("20")]
    [InlineData("21")]
    public async Task Download_guides_are_served_with_exact_release_availability(string release)
    {
        using var fixture = new VersionControlAdmissionTests.FileToolFixture(release);
        using var session = fixture.EnterSession();
        foreach (string topic in new[] { "download-physical", "download-plcsim" })
        {
            var result = await fixture.Call("GetToolUsage", new { exampleId = "sequence/" + topic, exampleKind = "all" });
            Assert.True((bool?)result.StructuredContent?["ok"], result.StructuredContent?.ToJsonString());
            var guide = Assert.Single(result.StructuredContent!["data"]!["examples"]!.AsArray())!;
            Assert.Equal(topic == "download-physical" ? release != "14sp1" : release is "20" or "21", (bool?)guide["available"]);
            Assert.Contains("V14 SP1", (string?)guide["notes"], StringComparison.Ordinal);
            var steps = guide["steps"]!.AsArray();
            int compile = steps.Select((step, index) => (step, index)).Single(row => (string?)row.step!["tool"] == "CompilePlcSoftware").index;
            Assert.Equal("SaveProject", (string?)steps[compile + 1]!["tool"]);
            if (topic == "download-physical")
                Assert.All(steps.Where(step => (string?)step!["tool"] == "DownloadPlc"),
                    step => Assert.True((bool?)step!["arguments"]?["confirmPhysicalTarget"]));
        }
        Assert.Equal(0, fixture.Worker.Calls);
    }

    private static async Task<JsonNode> Call(VersionControlAdmissionTests.FileToolFixture fixture, bool nested, JsonObject args)
        => (await fixture.Call(nested ? "CallTool" : "DownloadPlc", nested ? new { name = "DownloadPlc", arguments = args } : (object)args))
            .StructuredContent!;

    private static JsonObject Arguments(string adapter, bool confirmed) => new() { ["softwarePath"] = "PLC_1",
        ["targetIpAddress"] = "192.0.2.10", ["pgPcInterface"] = adapter, ["confirmPhysicalTarget"] = confirmed, ["dryRun"] = true };

    private sealed class ApprovalWaitScope : IDisposable
    {
        private readonly Func<PendingApproval, ApprovalSettings, CancellationToken, Task<ApprovalOutcome>>? previous
            = Host.ApprovalWaitOverrideForTests;
        internal ApprovalWaitScope(Func<PendingApproval, ApprovalSettings, CancellationToken, Task<ApprovalOutcome>> wait)
            => Host.ApprovalWaitOverrideForTests = wait;
        public void Dispose() => Host.ApprovalWaitOverrideForTests = previous;
    }

    private sealed class BoundWorker : IFoundationWorker, toolpipeline::TiaMcp.FoundationHost.IEngineWorker
    {
        internal const string Project = "C:/Projects/fixture.ap21";
        internal int Calls, OnlineCalls;
        public bool Faulted => false;
        public JsonNode? Binding => new JsonObject { ["identity"] = new JsonObject { ["processId"] = 123,
            ["projectPath"] = Project, ["projectName"] = "fixture" } };
        public object SessionKey { get; } = new();
        public JsonObject Snapshot() => new() { ["readiness"] = new JsonObject { ["ready"] = true } };
        public Task<JsonObject> Status(CancellationToken token) => Task.FromResult(Snapshot());
        public Task<JsonObject> Restart(bool confirmed, CancellationToken token) => throw new InvalidOperationException("No restart expected.");
        public Task<IDisposable> Acquire(CancellationToken token) => Task.FromResult<IDisposable>(new Lane());
        public Task<toolpipeline::TiaMcp.FoundationHost.EngineReply> Invoke(string id, string name, JsonObject args, bool preview, CancellationToken token)
            => throw new InvalidOperationException("No engine dispatch expected.");
        public Task<JsonNode?> Call(string operation, JsonObject arguments, CancellationToken token)
        {
            Calls++;
            if (operation == "ReadState") return Task.FromResult<JsonNode?>(new JsonObject { ["ProcessId"] = 123,
                ["ProjectFile"] = Project, ["ProjectName"] = "fixture", ["IsAttached"] = true });
            Assert.Equal("plc-online.ExecuteOnline", operation);
            OnlineCalls++;
            return Task.FromResult<JsonNode?>(new JsonObject { ["Value"] = new JsonObject { ["Ok"] = true,
                ["State"] = "Success", ["Message"] = "Synthetic download", ["Meta"] = new JsonObject() } });
        }
        public void Dispose() { }
        private sealed class Lane : IDisposable { public void Dispose() { } }
    }
}
