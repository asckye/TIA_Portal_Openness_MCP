using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Logic.V4;
using TiaMcp.Adapters.Contracts.Candidates;
using TiaMcp.FoundationHost;
using Xunit;

public sealed class PlcImportCandidateTests
{
    private sealed class Adapter : IPlcImportAdapter
    {
        private sealed class CleanupFailureStream : MemoryStream
        {
            internal CleanupFailureStream(byte[] bytes) : base(bytes, false) { }
            protected override void Dispose(bool disposing) { base.Dispose(disposing); throw new IOException("lock cleanup failed"); }
        }
        internal int Calls, FailIndex = -1;
        internal string Fault = "", Group = "root-block-group", Content = "original", Project = @"C:\Test.ap21";
        internal long Epoch = 1;
        internal bool OverwriteCapability;
        internal readonly List<PlcImportObject> Rows = new();
        internal readonly List<string> Trace = new();
        internal string[] Kinds = { "FC", "UDT", "TagTable" };
        internal Action? OnBefore;
        private string ContentHash => PlcImportSession.ByteHash(Encoding.UTF8.GetBytes(Content));
        public CandidateIdentity ReadIdentity() { Trace.Add("identity"); return new(23, DateTimeOffset.Parse("2026-10-03T00:00:00Z", CultureInfo.InvariantCulture), Project, Epoch); }
        public IReadOnlyList<PlcImportInput> ReadInputs(string release, string tool, PlcImportRequest request, IDictionary<string, Stream> locks)
        {
            Trace.Add("inputs"); if (Fault == "unreadable") throw new IOException("locked");
            return Kinds.Select((kind, index) =>
            {
                string path = @"C:\reviewed\" + index + ".xml";
                if (!locks.ContainsKey(path)) locks.Add(path, Fault == "cleanup" ? new CleanupFailureStream(Encoding.UTF8.GetBytes(Content)) : new MemoryStream(Encoding.UTF8.GetBytes(Content), false));
                return new PlcImportInput { Path = path, Files = new[] { path }, ContentHash = ContentHash,
                    Target = new PlcImportObject { Kind = kind, Name = "Item" + index, GroupPath = "", Number = kind == "FC" ? index + 1 : null } };
            }).ToArray();
        }
        private static PlcImportObject Clone(PlcImportObject r) => V4Json.Deserialize<PlcImportObject>(V4Json.Serialize(r));
        public IReadOnlyList<PlcImportObject> ReadInventory()
        {
            Trace.Add("inventory"); if (Fault == "inventory-after" && Calls == FailIndex + 1) throw new IOException("inventory unavailable");
            return Rows.Select(Clone).Reverse().ToArray();
        }
        public string TargetGroupIdentity(PlcImportObject target) { Trace.Add("group"); return Group; }
        public bool SupportsOverwrite(PlcImportInput input) => OverwriteCapability;
        public void BeforeImport(PlcImportInput input)
        {
            Trace.Add("before"); OnBefore?.Invoke();
            if (Calls == FailIndex && Fault == "before") throw new IOException("before import");
        }
        public PlcImportObject Import(PlcImportInput input, bool overwrite)
        {
            Trace.Add("import:" + Calls); int index = Calls++;
            if (index == FailIndex && Fault == "during-before") throw new IOException("native call failed before observable addition");
            var row = Clone(input.Target); row.Id = "object" + index; row.ContentHash = input.ContentHash;
            if (overwrite) Rows.RemoveAll(o => o.Name == row.Name && o.GroupPath == row.GroupPath);
            Rows.Add(row);
            if (index == FailIndex && Fault == "during-after") throw new IOException("native mutated then failed");
            if (index == FailIndex && Fault == "identity-after") Epoch++;
            if (index == FailIndex && Fault == "wrong-parent") row.GroupPath = "Other";
            return Clone(row);
        }
        public string ReadContent(PlcImportInput input, PlcImportObject imported)
        {
            Trace.Add("content:" + (Calls - 1));
            if (Calls == FailIndex + 1 && Fault == "after") throw new IOException("content unavailable");
            return Calls == FailIndex + 1 && Fault == "content-mismatch" ? new string('0', 64) : input.ContentHash;
        }
    }
    private static PlcImportRequest Request(int count = 3) => new() { SoftwarePath = "devices/PLC/CPU", InputPath = @"C:\reviewed", MaxItems = count, ImportOrder = Enumerable.Range(0, count).Select(i => i + ".xml").ToArray() };
    private static Envelope Run(PlcImportSession session, Adapter adapter, PlcImportRequest? request = null, string mode = "preview", string hash = "", string tool = "ImportPlcProgramFromDirectory", string release = "21", bool confirm = true)
        => session.Run(adapter, release, tool, "import-test", request ?? Request(), mode, confirm, hash, adapter.Project);
    private static string Hash(Envelope result) => result.Data!.Value.GetProperty("plan").GetProperty("hash").GetString()!;
    private static JsonObject Data(Envelope result) => JsonNode.Parse(result.Data!.Value.GetRawText())!.AsObject();

