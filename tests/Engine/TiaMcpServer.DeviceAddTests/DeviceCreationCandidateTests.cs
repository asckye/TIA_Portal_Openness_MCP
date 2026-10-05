using System.Globalization;
using System.Reflection;
using System.Text.Json.Nodes;
using TiaMcp.Logic.V4;
using Xunit;

public sealed class DeviceCreationCandidateTests
{
    private const string Identifier = "OrderNumber:6ES7 513-1AM03-0AB0/V3.0";
    private sealed class Adapter : IDeviceCreationAdapter
    {
        public int Creates, IdReads, InventoryReads, CatalogReads;
        public int Pid = 42;
        public long Epoch = 1;
        public DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public string Project = @"C:\Tests\Example.ap19";
        public string RootId => "root";
        public string? Fault;
        public Action<Adapter>? Observe;
        public readonly List<DeviceCatalogEntry> Catalog = new() { new() { TypeIdentifier = Identifier, ArticleNumber = "6ES7 513-1AM03-0AB0", Version = "V3.0", Description = "CPU 1513" } };
        public readonly List<DeviceInventoryItem> Items = new() { new() { Id = "first", Name = "PLC_1", ParentId = "root" } };
        public PlanIdentity ReadIdentity() { IdReads++; Observe?.Invoke(this); return new(Pid, Start, DeviceCreationSession.CanonicalProject(Project), Epoch, null, Array.Empty<PlanFile>()); }
        public IReadOnlyList<DeviceCatalogEntry> ReadCatalog(string exact) { CatalogReads++; if (Fault == "catalog") throw new IOException(); return Catalog; }
        public IReadOnlyList<DeviceInventoryItem> ReadInventory() { InventoryReads++; if (Creates > 0 && (Fault == "during-unreadable" || Fault == "after")) throw new IOException(); return Items; }
        public void BeforeCreate() { if (Fault == "before") throw new IOException(); }
        public DeviceInventoryItem Create(string typeIdentifier, string deviceName)
        {
            Creates++;
            Assert.Equal(Identifier, typeIdentifier);
            if (Fault == "during-before-residue") throw new IOException();
            var item = new DeviceInventoryItem { Id = "created", Name = deviceName, ParentId = RootId };
            Items.Add(item);
            if (Fault == "during" || Fault == "during-unreadable") throw new IOException();
            if (Fault == "identity-after") Epoch++;
            if (Fault == "wrong-parent") item.ParentId = "elsewhere";
            if (Fault == "after-return") return new DeviceInventoryItem { Id = "other", Name = deviceName, ParentId = RootId };
            return item;
        }
    }
    private static Envelope Run(DeviceCreationSession session, Adapter adapter, string mode = "preview", string hash = "", bool confirm = false,
        string identifier = Identifier, string name = "PLC_2", string family = "S7-1500", string release = "19", bool foundation = true, string? project = null)
        => session.Run(adapter, release, "CreateHardwareDevice", "test-request", identifier, name, family, mode, confirm, hash, project ?? adapter.Project, foundation);
    private static string Hash(Envelope envelope) => envelope.Data!.Value.GetProperty("plan").GetProperty("hash").GetString()!;
    private static void Rejected(Envelope envelope, ErrorCode code, Adapter adapter)
    {
        Assert.Equal(code, envelope.Error!.Code);
        Assert.Equal(Outcome.RejectedBeforeOperation, envelope.Meta.Outcome);
        Assert.Equal(Execution.NotStarted, envelope.Meta.Execution);
        Assert.False(envelope.Meta.RequiresSessionReset);
        Assert.Equal(0, adapter.Creates);
    }

