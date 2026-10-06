using System.Globalization;
using System.Text.Json;
using TiaMcp.Adapters.Contracts.Candidates;
using TiaMcp.Logic.V4;
using Xunit;

public sealed class FallbackCandidateTests
{
    private const string Project = "C:\\REVIEWED\\PROJECT.AP21";
    private sealed class Adapter : IFallbackAdapter
    {
        internal readonly List<string> Trace = new();
        internal string Fault = "";
        internal bool Poisoned;
        internal Action? Before;
        internal Action? After;
        internal int Executions;
        internal int Refreshes;
        internal FallbackObservation State = new() { Binding = new() { ProcessId = 123, ProcessStartUtc = DateTimeOffset.Parse("2026-10-03T00:00:00Z"), ProjectFile = Project, Epoch = 2, WorkerEpoch = 3, Ownership = "borrowed" },
            TargetId = "plc-1", OfflineState = "offline", InventoryHash = new string('a', 64), Routes = new[] { new FallbackRoute { Id = "exact-route", TargetId = "native-route-1", NativeCalls = new[] { "native-operation" } } } };
        public bool Writes { get; set; } = true;
        public bool NeedsConfiguration { get; set; }
        public bool RequiresOffline { get; set; }
        public FallbackObservation Observe()
        {
            Trace.Add("observe");
            if (Fault == "readback" && Executions > 0) throw new IOException("actual readback unavailable");
            if (Fault == "observe-stale" && Refreshes == 0) throw new ReadHandleStaleException(true, true, new ObjectDisposedException("read-only workspace"));
            return JsonSerializer.Deserialize<FallbackObservation>(JsonSerializer.Serialize(State))!;
        }
        public void BeforeAction() { Trace.Add("before"); if (Fault == "before") throw new IOException("not issued"); Before?.Invoke(); }
        public bool ApplyConfiguration(string route)
        {
            Assert.Equal("exact-route", route); Trace.Add("configuration");
            if (Fault == "configuration-throw") throw new IOException("configuration outcome unavailable");
            if (Fault == "configuration-false") return false;
            return true;
        }
        public FallbackNativeResult Execute(FallbackRequest request, FallbackAttempt a)
        {
            Trace.Add("primitive"); Executions++;
            if (Fault == "not-issued") throw new IOException("no native call");
            if (Fault == "stale" && Refreshes == 0 || Fault == "stale-twice") throw new ReadHandleStaleException(true, true, new ObjectDisposedException("read-only workspace"));
            if (Fault == "unsafe-stale") throw new ReadHandleStaleException(false, true, new ObjectDisposedException("writable proxy"));
            if (Fault == "executed-stale") throw new ReadHandleStaleException(true, false, new ObjectDisposedException("executed read command"));
            a.OperationIssued = true; a.WriteIssued |= Writes; Trace.Add("native");
            if (Fault == "during") throw new IOException("native call threw");
            var result = new FallbackNativeResult { Success = Fault != "native-false", State = Fault == "native-false" ? "Error" : "Success", Messages = new[] { "real native result" } };
            a.NativeResult = result;
            After?.Invoke();
            if (Fault == "after") throw new IOException("after native result");
            if (Fault == "after-write-stale") throw new ReadHandleStaleException(true, true, new ObjectDisposedException("stale after write"));
            return result;
        }
        public void RefreshReadHandle() { Trace.Add("refresh"); Refreshes++; if (Fault == "refresh-fault") throw new IOException("refresh unavailable"); }
        public void MarkUncertain() { Poisoned = true; }
    }
    private static FallbackRequest Request(string entry = "DownloadPlc") => new() { Entry = entry, SoftwarePath = "PLC_1", Route = "exact-route", Parameters = new() { ["flag"] = "true" } };
    private static string Hash(Envelope e) => e.Data!.Value.GetProperty("plan").GetProperty("hash").GetString()!;
    private static Envelope Run(FallbackSession s, IFallbackAdapter a, FallbackRequest r, string mode = "preview", string hash = "", bool confirm = true, string credential = "")
        => s.Run(a, "21", r.Entry, "request", r, credential, mode, confirm, hash, Project);
    [Theory]
    [InlineData("DownloadPlc")][InlineData("DownloadPlcToFolder")][InlineData("CompilePlcSoftware")][InlineData("ExportPlcBlockDocuments")][InlineData("ImportPlcBlockDocuments")]
    [InlineData("ListVersionControlWorkspaces")][InlineData("GetVersionControlStatus")][InlineData("CreateVersionControlWorkspace")][InlineData("ConnectProjectToWorkspace")][InlineData("SynchronizeVersionControlWorkspace")]
    public void EveryEntryReviewsAnExactRouteAndIssuesItsOperationOnce(string entry)
    {
        var a = new Adapter(); var s = new FallbackSession(); var r = Request(entry); var p = Run(s, a, r); Assert.True(p.Ok); Assert.DoesNotContain("native", a.Trace);
        var result = Run(s, a, r, "apply", Hash(p)); Assert.True(result.Ok, V4Json.Serialize(result)); Assert.Single(a.Trace.Where(x => x == "native"));
        Assert.Equal(ErrorCode.PlanStale, Run(s, a, r, "apply", Hash(p)).Error!.Code); Assert.Single(a.Trace.Where(x => x == "native")); Assert.DoesNotContain("refresh", a.Trace);
    }
    [Theory]
    [InlineData("before", false, Outcome.RejectedBeforeOperation, false)]
    [InlineData("not-issued", false, Outcome.RejectedBeforeOperation, false)]
    [InlineData("during", false, Outcome.Unknown, true)][InlineData("after", false, Outcome.Unknown, true)][InlineData("readback", false, Outcome.Unknown, true)]
    [InlineData("configuration-false", true, Outcome.Failed, false)][InlineData("configuration-throw", true, Outcome.Unknown, true)]
    [InlineData("native-false", false, Outcome.Failed, false)]
    public void EveryTransferInterruptionPreservesIssuedAndRealResultEvidence(string fault, bool configuration, Outcome outcome, bool reset)
    {
        var a = new Adapter { NeedsConfiguration = configuration }; var s = new FallbackSession(); var r = Request(); var p = Run(s, a, r); a.Fault = fault;
        var result = Run(s, a, r, "apply", Hash(p)); Assert.Equal(outcome, result.Meta.Outcome); Assert.Equal(reset, result.Meta.RequiresSessionReset); Assert.Equal(reset, a.Poisoned);
        Assert.InRange(a.Trace.Count(x => x == "native"), 0, 1); Assert.InRange(a.Trace.Count(x => x == "configuration"), 0, 1); Assert.DoesNotContain("refresh", a.Trace);
        var attempt = result.Data!.Value.GetProperty("attempt");
        Assert.Equal(configuration, attempt.GetProperty("configurationIssued").GetBoolean());
        if (fault == "configuration-false") { Assert.False(attempt.GetProperty("configurationResult").GetBoolean()); Assert.False(attempt.GetProperty("operationIssued").GetBoolean()); Assert.DoesNotContain("primitive", a.Trace); }
        if (fault == "after" || fault == "readback" || fault == "native-false") Assert.Contains("real native result", attempt.GetProperty("nativeResult").GetRawText());
        int count = a.Trace.Count(x => x == "native"); Assert.False(Run(s, a, r, "apply", Hash(p)).Ok); Assert.Equal(count, a.Trace.Count(x => x == "native"));
    }
    [Theory]
    [InlineData("stale", false, false, false, 0, false)]
    [InlineData("stale", true, false, false, 1, true)]
    [InlineData("stale-twice", true, false, false, 1, false)]
    [InlineData("unsafe-stale", true, false, false, 0, false)]
    [InlineData("executed-stale", true, false, false, 0, false)]
    [InlineData("after-write-stale", true, true, false, 0, false)]
    [InlineData("stale", true, true, true, 0, false)]
    public void ReadHandleRefreshRequiresPermissionAndProvenUnexecutedReadOnlyProxy(string fault, bool permission, bool writes, bool configuration, int refreshes, bool success)
    {
        var a = new Adapter { Writes = writes, NeedsConfiguration = configuration }; var s = new FallbackSession(); var r = Request("GetVersionControlStatus"); r.RefreshReadHandle = permission;
        var p = Run(s, a, r); a.Fault = fault; var result = Run(s, a, r, "apply", Hash(p));
        Assert.Equal(success, result.Ok); Assert.Equal(refreshes, a.Refreshes); Assert.InRange(a.Trace.Count(x => x == "native"), 0, 1);
        Assert.Equal(fault == "after-write-stale" || configuration, result.Meta.RequiresSessionReset);
        Assert.DoesNotContain("GoOffline", a.Trace); Assert.DoesNotContain("second-download", a.Trace);
    }
    [Theory]
    [InlineData(false)][InlineData(true)]
    public void PreviewStaleReadRefreshesOnlyOnceAndKeepsPlanHashStable(bool permission)
    {
        var a = new Adapter { Writes = false, Fault = "observe-stale" }; var s = new FallbackSession(); var r = Request("GetVersionControlStatus"); r.RefreshReadHandle = permission;
        var p = Run(s, a, r); Assert.Equal(permission, p.Ok); Assert.Equal(permission ? 1 : 0, a.Refreshes); Assert.DoesNotContain("native", a.Trace);
        if (permission) Assert.Equal(Hash(p), Hash(Run(s, a, r)));
    }
    private static void Change(Adapter a, string key)
    {
        switch (key)
        {
            case "pid": a.State.Binding.ProcessId++; break; case "start": a.State.Binding.ProcessStartUtc = a.State.Binding.ProcessStartUtc!.Value.AddSeconds(1); break;
            case "project": a.State.Binding.ProjectFile = "C:\\changed.ap21"; break; case "epoch": a.State.Binding.Epoch++; break; case "worker": a.State.Binding.WorkerEpoch++; break;
            case "target": a.State.TargetId += "changed"; break; case "route": a.State.Routes[0].Id += "changed"; break; case "route-target": a.State.Routes[0].TargetId += "changed"; break;
            case "inventory": a.State.InventoryHash = new string('b', 64); break; case "offline": a.State.OfflineState = "online"; break;
        }
    }
    [Theory]
    [InlineData("pid", false)][InlineData("start", false)][InlineData("project", false)][InlineData("epoch", false)][InlineData("worker", false)][InlineData("target", false)][InlineData("route", false)][InlineData("route-target", false)][InlineData("inventory", false)][InlineData("offline", false)]
    [InlineData("pid", true)][InlineData("start", true)][InlineData("project", true)][InlineData("epoch", true)][InlineData("worker", true)][InlineData("target", true)][InlineData("route", true)][InlineData("route-target", true)][InlineData("inventory", true)][InlineData("offline", true)]
    public void PreviewAndFinalBoundaryStateChangesIssueNoNativeCall(string key, bool final)
    {
        var a = new Adapter { RequiresOffline = true }; var s = new FallbackSession(); var r = Request(); var p = Run(s, a, r);
        if (final) a.Before = () => Change(a, key); else Change(a, key);
        var result = Run(s, a, r, "apply", Hash(p)); Assert.False(result.Ok); Assert.False(result.Meta.RequiresSessionReset); Assert.DoesNotContain("native", a.Trace); Assert.DoesNotContain("configuration", a.Trace);
    }
    [Theory]
    [InlineData("pid")][InlineData("start")][InlineData("project")][InlineData("epoch")][InlineData("worker")][InlineData("target")][InlineData("route")][InlineData("route-target")]
    public void ChangedPostWriteBindingOrRouteIsUnknown(string key)
    {
        var a = new Adapter(); var s = new FallbackSession(); var r = Request(); var p = Run(s, a, r); a.After = () => Change(a, key);
        var result = Run(s, a, r, "apply", Hash(p)); Assert.Equal(Outcome.Unknown, result.Meta.Outcome); Assert.True(result.Meta.RequiresSessionReset); Assert.Single(a.Trace.Where(x => x == "native"));
    }
    [Theory]
    [InlineData("CompilePlcSoftware")][InlineData("ExportPlcBlockDocuments")][InlineData("ImportPlcBlockDocuments")]
    public void OnlineModeIsAnExplicitPreconditionRefusalWithoutOperationOrOffline(string entry)
    {
        var a = new Adapter { RequiresOffline = true }; a.State.OfflineState = "online"; a.State.OfflineTargets = new[] { "PLC_1", "PLC_2" };
        var result = Run(new(), a, Request(entry)); Assert.Equal(ErrorCode.OfflineRequired, result.Error!.Code); Assert.Equal(Outcome.RejectedBeforeOperation, result.Meta.Outcome);
        Assert.Equal(new[] { "observe" }, a.Trace); Assert.Contains("PLC_2", V4Json.Serialize(result));
    }
    [Fact]
    public void HashIgnoresCultureCorrelationAndConfirmationButBindsSecretsAndArguments()
    {
        var a = new Adapter(); var s = new FallbackSession(); var r = Request(); var p = Run(s, a, r, credential: "secret-a"); var prior = CultureInfo.CurrentCulture;
        try { CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR"); Assert.Equal(Hash(p), Hash(s.Run(a, "21", r.Entry, "another", r, "secret-a"))); }
        finally { CultureInfo.CurrentCulture = prior; }
        Assert.DoesNotContain("secret-a", V4Json.Serialize(p)); Assert.NotEqual(Hash(p), Hash(Run(s, a, r, credential: "secret-b")));
        Assert.Equal(ErrorCode.ConfirmationRequired, Run(s, a, r, "apply", Hash(p), false, "secret-a").Error!.Code);
        r.Parameters["flag"] = "false"; Assert.Equal(ErrorCode.PlanStale, Run(s, a, r, "apply", Hash(p), credential: "secret-a").Error!.Code); Assert.DoesNotContain("native", a.Trace);
    }
    [Theory]
    [InlineData("")][InlineData("EXACT-route")][InlineData("exact")][InlineData(" exact-route")]
    public void UndiscoveredOrInexactRoutesCannotApply(string route)
    {
        var a = new Adapter(); var s = new FallbackSession(); var r = Request(); r.Route = route; var p = Run(s, a, r);
        Assert.Equal(route == "", p.Ok); Assert.False(Run(s, a, r, "apply", new string('a', 64)).Ok); Assert.DoesNotContain("native", a.Trace);
    }
    [Theory]
    [InlineData("DownloadPlc")][InlineData("DownloadPlcToFolder")][InlineData("CompilePlcSoftware")][InlineData("ExportPlcBlockDocuments")][InlineData("ImportPlcBlockDocuments")]
    [InlineData("ListVersionControlWorkspaces")][InlineData("GetVersionControlStatus")][InlineData("CreateVersionControlWorkspace")][InlineData("ConnectProjectToWorkspace")][InlineData("SynchronizeVersionControlWorkspace")]
    public void CandidateSchemasAreClosedAndDefaultsHaveNoRetryOrRefresh(string entry)
    {
        var s = FallbackContract.Schema(entry); Assert.False(s.GetProperty("additionalProperties").GetBoolean()); var p = s.GetProperty("properties");
        Assert.Equal("never", p.GetProperty("retryPolicy").GetProperty("default").GetString()); Assert.False(p.GetProperty("refreshReadHandle").GetProperty("default").GetBoolean());
        Assert.Equal("preview", p.GetProperty("mode").GetProperty("default").GetString()); Assert.False(p.GetProperty("confirm").GetProperty("default").GetBoolean());
    }
    [Theory]
    [InlineData("during")][InlineData("after")][InlineData("readback")]
    public void ReadFailuresNeverClaimUnknownWritesOrResetSession(string fault)
    {
        var a = new Adapter { Writes = false }; var s = new FallbackSession(); var r = Request("GetVersionControlStatus"); var p = Run(s, a, r); a.Fault = fault;
        var result = Run(s, a, r, "apply", Hash(p)); Assert.Equal(Outcome.ReadFailed, result.Meta.Outcome); Assert.False(result.Meta.RequiresSessionReset); Assert.False(a.Poisoned);
    }
    private sealed class Channel : IFallbackAdapter, IFallbackBoundary
    {
        internal Adapter Native = new();
        internal string Failure = "";
        public bool Writes => Native.Writes;
        public bool NeedsConfiguration => Native.NeedsConfiguration;
        public bool RequiresOffline => Native.RequiresOffline;
        public FallbackObservation Observe() => Native.Observe();
        public void BeforeAction() => Native.BeforeAction();
        public bool ApplyConfiguration(string route) => Native.ApplyConfiguration(route);
        public FallbackNativeResult Execute(FallbackRequest request, FallbackAttempt evidence) => Native.Execute(request, evidence);
        public void RefreshReadHandle() => Native.RefreshReadHandle();
        public void MarkUncertain() => Native.MarkUncertain();
        public FallbackAttempt Execute(FallbackCheck check)
        {
            if (Failure == "not-dispatched") throw new FallbackNotDispatchedException(new IOException("channel unavailable before dispatch"));
            if (Failure == "dispatch-unacknowledged") throw new IOException("no issuance evidence returned");
            var reply = CandidateExecution.Fallback(this, check);
            if (Failure == "reply-lost") throw new IOException("real reply lost");
            if (Failure == "invalid-reply") return new FallbackAttempt();
            return reply;
        }
    }
    [Theory]
    [InlineData("not-dispatched", true, Outcome.RejectedBeforeOperation, 0, false)]
    [InlineData("dispatch-unacknowledged", true, Outcome.Unknown, 0, true)]
    [InlineData("reply-lost", true, Outcome.Unknown, 1, true)]
    [InlineData("invalid-reply", true, Outcome.Unknown, 1, true)]
    [InlineData("reply-lost", false, Outcome.ReadFailed, 1, false)]
    [InlineData("invalid-reply", false, Outcome.ReadFailed, 1, false)]
    [InlineData("", true, Outcome.Succeeded, 1, false)]
    public void ChannelFaultsNeverInferNonIssuanceFromAMissingReply(string fault, bool writes, Outcome outcome, int calls, bool reset)
    {
        var a = new Channel(); a.Native.Writes = writes; var s = new FallbackSession(); var r = Request(); var p = Run(s, a, r); a.Failure = fault;
        var result = Run(s, a, r, "apply", Hash(p)); Assert.Equal(outcome, result.Meta.Outcome); Assert.Equal(reset, result.Meta.RequiresSessionReset);
        Assert.Equal(calls, a.Native.Trace.Count(x => x == "native")); Assert.False(Run(s, a, r, "apply", Hash(p)).Ok); Assert.Equal(calls, a.Native.Trace.Count(x => x == "native"));
    }
    private static class Selection
    {
        public static void Current() { }
        [BehaviorCandidate("CompilePlcSoftware", "P6-COMPILE", typeof(CompileFallbackContract))] public static void Compile() { }
        [BehaviorCandidate("CompilePlcSoftware", "P6-FALLBACK", typeof(CompileFallbackContract))] public static void Fallback() { }
    }
    [Theory]
    [InlineData(false, false, "Current")][InlineData(true, false, "Compile")][InlineData(false, true, "Fallback")][InlineData(true, true, "Compile")]
    public void OverlappingFallbackUsesTheOwningFamilyWhenBothAreSelected(bool compile, bool fallback, string selected)
    {
        var methods = typeof(Selection).GetMethods(); var current = typeof(Selection).GetMethod("Current")!;
        Assert.Equal(selected, BehaviorCapabilities.SelectMethod("CompilePlcSoftware", current, methods, "21",
            f => (f == "P6-COMPILE" ? compile : fallback) ? BehaviorPolicy.SafeV4 : BehaviorPolicy.Current).Name);
        Assert.Equal(BehaviorPolicy.Current, BehaviorCapabilities.Released("21", "P6-FALLBACK"));
    }
}
