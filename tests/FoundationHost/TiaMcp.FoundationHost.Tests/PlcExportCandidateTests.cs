using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Adapters.Contracts.Candidates;
using TiaMcp.FoundationHost;
using TiaMcp.Logic.V4;
using Xunit;

public sealed class PlcExportCandidateTests
{
    private sealed class Adapter : IPlcExportAdapter
    {
        internal int Calls, Count = 3, FailIndex = -1;
        internal string Fault = "", Content = "original", Project = @"C:\Test.ap21";
        internal long Epoch = 1;
        internal bool Overwrite;
        internal Action? OnBefore;
        internal Action? OnExport;
        internal readonly List<string> Trace = new();
        public CandidateIdentity ReadIdentity()
        {
            Trace.Add("identity");
            if (Fault == "after" && Calls == FailIndex + 1) throw new IOException("readback unavailable");
            return new(23, DateTimeOffset.Parse("2026-10-03T00:00:00Z"), Project, Epoch);
        }
        public IReadOnlyList<PlcExportObject> ReadObjects(string tool, PlcExportRequest request)
        {
            Trace.Add("objects"); return Enumerable.Range(0, Count).Select(i => new PlcExportObject { Id = "id" + i, Name = "Item" + i, Path = "Item" + i,
                Kind = "FC", Consistent = true, ContentHash = PlcImportSession.ByteHash(Encoding.UTF8.GetBytes(Content)), ContentObservation = "readable" }).ToArray();
        }
        public bool SupportsOverwrite(string tool) => Overwrite;
        public void BeforeExport(PlcExportObject item)
        {
            Trace.Add("before"); OnBefore?.Invoke();
            if (Calls == FailIndex && Fault == "before") throw new UnauthorizedAccessException("access denied before native");
        }
        public void Export(PlcExportObject item, string stage, bool documents)
        {
            Trace.Add("export:" + Calls); int index = Calls++;
            OnExport?.Invoke();
            if (index == FailIndex && Fault == "during-before") throw new IOException("disk full before observable output");
            File.WriteAllText(documents ? Path.Combine(stage, item.Name + ".s7dcl") : stage, "<Document>" + Content + "</Document>");
            if (documents) File.WriteAllText(Path.Combine(stage, item.Name + ".s7res"), "resources");
            if (index == FailIndex && Fault == "during-after") throw new IOException("disk full after partial output");
            if (index == FailIndex && Fault == "missing-output") File.Delete(stage);
            if (index == FailIndex && Fault == "identity-after") Epoch++;
        }
    }
    private sealed class Files : IPlcExportFiles
    {
        internal int Publishes, FailIndex = -1;
        internal string Fault = "";
        private readonly PlcExportFiles real = new();
        public CandidateFile Observe(string path) => real.Observe(path);
        public string Stage(string target, bool docs)
        {
            if (Fault == "stage" && Publishes == FailIndex) throw new IOException("disk full while staging");
            return real.Stage(target, docs);
        }
        public void Publish(string stage, string target, bool docs, bool overwrite)
        {
            int index = Publishes++;
            if (index == FailIndex && Fault == "access") throw new UnauthorizedAccessException("denied");
            if (index == FailIndex && Fault == "disk") throw new IOException("disk full");
            // This seam models a known atomic publication; production rename is separately checked.
            if (docs) Directory.Move(stage, target);
            else File.Copy(stage, target, overwrite);
            if (index == FailIndex && Fault == "after-publish") throw new IOException("lost acknowledgement");
            if (index == FailIndex && Fault == "readback") File.WriteAllText(target, "corrupt");
        }
    }
    private static string Root() { string path = Path.Combine(AppContext.BaseDirectory, "export-fixtures", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); return path; }
    private static PlcExportRequest Request(string root, int count = 3) => new() { SoftwarePath = "PLC", OutputPath = root, MaxItems = count };
    private static string Hash(Envelope e) => e.Data!.Value.GetProperty("plan").GetProperty("hash").GetString()!;
    private static JsonObject Data(Envelope e) => JsonNode.Parse(e.Data!.Value.GetRawText())!.AsObject();
    private static Envelope Run(PlcExportSession s, Adapter a, PlcExportRequest r, Files f, string mode = "preview", string hash = "", bool confirm = true, string tool = "ExportPlcBlocks", string release = "21")
        => s.Run(a, release, tool, "export-test", r, mode, confirm, hash, a.Project, f);