    [Fact]
    public void PreviewIsStableAcrossCultureAndInventoryOrderAndExcludesConfirmation()
    {
        var session = new DeviceCreationSession(); var adapter = new Adapter();
        adapter.Items.Add(new() { Id = "group", Name = "Group", ParentId = "root", IsGroup = true });
        var old = CultureInfo.CurrentCulture;
        try
        {
            var preview = Run(session, adapter);
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR"); adapter.Items.Reverse();
            Assert.Equal(Hash(preview), Hash(Run(session, adapter, confirm: true)));
            var plan = V4Json.Deserialize<Plan>(preview.Data!.Value.GetProperty("plan").GetRawText());
            Assert.Equal(64, plan.ArgumentsHash.Length); Assert.Equal(64, plan.InventoryHash!.Length);
            Assert.Single(plan.Operations); Assert.Empty(plan.InputHashes); Assert.Equal(42, plan.Identity.ProcessId);
            Assert.Equal(BehaviorPolicy.SafeV4, preview.Meta.BehaviorPolicy); Assert.Equal(Execution.ReadOnly, preview.Meta.Execution);
            Assert.Equal(0, adapter.Creates); Assert.Single(adapter.Catalog);
        }
        finally { CultureInfo.CurrentCulture = old; }
    }

    [Fact]
    public void ApplyCreatesExactlyOnceWithExactArgumentsAndCannotReplay()
    {
        var session = new DeviceCreationSession(); var adapter = new Adapter();
        var hash = Hash(Run(session, adapter));
        var apply = Run(session, adapter, "apply", hash, true);
        Assert.True(apply.Ok); Assert.Equal(Execution.Completed, apply.Meta.Execution); Assert.Equal(1, adapter.Creates);
        Assert.Single(apply.Data!.Value.GetProperty("residueCheck").GetProperty("added").EnumerateArray());
        Assert.Equal(ErrorCode.PlanStale, Run(session, adapter, "apply", hash, true).Error!.Code);
        Assert.Equal(1, adapter.Creates);
    }

    [Theory]
    [InlineData("before", false, "none")]
    [InlineData("catalog", false, "none")]
    [InlineData("during-before-residue", true, "checked")]
    [InlineData("during", true, "checked")]
    [InlineData("during-unreadable", true, "unavailable")]
    [InlineData("identity-after", true, "unavailable")]
    [InlineData("after", true, "unavailable")]
    [InlineData("wrong-parent", true, "checked")]
    [InlineData("after-return", true, "checked")]
    public void FaultsDistinguishPreflightFromIssuedWrites(string fault, bool issued, string residue)
    {
        var session = new DeviceCreationSession(); var adapter = new Adapter();
        var hash = Hash(Run(session, adapter)); adapter.Fault = fault;
        var result = Run(session, adapter, "apply", hash, true);
        Assert.False(result.Ok); Assert.Equal(issued ? 1 : 0, adapter.Creates);
        Assert.Equal(issued, result.Meta.RequiresSessionReset);
        Assert.Equal(issued ? Outcome.Unknown : Outcome.RejectedBeforeOperation, result.Meta.Outcome);
        if (!issued) { Assert.Single(adapter.Items); return; }
        Assert.Equal(residue, result.Data!.Value.GetProperty("residueCheck").GetProperty("status").GetString());
        Assert.Equal(ErrorCode.SessionResetRequired, Run(session, adapter).Error!.Code);
        Assert.Equal(ErrorCode.SessionResetRequired, Run(session, adapter, "apply", hash, true).Error!.Code);
        Assert.Equal(1, adapter.Creates);
    }

    [Theory]
    [InlineData("pid")][InlineData("start")][InlineData("project")][InlineData("epoch")]
    public void IdentityChangesRejectBeforeCreate(string change)
    {
        var session = new DeviceCreationSession(); var adapter = new Adapter(); var hash = Hash(Run(session, adapter));
        if (change == "pid") adapter.Pid++;
        if (change == "start") adapter.Start = adapter.Start.AddSeconds(1);
        if (change == "project") adapter.Project = @"C:\Tests\Other.ap19";
        if (change == "epoch") adapter.Epoch++;
        Rejected(Run(session, adapter, "apply", hash, true), ErrorCode.IdentityMismatch, adapter);
    }

