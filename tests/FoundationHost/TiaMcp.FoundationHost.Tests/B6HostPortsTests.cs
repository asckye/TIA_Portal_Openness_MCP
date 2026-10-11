extern alias enginehost;
using System.Text.Json.Nodes;
using TiaMcp.Adapters.Contracts;
using Host = enginehost::TiaMcpServer.ModelContextProtocol;
using Services = enginehost::TiaMcpServer.Siemens.Services;
using Xunit;

public sealed class B6HostPortsTests
{
    private static readonly string[] Releases = { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" };
    public static IEnumerable<object[]> RegistrationCases =>
        from release in Releases
        from family in PortedFamilies.All.Where(f => f.Name is "F06" or "F23" or "F35")
        from tool in family.Tools
        select new object[] { release, tool };

    [Theory, MemberData(nameof(RegistrationCases))]
    public void Registration_requires_an_implemented_release_API(string release, string tool)
    {
        string first = tool switch {
            "GetObjectIdentifier" or "ImportLibraryTypeDocuments" => "20",
            "CompareLibraries" or "CompareLibraryObjects" => "18",
            "ManageProjectCompilationSettings" => "16",
            "ImportMasterCopyFromGlobalLibrary" => "19",
            _ => "14sp1" };
        Assert.Equal(Array.IndexOf(Releases, release) >= Array.IndexOf(Releases, first), PortedFamilies.Available(release, tool));
    }

    [Theory]
    [InlineData("InvokeObject")]
    [InlineData("InvokeService")]
    [InlineData("DescribeService")]
    public void Cross_reference_refusal_precedes_the_worker_and_invalid_target(string operation)
    {
        var service = new Services.ReflectionPortService((_, __) => throw new InvalidOperationException("No native operation may be issued."), () => "");
        var failure = Assert.Throws<global::TiaMcpServer.Siemens.PortalException>(() => {
            if (operation == "InvokeObject") service.InvokeObject("Block", "missing", "GetCrossReferences", allowWrite: true);
            else if (operation == "InvokeService") service.InvokeService("Block", "missing", "CrossReferenceService", "GetCrossReferences", allowWrite: true);
            else service.DescribeService("Block", "missing", "Service");
        });
        Assert.Equal(global::TiaMcpServer.Siemens.PortalErrorCode.InvalidState, failure.Code);
        Assert.Contains("cannot be accessed through reflection", failure.Message);
    }

    public static IEnumerable<object[]> ActionCases =>
        from release in Releases
        from pair in new[] {
            ("ManageLibraryTypeVersion", "edit", "20"), ("ManageLibraryTypeVersion", "release", "20"),
            ("ManageLibraryTypeVersion", "discard", "20"), ("ManageLibraryTypeVersion", "setDefault", "17"),
            ("SynchronizeLibrary", "harmonizeProject", "17"), ("SynchronizeLibrary", "cleanUp", "17"),
            ("ManageGlobalLibrary", "archive", "15.1"), ("ManageGlobalLibrary", "retrieve", "15.1"),
            ("ManageGlobalLibrary", "saveAs", "15.1"), ("ManageLibraryMasterCopy", "compare", "18") }
        select new object[] { release, pair.Item1, pair.Item2, pair.Item3 };

    [Theory, MemberData(nameof(ActionCases))]
    public void Partial_library_actions_refuse_before_worker_dispatch(string release, string operation, string action, string first)
    {
        Assert.Equal(Array.IndexOf(Releases, release) < Array.IndexOf(Releases, first), LibraryCapabilities.Unsupported(release, operation, action) != null);
        if (Array.IndexOf(Releases, release) >= Array.IndexOf(Releases, first)) return;
        using var scope = Host.HardwareContract.UseRelease(release);
        var service = new Services.LibraryPortService((_, _) => throw new Xunit.Sdk.XunitException("Unsupported action reached the worker."), () => "fixture.ap" + release);
        Assert.Throws<NotSupportedException>(() => operation switch {
            "ManageLibraryTypeVersion" => service.ManageLibraryTypeVersion("Type", "1.0", action),
            "SynchronizeLibrary" => service.SynchronizeLibrary(action, "[{\"folder\":\"\"}]"),
            "ManageGlobalLibrary" => service.ManageGlobalLibrary(action),
            _ => service.ManageLibraryMasterCopy("Copy", action) });
    }

    [Theory]
    [InlineData("14sp1")][InlineData("15.1")][InlineData("16")][InlineData("17")]
    [InlineData("18")][InlineData("19")][InlineData("20")][InlineData("21")]
    public void Every_property_and_overload_boundary_is_release_specific(string release)
    {
        int index = Array.IndexOf(Releases, release);
        Assert.Equal(index < 1, EngineeringServicesCapabilities.Unsupported(release, "CompareProjects", "hardware") != null);
        Assert.Equal(index < 2, EngineeringServicesCapabilities.Unsupported(release, "GetProjectSettings", customIdentityKey: "key") != null);
        Assert.Equal(index < 5, EngineeringServicesCapabilities.Unsupported(release, "ManageProjectCompilationSettings", properties: new[] { "IsVirtualPlcDuringBlockCompilationEnabled" }) != null);
        Assert.Equal(index < 6, LibraryCapabilities.Unsupported(release, "ManageLibraryType", "update", properties: new[] { "DoNotUse" }) != null);
        Assert.Equal(index < 4, LibraryCapabilities.Unsupported(release, "ManageLibraryType", "update", properties: new[] { "SetForUpdate" }) != null);
        Assert.Equal(index < 3, LibraryCapabilities.Unsupported(release, "SynchronizeLibrary", "updateProject", forceUpdateMode: "AlwaysSetUpdatedVersionAsDefault") != null);
        Assert.Null(LibraryCapabilities.Unsupported(release, "SynchronizeLibrary", "updateLibrary"));
        Assert.Equal(index < 2, LibraryCapabilities.Unsupported(release, "CreateLibraryMasterCopy", sourceKind: "screen") != null);
    }

    [Theory]
    [InlineData(false)][InlineData(true)]
    public void A_failed_import_requires_reset_only_after_an_import_attempt(bool attempted)
    {
        var reply = new LibraryReply { Import = new GlobalLibraryImportEvidence { Ok = false,
            Attempts = attempted ? new[] { "Try ScreenItems.Import(MasterCopy)" } : new[] { "No compatible method" } } };
        Assert.Equal(attempted, reply.MayHaveChanged);
        Assert.Equal(attempted, reply.RequiresSessionReset);
    }

    [Fact]
    public void Reflection_resolution_retains_native_error_code_and_candidates()
    {
        var service = new Services.ReflectionPortService((_, _) => new JsonObject { ["Failure"] = new JsonObject {
            ["Code"] = "InvalidParams", ["Message"] = "Exact path required", ["Candidates"] = new JsonArray("PLC1", "PLC2") } }, () => "fixture.ap21");
        var error = Assert.Throws<global::TiaMcpServer.Siemens.PortalException>(() => service.DescribeObject("software", "PLC"));
        Assert.Equal("InvalidParams", error.Code.ToString());
        Assert.Equal(new[] { "PLC1", "PLC2" }, error.Candidates);
    }

    [Fact]
    public void Transaction_proxy_keeps_state_reads_commit_and_cleanup_in_order()
    {
        var calls = new List<string>(); bool requested = false;
        var service = new Services.EngineeringServicesPortService((operation, args) => {
            calls.Add(operation.Split('.')[1] + ((string?)args["member"] is { } member ? ":" + member : ""));
            if (operation.EndsWith("BeginEngineeringTransaction")) {
                Assert.Equal("fixture.ap21", (string?)args["expectedProjectFile"]);
                return JsonValue.Create("scope-token");
            }
            Assert.Equal("scope-token", (string?)args["id"]);
            if (operation.EndsWith("CommitEngineeringTransaction")) { requested = true; return null; }
            return JsonValue.Create((string?)args["member"] switch { "CanCommit" => true, "CommitRequested" => requested, _ => false });
        }, () => "fixture.ap21");
        var meta = new JsonObject();
        Assert.True(Host.TransactionExecution.Run(2, () => service.BeginTransaction("Batch"), i => { calls.Add("subcall:" + i); return true; }, meta));
        Assert.Equal(new[] { "BeginEngineeringTransaction", "ReadEngineeringTransaction:IsCancellationRequested", "subcall:0",
            "ReadEngineeringTransaction:IsCancellationRequested", "subcall:1", "ReadEngineeringTransaction:CanCommit",
            "ReadEngineeringTransaction:IsCancellationRequested", "CommitEngineeringTransaction", "ReadEngineeringTransaction:CommitRequested",
            "ReadEngineeringTransaction:CanCommit", "DisposeEngineeringTransaction" }, calls);
        Assert.True((bool)meta["committed"]!);
    }
}