    [Theory]
    [InlineData("Failure", false)]
    [InlineData("PartialSuccess", true)]
    [InlineData("987654", true)]
    [InlineData(null, true)]
    public void NativeDocumentResultSurvivesTheCandidateBoundary(string? state, bool reset)
    {
        var a = new Adapter { Count = 1 }; var f = new Files(); var s = new PlcExportSession(); var r = Request(Path.Combine(Root(), "documents"), 1);
        var preview = Run(s, a, r, f, tool: "ExportPlcBlockDocuments");
        Assert.True(preview.Ok);
        a.OnExport = () => throw new TiaMcp.Adapters.Contracts.NativeResultException(TiaMcp.Adapters.Contracts.NativeResultStates.Documents, state);
        var result = Run(s, a, r, f, "apply", Hash(preview), tool: "ExportPlcBlockDocuments");
        Assert.Equal(reset ? Outcome.Unknown : Outcome.Failed, result.Meta.Outcome);
        Assert.Equal(reset ? ErrorCode.OutcomeUnknown : ErrorCode.NativeOperationFailed, result.Error!.Code);
        Assert.Equal(reset, result.Meta.RequiresSessionReset);
        Assert.Equal(state, (string?)Data(result)["residue"]?["nativeState"]);
        Assert.Equal(1, a.Calls); Assert.Equal(0, f.Publishes);
    }