    [Theory]
    [InlineData("catalog-description")][InlineData("catalog-removed")][InlineData("catalog-duplicate")]
    [InlineData("inventory-name")][InlineData("inventory-parent")][InlineData("new-group")][InlineData("new-object")]
    public void CatalogAndInventoryInvalidatePlan(string change)
    {
        var session = new DeviceCreationSession(); var adapter = new Adapter(); var hash = Hash(Run(session, adapter));
        if (change == "catalog-description") adapter.Catalog[0].Description += "changed";
        if (change == "catalog-removed") adapter.Catalog.Clear();
        if (change == "catalog-duplicate") adapter.Catalog.Add(adapter.Catalog[0]);
        if (change == "inventory-name") adapter.Items[0].Name = "renamed";
        if (change == "inventory-parent") adapter.Items[0].ParentId = "group";
        if (change == "new-group") adapter.Items.Add(new() { Id = "group", Name = "New", ParentId = "root", IsGroup = true });
        if (change == "new-object") adapter.Items[0].Id = "replacement";
        Rejected(Run(session, adapter, "apply", hash, true), ErrorCode.PlanStale, adapter);
    }

    [Fact]
    public void ChangeDuringFinalReadRejectsBeforeCreate()
    {
        var session = new DeviceCreationSession(); var adapter = new Adapter(); var hash = Hash(Run(session, adapter));
        adapter.Observe = a => { if (a.IdReads == 4) a.Epoch++; };
        Rejected(Run(session, adapter, "apply", hash, true), ErrorCode.IdentityMismatch, adapter);
    }

    [Fact]
    public void ConfirmationHashAndProjectAreMandatory()
    {
        var session = new DeviceCreationSession(); var adapter = new Adapter(); var hash = Hash(Run(session, adapter));
        Rejected(Run(session, adapter, "apply", hash), ErrorCode.ConfirmationRequired, adapter);
        Rejected(Run(session, adapter, "apply", "", true), ErrorCode.InvalidArgument, adapter);
        Rejected(Run(session, adapter, "apply", new string('a', 64), true), ErrorCode.PlanStale, adapter);
        Rejected(Run(session, adapter, "apply", hash, true, project: ""), ErrorCode.InvalidArgument, adapter);
        Rejected(Run(session, adapter, "apply", hash, true, project: @"C:\Other.ap19"), ErrorCode.IdentityMismatch, adapter);
    }

    [Fact]
    public void ConflictsAreShownInPreviewAndNeverCreate()
    {
        var session = new DeviceCreationSession(); var adapter = new Adapter(); adapter.Items[0].Name = "plc_2";
        var preview = Run(session, adapter); Assert.True(preview.Ok);
        Assert.Single(preview.Data!.Value.GetProperty("nameConflicts").EnumerateArray());
        Rejected(Run(session, adapter, "apply", Hash(preview), true), ErrorCode.AlreadyExists, adapter);
    }

    [Fact]
    public void CatalogMustMatchExactlyOnceAndCannotSubstitute()
    {
        var session = new DeviceCreationSession(); var adapter = new Adapter();
        Rejected(Run(session, adapter, identifier: Identifier.ToLowerInvariant()), ErrorCode.NotFound, adapter);
        adapter.Catalog.Add(adapter.Catalog[0]); Rejected(Run(session, adapter), ErrorCode.TargetAmbiguous, adapter);
        adapter.Catalog.Clear(); Rejected(Run(session, adapter), ErrorCode.NotFound, adapter);
    }

    [Theory]
    [InlineData("14sp1")][InlineData("15.1")][InlineData("16")][InlineData("17")][InlineData("18")][InlineData("20")][InlineData("21")]
    public void FoundationDoesNotExpandReleaseRange(string release)
    { var a = new Adapter(); Rejected(Run(new(), a, release: release), ErrorCode.UnsupportedCapability, a); Assert.Equal(0, a.IdReads); }

