using Siemens.Engineering;
using Siemens.Engineering.VersionControl;
using TiaMcp.Adapters.Contracts.Candidates;
using TiaMcp.Adapters.Native.Vci;
using TiaMcp.Logic.V4;
using Xunit;

public sealed class VciFallbackTests
{
    private const string Project = "C:\\REVIEWED\\PROJECT.AP21";
    private static VciFallbackAdapter Adapter(World world, FallbackRequest request)
        => new(() => new SessionState { ProcessId = 123, ProcessStartUtc = DateTimeOffset.Parse("2026-10-03T00:00:00Z"), ProjectFile = Project, Epoch = 2, Ownership = "borrowed" }, () => world, () => { }, () => world.Poisoned = true, request);
    private static string Hash(Envelope e) => e.Data!.Value.GetProperty("plan").GetProperty("hash").GetString()!;
    private static FallbackRequest Request(string entry = "GetVersionControlStatus") => new() { Entry = entry, Parameters = new() { ["changedOnly"] = "false", ["direction"] = "ProjectToWorkspace", ["workspaceName"] = "new", ["folderPath"] = AppContext.BaseDirectory } };
    private static Envelope Run(FallbackSession session, VciFallbackAdapter a, FallbackRequest r, string mode = "preview", string hash = "") => session.Run(a, "21", r.Entry, "test", r, mode: mode, confirm: true, expectedPlanHash: hash, expectedProjectFile: Project);
    [Theory]
    [InlineData(false)][InlineData(true)]
    public void ActualReadProxyRefreshRequiresExplicitPermission(bool permission)
    {
        var w = new World(); var r = Request("ListVersionControlWorkspaces"); r.RefreshReadHandle = permission; var a = Adapter(w, r); var s = new FallbackSession();
        r.Route = a.Observe().Routes.Single().Id; var p = Run(s, a, r); Assert.True(p.Ok); w.Fault = "stale-name";
        var result = Run(s, a, r, "apply", Hash(p)); Assert.Equal(permission, result.Ok); Assert.Equal(permission ? 2 : 1, w.Services); Assert.False(w.Poisoned);
        Assert.DoesNotContain("sync", w.Trace); Assert.DoesNotContain("export", w.Trace);
    }
    [Theory]
    [InlineData("status-before")][InlineData("status-after")]
    public void AStartedReadCommandCannotBeRefreshedOrReplayed(string fault)
    {
        var w = new World(); var r = Request(); r.RefreshReadHandle = true; var a = Adapter(w, r); var s = new FallbackSession(); r.Route = a.Observe().Routes.Single().Id;
        var p = Run(s, a, r); w.Fault = fault; var result = Run(s, a, r, "apply", Hash(p)); Assert.Equal(Outcome.ReadFailed, result.Meta.Outcome); Assert.False(result.Meta.RequiresSessionReset);
        Assert.Single(w.Trace.Where(x => x == "status")); Assert.Equal(1, w.Services);
    }
    [Theory]
    [InlineData("sync-before")][InlineData("sync-after")]
    public void SyncInterruptionStopsTheBatchWithRealStateAndNoRefresh(string fault)
    {
        var w = new World(); var r = Request("SynchronizeVersionControlWorkspace"); r.RefreshReadHandle = true; var a = Adapter(w, r); var s = new FallbackSession(); r.Route = a.Observe().Routes.Single().Id;
        var p = Run(s, a, r); w.Fault = fault; var result = Run(s, a, r, "apply", Hash(p)); Assert.Equal(Outcome.Unknown, result.Meta.Outcome); Assert.True(result.Meta.RequiresSessionReset); Assert.True(w.Poisoned);
        Assert.Single(w.Trace.Where(x => x == "sync")); Assert.Equal(1, w.Services); Assert.Contains("unknown", result.Data!.Value.GetProperty("attempt").GetProperty("nativeResult").GetRawText());
        Assert.False(Run(s, a, r, "apply", Hash(p)).Ok); Assert.Single(w.Trace.Where(x => x == "sync"));
    }
    [Theory]
    [InlineData("create-before")][InlineData("create-after")]
    public void WorkspaceCreateCannotReplayAnUnknownWrite(string fault)
    {
        var w = new World(); var r = Request("CreateVersionControlWorkspace"); var a = Adapter(w, r); var s = new FallbackSession(); r.Route = a.Observe().Routes.Single().Id;
        var p = Run(s, a, r); w.Fault = fault; var result = Run(s, a, r, "apply", Hash(p)); Assert.Equal(Outcome.Unknown, result.Meta.Outcome); Assert.Single(w.Trace.Where(x => x == "create"));
        Assert.True(result.Meta.RequiresSessionReset); Assert.Equal(fault == "create-after" ? 2 : 1, w.Service.WorkspaceGroup.Workspaces.Count);
    }
    [Fact]
    public void CandidateMappingUsesOneChosenLayoutAndStopsOnTheFirstUnknownWrite()
    {
        var w = new World(); var r = Request("ConnectProjectToWorkspace"); var a = Adapter(w, r); var targets = new[] {
            new VciMappingTarget { Object = new Siemens.Engineering.SW.Blocks.PlcBlock(), Name = "one", RelativePath = "one" },
            new VciMappingTarget { Object = new Siemens.Engineering.SW.Blocks.PlcBlock(), Name = "two", RelativePath = "two" } };
        a.MappingTargets = () => targets;
        var s = new FallbackSession(); r.Route = a.Observe().Routes.Single().Id; var p = Run(s, a, r); Assert.True(p.Ok);
#if STUDIO_VCI_MODERN
        w.Fault = "export-after";
#else
        w.Fault = "map-create-after";
#endif
        var result = Run(s, a, r, "apply", Hash(p)); Assert.Equal(Outcome.Unknown, result.Meta.Outcome); Assert.True(result.Meta.RequiresSessionReset);
        Assert.Single(w.Trace.Where(x => x == "export" || x == "map-create")); Assert.Equal(1, w.Services);
        Assert.Contains("unknown", result.Data!.Value.GetProperty("attempt").GetProperty("nativeResult").GetRawText());
    }
    [Theory]
    [InlineData("CreateVersionControlWorkspace")][InlineData("SynchronizeVersionControlWorkspace")][InlineData("ConnectProjectToWorkspace")]
    public void StaleReadbackAfterAWriteNeverRefreshesTheHandle(string entry)
    {
        var w = new World(); var r = Request(entry); r.RefreshReadHandle = true; var a = Adapter(w, r);
        var targets = new[] { new VciMappingTarget { Object = new Siemens.Engineering.SW.Blocks.PlcBlock(), Name = "one", RelativePath = "one" } };
        a.MappingTargets = () => targets;
        var s = new FallbackSession(); r.Route = a.Observe().Routes.Single().Id; var p = Run(s, a, r); w.Fault = "stale-after-write";
        var result = Run(s, a, r, "apply", Hash(p)); Assert.Equal(Outcome.Unknown, result.Meta.Outcome); Assert.True(result.Meta.RequiresSessionReset); Assert.Equal(1, w.Services);
        int calls = w.Trace.Count(x => x == "create" || x == "sync" || x == "export" || x == "map-create");
        Assert.False(Run(s, a, r, "apply", Hash(p)).Ok); Assert.Equal(calls, w.Trace.Count(x => x == "create" || x == "sync" || x == "export" || x == "map-create"));
    }
    [Fact]
    public void RepeatedStaleReadStopsAfterOneRefresh()
    {
        var w = new World(); var r = Request("ListVersionControlWorkspaces"); r.RefreshReadHandle = true; var a = Adapter(w, r); w.Fault = "stale-always";
        var result = Run(new(), a, r); Assert.False(result.Ok); Assert.Equal(2, w.Services); Assert.False(result.Meta.RequiresSessionReset);
    }
#if STUDIO_VCI_INITIAL
    [Theory]
    [InlineData("set-root-before")][InlineData("set-root-after")]
    public void LegacyRootAssignmentFailureNeverCreatesASecondWorkspace(string fault)
    {
        var w = new World(); var r = Request("CreateVersionControlWorkspace"); var a = Adapter(w, r); var s = new FallbackSession(); r.Route = a.Observe().Routes.Single().Id;
        var p = Run(s, a, r); w.Fault = fault; var result = Run(s, a, r, "apply", Hash(p)); Assert.Equal(Outcome.Unknown, result.Meta.Outcome);
        Assert.Single(w.Trace.Where(x => x == "create")); Assert.Single(w.Trace.Where(x => x == "set-root")); Assert.True(result.Meta.RequiresSessionReset);
    }
#endif
}