    public static IEnumerable<object[]> Faults()
    {
        foreach (string fault in new[] { "before", "during-before", "during-after", "after", "identity-after", "missing-output", "stage", "access", "disk", "after-publish", "readback" })
            foreach (int index in new[] { 0, 1, 2 }) yield return new object[] { fault, index };
    }
    [Theory, MemberData(nameof(Faults))]
    public void EveryItemStopsAtEachBoundaryAndRetainsEvidence(string fault, int index)
    {
        var a = new Adapter(); var f = new Files(); var s = new PlcExportSession(); var r = Request(Root());
        var preview = Run(s, a, r, f); Assert.True(preview.Ok); Assert.Empty(Directory.GetFileSystemEntries(r.OutputPath)); Assert.Equal(0, a.Calls);
        a.Fault = f.Fault = fault; a.FailIndex = f.FailIndex = index;
        var result = Run(s, a, r, f, "apply", Hash(preview));
        bool before = fault is "before" or "stage", knownPublish = fault is "access" or "disk";
        bool unknown = !before && !knownPublish;
        Assert.False(result.Ok); Assert.Equal(unknown, result.Meta.RequiresSessionReset);
        Assert.Equal(unknown ? Outcome.Unknown : index > 0 ? Outcome.Partial : knownPublish ? Outcome.Failed : Outcome.RejectedBeforeOperation, result.Meta.Outcome);
        Assert.Equal(index + (before ? 0 : 1), a.Calls);
        var items = Data(result)["items"]!.AsArray(); Assert.Equal(3, items.Count);
        for (int i = 0; i < index; i++) Assert.True((bool)items[i]!["result"]!["ok"]!);
        for (int i = index + 1; i < 3; i++) { Assert.Equal("NOT_EXECUTED", (string?)items[i]!["result"]!["error"]!["code"]); Assert.Equal(index, (int?)items[i]!["result"]!["error"]!["details"]!["causeIndex"]); }
        Assert.NotNull(Data(result)["residue"]); Assert.Equal(a.Calls, a.Trace.Count(t => t.StartsWith("export:", StringComparison.Ordinal)));
        int calls = a.Calls;
        if (unknown) Assert.Equal(ErrorCode.SessionResetRequired, Run(s, a, r, f).Error!.Code);
        else Assert.False(Run(s, a, r, f, "apply", Hash(preview)).Ok);
        Assert.Equal(calls, a.Calls);
    }
    [Theory]
    [InlineData("objects")]
    [InlineData("identity")]
    [InlineData("destination")]
    [InlineData("arguments")]
    public void PlansAreStableAndInvalidateEveryReviewedInput(string change)
    {
        var a = new Adapter(); var f = new Files(); var s = new PlcExportSession(); var r = Request(Root());
        string hash = Hash(Run(s, a, r, f)); Assert.Equal(hash, Hash(Run(s, a, r, f)));
        if (change == "objects") a.Content = "changed";
        if (change == "identity") a.Epoch++;
        if (change == "destination") File.WriteAllText(Path.Combine(r.OutputPath, "Item0.xml"), "original-destination");
        if (change == "arguments") r.PreservePath = true;
        Assert.Equal(change == "identity" ? ErrorCode.IdentityMismatch : ErrorCode.PlanStale, Run(s, a, r, f, "apply", hash).Error!.Code); Assert.Equal(0, a.Calls);
    }
    [Theory]
    [InlineData("objects")]
    [InlineData("identity")]
    [InlineData("destination")]
    public void FinalSynchronousRecheckPreventsNativeCalls(string change)
    {
        var a = new Adapter(); var f = new Files(); var s = new PlcExportSession(); var r = Request(Root()); string hash = Hash(Run(s, a, r, f));
        a.OnBefore = () => { if (change == "objects") a.Content = "changed"; if (change == "identity") a.Epoch++; if (change == "destination") File.WriteAllText(Path.Combine(r.OutputPath, "Item0.xml"), "raced"); };
        var result = Run(s, a, r, f, "apply", hash); Assert.False(result.Ok); Assert.Equal(0, a.Calls); Assert.False(result.Meta.RequiresSessionReset);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PublishFailurePreservesAnExistingDestinationAndStaging(bool overwrite)
    {
        var a = new Adapter { Overwrite = overwrite }; var f = new Files { Fault = "access", FailIndex = 0 }; var s = new PlcExportSession(); var r = Request(Root(), 1); a.Count = 1; r.Overwrite = overwrite;
        string target = Path.Combine(r.OutputPath, "Item0.xml"); File.WriteAllText(target, "original");
        string hash = Hash(Run(s, a, r, f)); var result = Run(s, a, r, f, "apply", hash);
        Assert.Equal("original", File.ReadAllText(target)); Assert.False(result.Ok); Assert.Equal(overwrite ? 1 : 0, a.Calls);
        if (overwrite) Assert.True(File.Exists((string)Data(result)["residue"]!["stagingPath"]!));
    }
    [Fact]
    public void LockedDestinationRejectsBeforeNativeAndConfirmationIsMandatory()
    {
        var a = new Adapter(); var f = new Files(); var s = new PlcExportSession(); var r = Request(Root()); string hash = Hash(Run(s, a, r, f));
        Assert.Equal(ErrorCode.ConfirmationRequired, Run(s, a, r, f, "apply", hash, false).Error!.Code);
        string target = Path.Combine(r.OutputPath, "Item0.xml"); File.WriteAllText(target, "original"); using var locked = new FileStream(target, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.False(Run(s, a, r, f, "apply", hash).Ok); Assert.Equal(0, a.Calls);
    }
    [Fact]
    public void SuccessVerifiesFilesAndNativeCallOrderWithoutReplay()
    {
        var a = new Adapter(); var f = new Files(); var s = new PlcExportSession(); var r = Request(Root()); string hash = Hash(Run(s, a, r, f));
        var result = Run(s, a, r, f, "apply", hash); Assert.True(result.Ok); Assert.Equal(3, a.Calls);
        foreach (var node in Data(result)["items"]!.AsArray()) { Assert.True((bool)node!["result"]!["data"]!["contentVerified"]!); Assert.True((bool)node["result"]!["data"]!["pathVerified"]!); }
        int before = a.Trace.IndexOf("before"); Assert.Equal(new[] { "before", "identity", "objects", "export:0", "identity" }, a.Trace.Skip(before).Take(5));
        Assert.False(Run(s, a, r, f, "apply", hash).Ok); Assert.Equal(3, a.Calls);
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
    public void ReleasedPolicyStaysCurrentAndSchemaIsCandidateOnly(string release)
    {
        Assert.Equal(BehaviorPolicy.Current, BehaviorCapabilities.Released(release, "P6-EXPORT"));
        foreach (string entry in PlcExportContract.Entries)
        {
            var schema = PlcExportContract.Schema(entry, release); var p = schema.GetProperty("properties");
            Assert.False(p.GetProperty("overwrite").GetProperty("default").GetBoolean()); Assert.Equal("preview", p.GetProperty("mode").GetProperty("default").GetString());
        }
    }
    [Fact]
    public void RelativeOutputsNeedWorkspaceAndCannotEscapeIt()
    {
        string root = Root(); Assert.Equal(Path.Combine(root, "out"), PlcExportSession.Resolve("out", root));
        Assert.Throws<PlcExportRejection>(() => PlcExportSession.Resolve("../escape", root)); Assert.Throws<PlcExportRejection>(() => PlcExportSession.Resolve("out", ""));
    }
    [Fact]
    public void DestinationAppearingDuringNativeExportIsPreserved()
    {
        var a = new Adapter(); var f = new Files(); var s = new PlcExportSession(); var r = Request(Root()); string hash = Hash(Run(s, a, r, f));
        string target = Path.Combine(r.OutputPath, "Item0.xml"); a.OnExport = () => File.WriteAllText(target, "raced-original");
        var result = Run(s, a, r, f, "apply", hash);
        Assert.Equal(Outcome.Failed, result.Meta.Outcome); Assert.Equal(1, a.Calls); Assert.Equal(0, f.Publishes); Assert.Equal("raced-original", File.ReadAllText(target));
        Assert.True(File.Exists((string)Data(result)["residue"]!["stagingPath"]!));
    }
    [Theory]
    [InlineData("20")]
    [InlineData("21")]
    public void DocumentPreviewIncludesDirectoryAndCodeResourceIdentities(string release)
    {
        var a = new Adapter { Count = 1 }; var f = new Files(); var s = new PlcExportSession(); var r = Request(Path.Combine(Root(), "documents"), 1);
        var preview = Run(s, a, r, f, tool: "ExportPlcBlockDocuments", release: release); Assert.True(preview.Ok);
        Assert.False(Directory.Exists(r.OutputPath)); Assert.Equal(0, a.Calls); Assert.Equal(3, Data(preview)["destinations"]!.AsArray().Count);
        a.Fault = "during-after"; a.FailIndex = 0;
        var result = Run(s, a, r, f, "apply", Hash(preview), tool: "ExportPlcBlockDocuments", release: release);
        Assert.Equal(Outcome.Unknown, result.Meta.Outcome); Assert.False(Directory.Exists(r.OutputPath));
        Assert.True(Directory.Exists((string)Data(result)["residue"]!["stagingPath"]!));
    }
    [Theory]
    [InlineData("overwrite")]
    [InlineData("onError")]
    [InlineData("documents")]
    public void UnsupportedCapabilitiesNeverIssueNativeExport(string capability)
    {
        var a = new Adapter { Count = 1 }; var f = new Files(); var s = new PlcExportSession(); var r = Request(Root(), 1);
        if (capability == "overwrite") r.Overwrite = true;
        if (capability == "onError") r.OnError = "continue";
        var result = Run(s, a, r, f, tool: capability == "documents" ? "ExportPlcBlockDocuments" : "ExportPlcBlock", release: "19");
        Assert.Equal(ErrorCode.UnsupportedCapability, result.Error!.Code); Assert.Equal(0, a.Calls);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ProductionPublicationNeverDeletesAConflictingOrLockedOriginal(bool overwrite)
    {
        string root = Root(), target = Path.Combine(root, "target.xml"), stage = Path.Combine(root, "stage.xml");
        File.WriteAllText(target, "original"); File.WriteAllText(stage, "staged");
        var publication = new PlcExportFiles();
        using (var locked = overwrite ? new FileStream(target, FileMode.Open, FileAccess.ReadWrite, FileShare.None) : null)
            Assert.ThrowsAny<IOException>(() => publication.Publish(stage, target, false, overwrite));
        Assert.Equal("original", File.ReadAllText(target)); Assert.Equal("staged", File.ReadAllText(stage));
    }
    private sealed class Worker(Adapter adapter, bool malformed) : IFoundationWorker
    {
        public Task<JsonNode?> Call(string operation, JsonObject args, CancellationToken token)
        {
            Assert.Equal("ExportPlcCandidate", operation); var c = args["candidate"]!.Deserialize<ExportCandidateCall>()!; var r = new ExportCandidateReply();
            switch (c.Action)
            {
                case "identity": r.Identity = adapter.ReadIdentity(); break;
                case "objects": r.Objects = adapter.ReadObjects(c.Tool, c.Request).ToArray(); break;
                case "overwrite": r.OverwriteSupported = adapter.SupportsOverwrite(c.Tool); break;
                case "execute": r.Attempt = CandidateExecution.Export(adapter, c.Check!); r.RequiresSessionReset = r.Attempt.RequiresSessionReset; break;
            }
            var node = JsonSerializer.SerializeToNode(r)!;
            if (malformed && c.Action == "execute") node["Attempt"]!["Staged"] = new JsonArray(new JsonObject());
            return Task.FromResult<JsonNode?>(node);
        }
        public void Dispose() { }
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FoundationUsesOneAtomicDispatchAndPoisonsCrossFamilySession(bool malformed)
    {
        var a = new Adapter { Count = 1, Fault = malformed ? "" : "during-after", FailIndex = 0 }; using var w = new Worker(a, malformed); var s = FoundationCandidateSession.For(w); var r = Request(Root(), 1);
        var preview = s.Export("19", "ExportPlcBlocks", "remote", r, "preview", false, "", "", default); Assert.True(preview.Ok);
        var result = s.Export("19", "ExportPlcBlocks", "remote", r, "apply", true, Hash(preview), a.Project, default);
        Assert.Equal(Outcome.Unknown, result.Meta.Outcome); Assert.Equal(1, a.Calls);
        Assert.Equal(ErrorCode.SessionResetRequired, s.Export("19", "ExportPlcBlocks", "remote", r, "preview", false, "", "", default).Error!.Code);
        Assert.Equal(ErrorCode.SessionResetRequired, s.Import("19", "ImportPlcBlock", "remote", new(), "preview", false, "", "", default).Error!.Code);
    }
}