    public static IEnumerable<object[]> Faults()
    {
        foreach (var fault in new[] { "before", "during-before", "during-after", "after", "inventory-after", "identity-after", "wrong-parent", "content-mismatch" })
            foreach (int index in new[] { 0, 1, 2 }) yield return new object[] { fault, index };
    }
    [Theory, MemberData(nameof(Faults))]
    public void EveryNativeBoundaryStopsWithoutReplayAndKeepsAllItems(string fault, int index)
    {
        var session = new PlcImportSession(); var adapter = new Adapter(); var preview = Run(session, adapter); Assert.True(preview.Ok);
        Assert.Equal(0, adapter.Calls); adapter.Fault = fault; adapter.FailIndex = index;
        var result = Run(session, adapter, mode: "apply", hash: Hash(preview));
        bool before = fault == "before"; Assert.False(result.Ok);
        Assert.Equal(before ? index == 0 ? Outcome.RejectedBeforeOperation : Outcome.Partial : Outcome.Unknown, result.Meta.Outcome);
        Assert.Equal(!before, result.Meta.RequiresSessionReset); Assert.Equal(index + (before ? 0 : 1), adapter.Calls);
        var items = Data(result)["items"]!.AsArray(); Assert.Equal(3, items.Count);
        for (int i = 0; i < index; i++) Assert.True(items[i]!["result"]!["ok"]!.GetValue<bool>());
        for (int i = index + 1; i < 3; i++) { Assert.Equal("NOT_EXECUTED", (string?)items[i]!["result"]!["error"]!["code"]); Assert.Equal(index, (int?)items[i]!["result"]!["error"]!["details"]!["causeIndex"]); }
        if (!before)
        {
            Assert.NotNull(Data(result)["residueCheck"]); int calls = adapter.Calls;
            Assert.Equal(ErrorCode.SessionResetRequired, Run(session, adapter).Error!.Code); Assert.Equal(calls, adapter.Calls);
        }
        Assert.Equal(index + (before ? 0 : 1), adapter.Trace.Count(t => t.StartsWith("import:", StringComparison.Ordinal)));
    }