    [Theory]
    [InlineData("6ES7516-3AN03-0AB0", "S7-1500", false)]
    [InlineData("6ES7513-1AM03-0AB0", "S7-1200", false)]
    [InlineData("6ES7211-1BE40-0XB0", "S7-1200", true)]
    [InlineData("6ES7513-1AM03-0AB0", "S7-1500", true)]
    [InlineData("6AV2123-3GB32-0AW0", "HMI", false)]
    public void FoundationRetainsModelWhitelist(string article, string family, bool accepted)
    {
        var a = new Adapter(); var row = a.Catalog[0]; row.ArticleNumber = article; row.TypeIdentifier = "OrderNumber:" + article + "/V3.0";
        var result = Run(new(), a, identifier: row.TypeIdentifier, family: family); Assert.Equal(accepted, result.Ok); Assert.Equal(0, a.Creates);
    }

    [Fact]
    public void GeneratedRecordsAndBothSelectorStatesRemainSeparate()
    {
        foreach (var release in new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" })
            foreach (var family in new[] { "DEVICE", "IMPORT", "EXPORT", "SESSION", "CLOSE", "SOURCE", "COMPILE", "FALLBACK" })
                Assert.Equal(BehaviorPolicy.Current, BehaviorCapabilities.Released(release, "P6-" + family));
        var original = typeof(DeviceCreationCandidateTests).GetMethod(nameof(Current), BindingFlags.NonPublic | BindingFlags.Instance)!;
        var candidate = typeof(DeviceCreationCandidateTests).GetMethod(nameof(Candidate), BindingFlags.NonPublic | BindingFlags.Instance)!;
        Assert.Same(original, BehaviorCapabilities.SelectMethod("entry", original, new[] { candidate }, "21", _ => BehaviorPolicy.Current));
        Assert.Same(candidate, BehaviorCapabilities.SelectMethod("entry", original, new[] { candidate }, "21", _ => BehaviorPolicy.SafeV4));
        Assert.Throws<InvalidOperationException>(() => BehaviorCapabilities.SelectMethod("entry", original, new[] { candidate, candidate }, "21"));
    }

    [Fact]
    public void FoundationResponseBoundaryPreservesWhitelistAndVerifiedSuccess()
    {
        var unlisted = new Adapter();
        unlisted.Catalog[0].ArticleNumber = "6 ES7513-1AM03-0AB0";
        unlisted.Catalog[0].TypeIdentifier = "OrderNumber:" + unlisted.Catalog[0].ArticleNumber + "/V3.0";
        Rejected(Run(new(), unlisted, identifier: unlisted.Catalog[0].TypeIdentifier), ErrorCode.UnsupportedCapability, unlisted);
        var session = new DeviceCreationSession(); var adapter = new Adapter();
        var preview = Run(session, adapter);
        var request = new JsonObject { ["requestId"] = "test-request", ["typeIdentifier"] = Identifier, ["deviceName"] = "PLC_2", ["family"] = "S7-1500" };
        var payload = JsonNode.Parse(V4Json.Serialize(preview))!;
        Assert.True(TiaMcp.LegacyHost.DeviceAddContract.ValidateCandidate(payload, request).Ok);
        foreach (string fault in new[] { "model", "issued", "host", "correlation" })
        {
            var invalid = payload.DeepClone();
            if (fault == "model") invalid["data"]!["catalogEntry"]!["articleNumber"] = "6ES7516-3AN02-0AB0";
            if (fault == "issued") invalid["data"]!["createIssued"] = true;
            if (fault == "host") invalid["data"]!["capabilityScope"]!["host"] = "full-engine";
            if (fault == "correlation") invalid["meta"]!["requestId"] = "other";
            Assert.ThrowsAny<Exception>(() => TiaMcp.LegacyHost.DeviceAddContract.ValidateCandidate(invalid, request));
        }
        request["mode"] = "apply"; request["confirm"] = true; request["expectedPlanHash"] = Hash(preview); request["expectedProjectFile"] = adapter.Project;
        var applied = Run(session, adapter, "apply", Hash(preview), true);
        Assert.True(TiaMcp.LegacyHost.DeviceAddContract.ValidateCandidate(JsonNode.Parse(V4Json.Serialize(applied)), request).Ok);
    }
    private void Current() { }
    [BehaviorCandidate("entry", "P6-DEVICE", typeof(DeviceCreationContract))]
    private void Candidate() { }
}