    [Fact]
    public void PreviewHashesOriginalFilesAndSuccessReadsEveryContentOnceBeforeNextImport()
    {
        var session = new PlcImportSession(); var adapter = new Adapter(); var preview = Run(session, adapter);
        Assert.True(preview.Ok); Assert.Equal(Execution.ReadOnly, preview.Meta.Execution);
        Assert.Equal(3, Data(preview)["files"]!.AsArray().Count); Assert.Equal(0, adapter.Calls);
        var result = Run(session, adapter, mode: "apply", hash: Hash(preview)); Assert.True(result.Ok); Assert.Equal(3, adapter.Calls);
        Assert.All(Data(result)["items"]!.AsArray(), item => Assert.True(item!["result"]!["data"]!["contentVerified"]!.GetValue<bool>()));
        for (int i = 0; i < 2; i++) Assert.True(adapter.Trace.IndexOf("content:" + i) < adapter.Trace.IndexOf("import:" + (i + 1)));
        Assert.Equal(ErrorCode.PlanStale, Run(session, adapter, mode: "apply", hash: Hash(preview)).Error!.Code); Assert.Equal(3, adapter.Calls);
    }
    [Theory]
    [InlineData("bytes", ErrorCode.PlanStale)] [InlineData("inventory", ErrorCode.PlanStale)]
    [InlineData("group", ErrorCode.PlanStale)] [InlineData("epoch", ErrorCode.IdentityMismatch)]
    [InlineData("project", ErrorCode.IdentityMismatch)] [InlineData("unreadable", ErrorCode.PlanStale)]
    public void ChangedReviewInputsRejectBeforeAnyNativeImport(string change, ErrorCode expected)
    {
        var session = new PlcImportSession(); var adapter = new Adapter(); string hash = Hash(Run(session, adapter));
        if (change == "bytes") adapter.Content = "different";
        if (change == "inventory") adapter.Rows.Add(new PlcImportObject { Id = "other", Name = "Other", Kind = "FC", ContentHash = "changed" });
        if (change == "group") adapter.Group = "different-group";
        if (change == "epoch") adapter.Epoch++;
        if (change == "project") adapter.Project = @"C:\Other.ap21";
        if (change == "unreadable") adapter.Fault = change;
        Assert.Equal(expected, Run(session, adapter, mode: "apply", hash: hash).Error!.Code); Assert.Equal(0, adapter.Calls);
    }
    [Fact]
    public void LastIdentityReadImmediatelyBeforeImportRejectsBindingChange()
    {
        var session = new PlcImportSession(); var adapter = new Adapter(); string hash = Hash(Run(session, adapter)); adapter.OnBefore = () => adapter.Epoch++;
        Assert.Equal(ErrorCode.IdentityMismatch, Run(session, adapter, mode: "apply", hash: hash).Error!.Code); Assert.Equal(0, adapter.Calls);
    }
    [Fact]
    public void HashIsCultureAndInventoryEnumerationIndependentButIncludesOrderAndAllContent()
    {
        var adapter = new Adapter(); var session = new PlcImportSession(); string first = Hash(Run(session, adapter));
        var old = CultureInfo.CurrentCulture; try { CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR"); Assert.Equal(first, Hash(Run(session, adapter))); } finally { CultureInfo.CurrentCulture = old; }
        var request = Request(); request.ImportOrder = request.ImportOrder.Reverse().ToArray(); Assert.NotEqual(first, Hash(Run(session, adapter, request)));
        adapter.Content = "new content"; Assert.NotEqual(first, Hash(Run(session, adapter)));
    }
    [Theory]
    [InlineData("overwrite", ErrorCode.UnsupportedCapability)] [InlineData("compile", ErrorCode.UnsupportedCapability)]
    [InlineData("version", ErrorCode.UnsupportedCapability)] [InlineData("continue", ErrorCode.UnsupportedCapability)]
    [InlineData("technology", ErrorCode.UnsupportedCapability)] [InlineData("group", ErrorCode.InvalidArgument)]
    [InlineData("budget", ErrorCode.LimitExceeded)] [InlineData("confirm", ErrorCode.ConfirmationRequired)]
    public void UnsupportedOrIllegalArgumentsNeverImport(string change, ErrorCode expected)
    {
        var adapter = new Adapter(); var session = new PlcImportSession(); var request = Request();
        if (change == "overwrite") request.Overwrite = true;
        if (change == "compile") request.CompileAfter = true;
        if (change == "version") request.VersionPolicy = "repair";
        if (change == "continue") request.OnError = "continue";
        if (change == "technology") request.TechnologyFolderPath = "TO";
        if (change == "group") request.BlockGroupPath = "../Elsewhere";
        if (change == "budget") request.MaxItems = 257;
        var result = Run(session, adapter, request, mode: change == "confirm" ? "apply" : "preview", confirm: false);
        Assert.Equal(expected, result.Error!.Code); Assert.Equal(0, adapter.Calls);
    }
    [Fact]
    public void ExistingObjectsAreVisibleInPreviewAndRefusedWithoutOverwrite()
    {
        var session = new PlcImportSession(); var adapter = new Adapter(); adapter.Rows.Add(new PlcImportObject { Id = "existing", Kind = "FC", Name = "Item0", Number = 1 });
        var preview = Run(session, adapter); Assert.True(preview.Ok); Assert.Single(Data(preview)["conflicts"]!.AsArray());
        Assert.Equal(ErrorCode.AlreadyExists, Run(session, adapter, mode: "apply", hash: Hash(preview)).Error!.Code); Assert.Equal(0, adapter.Calls);
    }
    [Fact]
    public void CleanupFailureAfterVerifiedImportsCannotEscapeAsAnOperationBeforeWrite()
    {
        var session = new PlcImportSession(); var adapter = new Adapter(); string hash = Hash(Run(session, adapter)); adapter.Fault = "cleanup";
        var result = Run(session, adapter, mode: "apply", hash: hash);
        Assert.Equal(Outcome.Unknown, result.Meta.Outcome); Assert.True(result.Meta.RequiresSessionReset); Assert.Equal(3, adapter.Calls);
        Assert.Equal("failed", (string?)Data(result)["lockCleanup"]); Assert.Equal(3, Data(result)["items"]!.AsArray().Count);
    }
    [Fact]
    public void BeforeCallRefusalConsumesOnlyTheReviewedAttemptAndCannotReplayIt()
    {
        var session = new PlcImportSession(); var adapter = new Adapter(); string hash = Hash(Run(session, adapter)); adapter.Fault = "before"; adapter.FailIndex = 0;
        Assert.False(Run(session, adapter, mode: "apply", hash: hash).Ok); adapter.Fault = "";
        Assert.Equal(ErrorCode.PlanStale, Run(session, adapter, mode: "apply", hash: hash).Error!.Code); Assert.Equal(0, adapter.Calls);
    }
    [Fact]
    public void SupportedOverwriteCannotReplaceAnUnrelatedCrossGroupObject()
    {
        var adapter = new Adapter { OverwriteCapability = true }; adapter.Rows.Add(new PlcImportObject { Id = "unrelated", Name = "Item0", Kind = "FC", Number = 1, GroupPath = "Other" });
        var request = Request(); request.Overwrite = true;
        Assert.Equal(ErrorCode.AlreadyExists, Run(new(), adapter, request).Error!.Code); Assert.Equal(0, adapter.Calls);
    }
    [Fact]
    public void ExplicitSupportedOverwriteVerifiesReplacementAndRetainsOtherInventory()
    {
        var session = new PlcImportSession(); var adapter = new Adapter { OverwriteCapability = true };
        adapter.Rows.Add(new PlcImportObject { Id = "existing", Kind = "FC", Name = "Item0", Number = 1 });
        var request = Request(); request.Overwrite = true; var preview = Run(session, adapter, request);
        Assert.True(Run(session, adapter, request, mode: "apply", hash: Hash(preview)).Ok); Assert.Equal(3, adapter.Rows.Count);
    }

    [Theory]
    [InlineData("14sp1")] [InlineData("15.1")] [InlineData("16")] [InlineData("17")] [InlineData("18")] [InlineData("19")] [InlineData("20")] [InlineData("21")]
    public void EachReleaseKeepsCurrentLedgerAndCandidatePlanIdentity(string release)
    {
        Assert.Equal(BehaviorPolicy.Current, BehaviorCapabilities.Released(release, "P6-IMPORT"));
        var adapter = new Adapter(); var session = new PlcImportSession(); var preview = Run(session, adapter, release: release);
        Assert.Equal(release, preview.Meta.ReleaseKey); Assert.Equal(BehaviorPolicy.SafeV4, preview.Meta.BehaviorPolicy);
        Assert.True(Run(session, adapter, mode: "apply", hash: Hash(preview), release: release).Ok);
    }
    [Theory]
    [InlineData("14sp1")] [InlineData("15.1")] [InlineData("16")] [InlineData("17")]
    [InlineData("18")] [InlineData("19")] [InlineData("20")] [InlineData("21")]
    public void HostAcceptsAuthenticPreviewAndVerifiedApplyEvidence(string release)
    {
        var session = new PlcImportSession(); var adapter = new Adapter(); var request = Request();
        var preview = Run(session, adapter, request, release: release);
        var wire = JsonSerializer.SerializeToNode(request, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })!.AsObject();
        wire["tool"] = "ImportPlcProgramFromDirectory"; wire["requestId"] = "import-test"; wire["mode"] = "preview";
        Assert.True(BatchImportContract.ValidateCandidate(JsonNode.Parse(V4Json.Serialize(preview)), wire).Ok);
        wire["mode"] = "apply"; wire["confirm"] = true; wire["expectedPlanHash"] = Hash(preview); wire["expectedProjectFile"] = adapter.Project;
        var applied = Run(session, adapter, request, "apply", Hash(preview), release: release);
        Assert.True(BatchImportContract.ValidateCandidate(JsonNode.Parse(V4Json.Serialize(applied)), wire).Ok);
        foreach (string field in new[] { "contentVerified", "nativeImportCalls" })
        {
            var forged = JsonNode.Parse(V4Json.Serialize(applied))!;
            forged["data"]!["items"]![0]!["result"]!["data"]![field] = field == "contentVerified" ? JsonValue.Create(false) : JsonValue.Create(2);
            Assert.Throws<InvalidDataException>(() => BatchImportContract.ValidateCandidate(forged, wire));
        }
    }

}
