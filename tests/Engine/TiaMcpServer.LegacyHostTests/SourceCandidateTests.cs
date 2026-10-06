using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Adapters.Contracts.Candidates;
using TiaMcp.LegacyHost;
using TiaMcp.Logic.V4;
using Xunit;

public sealed class SourceCandidateTests
{
    private const string Project = "C:\\authorized-copy\\Project.ap21";
    private sealed class Adapter : ISourceAdapter, ISourceBoundary
    {
        internal SourceObservation Value = new() { Identity = new(12, DateTimeOffset.Parse("2026-10-03T00:00:00Z"), Project, 3), PlcId = "plc-1", SoftwarePath = "devices/Station/CPU", Groups = new[] { "", "Group%2FOne" }, GroupIds = new[] { "root-1", "group-1" } };
        internal string Release = "21", Fault = "";
        internal int Calls, FailIndex = -1;
        internal Action? Before;
        internal readonly List<string> Trace = new();
        private readonly int owner = Environment.CurrentManagedThreadId;
        public SourceObservation Observe()
        {
            Assert.Equal(owner, Environment.CurrentManagedThreadId); Trace.Add("observe");
            if (Calls > 0 && Fault == "after") throw new IOException("readback lost");
            return JsonSerializer.Deserialize<SourceObservation>(JsonSerializer.Serialize(Value))!;
        }
        public void BeforeAction(SourceItem item)
        { Trace.Add("before"); Before?.Invoke(); if (Calls == FailIndex && Fault == "before") throw new IOException("not issued"); }
        private void Native(string action)
        {
            Assert.Equal(owner, Environment.CurrentManagedThreadId); Trace.Add(action); Calls++;
            if (Calls - 1 == FailIndex && Fault == "during") throw new IOException("native interrupted");
        }
        private void After() { if (Calls - 1 == FailIndex && Fault == "after-native") throw new IOException("ack lost after mutation"); }
        public SourceRow Import(SourceItem item, string[] names)
        {
            Native("import:" + item.SourceName); var s = new SourceRow { Id = "source-" + Calls, Name = item.SourceName, GroupPath = item.GroupPath, FilePath = item.FilePath, ContentHash = CandidatePrimitives.ByteHash(File.ReadAllBytes(item.FilePath)), Declarations = names };
            Value.Sources = Value.Sources.Concat(new[] { s }).ToArray(); After(); return s;
        }
        public string[]? Generate(SourceRow row)
        { Native("generate:" + row.Name); Value.Objects = Value.Objects.Concat(row.Declarations).ToArray(); After(); return Release == "14sp1" ? null : row.Declarations; }
        public void Delete(SourceRow row)
        { Native("delete:" + row.Name); Value.Sources = Value.Sources.Where(s => s.Id != row.Id).ToArray(); After(); }
        public SourceAttempt Execute(SourceCheck check, IDictionary<string, Stream> locks) => CandidateExecution.Source(this, check, locks);
    }
    private sealed class Input : IDisposable
    {
        private readonly string root = System.IO.Path.Combine(AppContext.BaseDirectory, "source-candidate-" + Guid.NewGuid().ToString("N"));
        internal string Path { get; }
        internal Input() { Directory.CreateDirectory(root); Path = System.IO.Path.Combine(root, "Exact.Name.scl"); File.WriteAllText(Path, "FUNCTION \"Generated_1\" : Void\nBEGIN\nEND_FUNCTION\n", new System.Text.UTF8Encoding(false)); }
        public void Dispose() => Directory.Delete(root, true);
    }
    private static SourceRequest Request(string action, string file = "", string name = "Exact.Name.scl", string group = "") => new() { Items = new[] { new SourceItem { Action = action, FilePath = file, SourceName = name, GroupPath = group } } };
    private static string Hash(Envelope e) => e.Data!.Value.GetProperty("plan").GetProperty("hash").GetString()!;
    private static Envelope Run(SourceSession s, Adapter a, SourceRequest r, string mode = "preview", string hash = "", bool confirm = true)
        => s.Run(a, a.Release, "ImportPlcExternalSource", "test", r, mode, confirm, hash, Project);
    private static Adapter Existing(string action = "generate")
    {
        var a = new Adapter(); a.Value.Sources = new[] { new SourceRow { Id = "exact-source", Name = "Exact.Name.scl", Declarations = new[] { "block:Generated_1" } } }; return a;
    }
    [Fact]
    public void DeclarationInventoryHandlesMultipleDeclarationsOnOneLineAndSkipsQuotedText()
    {
        var text = "FUNCTION \"New\" : Void BEGIN x := 'FUNCTION Existing'; END_FUNCTION FUNCTION_BLOCK \"Existing\" BEGIN END_FUNCTION_BLOCK";
        Assert.Equal(new[] { "block:New", "block:Existing" }, SourceDeclarations.Read(System.Text.Encoding.UTF8.GetBytes(text)));
        Assert.Throws<CandidateObservationException>(() => SourceDeclarations.Read(System.Text.Encoding.UTF8.GetBytes("FUNCTION \"Escaped$22Name\" : Void")));
        Assert.Throws<CandidateObservationException>(() => SourceDeclarations.Read(System.Text.Encoding.UTF8.GetBytes("(* unterminated")));
    }
    [Fact]
    public void KnownUnrelatedObjectsAndExactGroupNamesSurviveSourceGeneration()
    {
        var a = Existing(); a.Value.Objects = new[] { "block:Unrelated" }; var s = new SourceSession(); var r = Request("generate"); var p = Run(s, a, r);
        Assert.True(p.Ok); Assert.True(Run(s, a, r, "apply", Hash(p)).Ok); Assert.Contains("block:Unrelated", a.Value.Objects);
    }
    [Theory]
    [InlineData("14sp1")][InlineData("21")]
    public void OneReviewedBatchExecutesImportGenerateDeleteInOrder(string release)
    {
        using var input = new Input(); var a = new Adapter { Release = release }; var s = new SourceSession();
        var r = new SourceRequest { Items = new[] { Request("import", input.Path).Items[0], Request("generate").Items[0], Request("delete").Items[0] } };
        var p = Run(s, a, r); Assert.True(p.Ok); var result = Run(s, a, r, "apply", Hash(p)); Assert.True(result.Ok, V4Json.Serialize(result));
        Assert.Equal(3, result.Data!.Value.GetProperty("items").GetArrayLength()); Assert.Empty(a.Value.Sources); Assert.Equal(new[] { "block:Generated_1" }, a.Value.Objects);
        Assert.Equal(new[] { "import:Exact.Name.scl", "generate:Exact.Name.scl", "delete:Exact.Name.scl" }, a.Trace.Where(x => x.Contains(':')));
    }
    [Theory]
    [InlineData(".scl")][InlineData(".awl")][InlineData(".db")][InlineData(".udt")]
    public void OpaqueImportBytesKeepExactExtensionAndUnprovenGenerationIsRefused(string extension)
    {
        using var input = new Input(); string file = System.IO.Path.ChangeExtension(input.Path, extension); File.WriteAllBytes(file, new byte[] { 255, 254, 1 });
        var a = new Adapter(); var s = new SourceSession(); var r = Request("import", file, "Exact.Name" + extension); var p = Run(s, a, r);
        Assert.True(p.Ok); Assert.True(Run(s, a, r, "apply", Hash(p)).Ok); Assert.Equal("Exact.Name" + extension, a.Value.Sources.Single().Name);
        Assert.Equal(ErrorCode.PreconditionFailed, Run(s, a, Request("generate", name: "Exact.Name" + extension)).Error!.Code); Assert.Equal(1, a.Calls);
    }
    [Fact]
    public void GenerationLocksAndRevalidatesTheImportedFile()
    {
        using var input = new Input(); var a = new Adapter(); var s = new SourceSession(); var import = Request("import", input.Path); var p = Run(s, a, import);
        Assert.True(Run(s, a, import, "apply", Hash(p)).Ok); var generate = Request("generate"); Assert.True(Run(s, a, generate).Ok);
        File.AppendAllText(input.Path, "// different input\n"); Assert.Equal(ErrorCode.PlanStale, Run(s, a, generate).Error!.Code); Assert.Equal(1, a.Calls);
    }
    [Fact]
    public void ConfirmationAndChangedBatchOrderRefuseBeforeTheFirstNativeCall()
    {
        var a = new Adapter(); a.Value.Sources = new[] { new SourceRow { Id = "a", Name = "A.scl" }, new SourceRow { Id = "b", Name = "B.scl" } }; var s = new SourceSession();
        var r = new SourceRequest { Items = new[] { Request("delete", name: "A.scl").Items[0], Request("delete", name: "B.scl").Items[0] } }; var p = Run(s, a, r);
        Assert.Equal(ErrorCode.ConfirmationRequired, Run(s, a, r, "apply", Hash(p), false).Error!.Code);
        Array.Reverse(r.Items); Assert.Equal(ErrorCode.PlanStale, Run(s, a, r, "apply", Hash(p)).Error!.Code); Assert.Equal(0, a.Calls);
    }
    [Fact]
    public void RequestMutationInsideFinalPreflightCannotChangeTheReviewedWrite()
    {
        var a = Existing(); var s = new SourceSession(); var r = Request("delete"); var p = Run(s, a, r); a.Before = () => r.Items[0].SourceName = "Other.scl";
        var e = Run(s, a, r, "apply", Hash(p)); Assert.Equal(ErrorCode.PlanStale, e.Error!.Code); Assert.False(e.Meta.RequiresSessionReset); Assert.Equal(0, a.Calls);
    }
    [Theory]
    [InlineData("14sp1")][InlineData("15.1")][InlineData("16")][InlineData("17")][InlineData("18")][InlineData("19")][InlineData("20")][InlineData("21")]
    public void ImportGenerateDeleteAreSeparateConfirmedCallsWithExactNamesAndEvidence(string release)
    {
        using var input = new Input(); var a = new Adapter { Release = release }; var s = new SourceSession();
        foreach (var action in new[] { "import", "generate", "delete" })
        {
            var r = Request(action, action == "import" ? input.Path : ""); var p = Run(s, a, r); Assert.True(p.Ok, V4Json.Serialize(p));
            Assert.Equal(Hash(p), Hash(Run(s, a, r))); Assert.Equal(action, p.Data!.Value.GetProperty("plan").GetProperty("operations")[0].GetProperty("arguments").GetProperty("action").GetString());
            var result = Run(s, a, r, "apply", Hash(p)); Assert.True(result.Ok, V4Json.Serialize(result));
            var child = result.Data!.Value.GetProperty("items")[0].GetProperty("result").GetProperty("data");
            if (action == "generate") { Assert.Equal(release == "14sp1", child.GetProperty("nativeResult").ValueKind == JsonValueKind.Null); Assert.Equal(release != "14sp1", child.GetProperty("observation").ValueKind == JsonValueKind.Null); }
        }
        Assert.Equal(new[] { "import:Exact.Name.scl", "generate:Exact.Name.scl", "delete:Exact.Name.scl" }, a.Trace.Where(x => x.Contains(':'))); Assert.Equal(3, a.Calls);
    }
    [Theory]
    [InlineData("import", "before")][InlineData("import", "during")][InlineData("import", "after-native")][InlineData("import", "after")]
    [InlineData("generate", "before")][InlineData("generate", "during")][InlineData("generate", "after-native")][InlineData("generate", "after")]
    [InlineData("delete", "before")][InlineData("delete", "during")][InlineData("delete", "after-native")][InlineData("delete", "after")]
    public void EveryNativeBoundaryFaultStopsAndNeverReplays(string action, string fault)
    {
        using var input = new Input(); var a = action == "import" ? new Adapter() : Existing(); var s = new SourceSession(); var r = Request(action, action == "import" ? input.Path : ""); var p = Run(s, a, r); Assert.True(p.Ok);
        a.Fault = fault; a.FailIndex = 0; var e = Run(s, a, r, "apply", Hash(p)); Assert.False(e.Ok);
        Assert.Equal(fault != "before", e.Meta.RequiresSessionReset); Assert.Equal(fault == "before" ? 0 : 1, a.Calls);
        Assert.Equal(fault == "before" ? Outcome.RejectedBeforeOperation : Outcome.Unknown, e.Meta.Outcome);
        Assert.False(Run(s, a, r, "apply", Hash(p)).Ok); Assert.Equal(fault == "before" ? 0 : 1, a.Calls);
    }
    [Theory]
    [InlineData(0, "before")][InlineData(1, "before")][InlineData(0, "during")][InlineData(1, "during")]
    [InlineData(0, "after-native")][InlineData(1, "after-native")]
    public void BatchFirstAndMiddleFaultsRetainChildrenAndCauseIndex(int index, string fault)
    {
        var a = new Adapter(); a.Value.Sources = Enumerable.Range(0, 3).Select(i => new SourceRow { Id = "s" + i, Name = "Name" + i + ".scl" }).ToArray(); var s = new SourceSession();
        var r = new SourceRequest { Items = a.Value.Sources.Select(row => new SourceItem { Action = "delete", SourceName = row.Name }).ToArray() };
        var p = Run(s, a, r); Assert.True(p.Ok); a.FailIndex = index; a.Fault = fault;
        var e = Run(s, a, r, "apply", Hash(p)); Assert.Equal(fault == "before" ? index == 0 ? Outcome.RejectedBeforeOperation : Outcome.Partial : Outcome.Unknown, e.Meta.Outcome);
        var items = e.Data!.Value.GetProperty("items"); Assert.Equal(3, items.GetArrayLength()); Assert.Equal(index + (fault == "before" ? 0 : 1), a.Calls);
        for (int i = index + 1; i < 3; i++) { Assert.Equal("NOT_EXECUTED", items[i].GetProperty("result").GetProperty("error").GetProperty("code").GetString()); Assert.Equal(index, items[i].GetProperty("result").GetProperty("error").GetProperty("details").GetProperty("causeIndex").GetInt32()); }
    }
    [Theory]
    [InlineData("pid")][InlineData("start")][InlineData("project")][InlineData("epoch")][InlineData("plc")][InlineData("group")][InlineData("source")][InlineData("name")][InlineData("objects")]
    public void PlanAndFinalBoundaryRecheckAllIdentityFields(string field)
    {
        foreach (bool boundary in new[] { false, true })
        {
            var a = Existing(); var s = new SourceSession(); var r = Request("delete"); var p = Run(s, a, r); Assert.True(p.Ok);
            void Change()
            {
                switch (field) { case "pid": a.Value.Identity.ProcessId++; break; case "start": a.Value.Identity.ProcessStartUtc = a.Value.Identity.ProcessStartUtc!.Value.AddSeconds(1); break; case "project": a.Value.Identity.ProjectFile = "C:\\changed\\Other.ap21"; break; case "epoch": a.Value.Identity.BindingEpoch++; break; case "plc": a.Value.PlcId += "changed"; break; case "group": a.Value.GroupIds[0] += "changed"; break; case "source": a.Value.Sources[0].Id += "changed"; break; case "name": a.Value.Sources[0].Name += "changed"; break; default: a.Value.Objects = new[] { "block:Other" }; break; }
            }
            if (boundary) a.Before = Change; else Change();
            var e = Run(s, a, r, "apply", Hash(p)); Assert.False(e.Ok); Assert.Equal(0, a.Calls); Assert.False(e.Meta.RequiresSessionReset);
        }
    }
    [Theory]
    [InlineData("Exact.Name")][InlineData("exact.name.scl")][InlineData("Exact.Name.awl")][InlineData(" Exact.Name.scl")]
    public void MissingDeleteAndExtensionOrCaseAliasesAreRefused(string name)
    { var a = Existing(); var s = new SourceSession(); Assert.Equal(ErrorCode.NotFound, Run(s, a, Request("delete", name: name)).Error!.Code); Assert.Equal(0, a.Calls); }
    [Fact]
    public void SourceGroupIsOpaqueAndGenerationNeverOverwrites()
    {
        var a = Existing(); a.Value.Sources[0].GroupPath = "Group%2FOne"; var s = new SourceSession();
        Assert.Equal(ErrorCode.NotFound, Run(s, a, Request("delete", group: "Group/One")).Error!.Code);
        Assert.True(Run(s, a, Request("delete", group: "Group%2FOne")).Ok);
        a.Value.Objects = new[] { "block:generated_1" }; Assert.Equal(ErrorCode.AlreadyExists, Run(s, a, Request("generate", group: "Group%2FOne")).Error!.Code); Assert.Equal(0, a.Calls);
    }
    [Fact]
    public void HashIsStableAcrossCultureRequestIdAndChangesWithFileBytesAndOrder()
    {
        using var input = new Input(); var a = new Adapter(); var s = new SourceSession(); var r = Request("import", input.Path); var p = Run(s, a, r); var culture = CultureInfo.CurrentCulture;
        try { CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR"); Assert.Equal(Hash(p), Hash(s.Run(a, "21", "ImportPlcExternalSource", "new-id", r))); }
        finally { CultureInfo.CurrentCulture = culture; }
        File.AppendAllText(input.Path, "// changed bytes\n"); Assert.NotEqual(Hash(p), Hash(Run(s, a, r))); Assert.Equal(ErrorCode.PlanStale, Run(s, a, r, "apply", Hash(p)).Error!.Code); Assert.Equal(0, a.Calls);
    }
    [Theory]
    [InlineData(0)][InlineData(1)][InlineData(2)]
    public void EmptyAndExactQueryPathsAreSharedByReadAndWriteWithoutFallback(int count)
    {
        var rows = Enumerable.Range(0, count).Select(i => new PlcPathTarget<object> { Value = new object(), Path = "devices/Station%2F" + i + "/CPU", QueryNames = new[] { "Structure_" + i } }).ToArray();
        if (count == 1) Assert.Same(PlcPathSelection.Select(rows, "").Value, PlcPathSelection.Select(rows, "Structure_0").Value);
        else { var e = Assert.Throws<PlcPathException>(() => PlcPathSelection.Select(rows, "")); Assert.True(e.Ambiguous); Assert.Equal(count, e.Candidates.Length); }
        if (count > 0) Assert.Same(PlcPathSelection.Select(rows, rows[0].Path).Value, PlcPathSelection.Select(rows, "Structure_0").Value);
        foreach (var wrong in new[] { "wrong", "Structure_0/extra", "structure_0", " Structure_0", "devices/Station/0/CPU" }) Assert.False(Assert.Throws<PlcPathException>(() => PlcPathSelection.Select(rows, wrong)).Ambiguous);
        var switched = new[] { new PlcPathTarget<object> { Value = new object(), Path = "devices/Other/CPU" } }; Assert.Throws<PlcPathException>(() => PlcPathSelection.Select(switched, "Structure_0"));
    }
    private sealed class Worker(Adapter adapter) : IFoundationWorker
    {
        internal bool Malformed, Forge;
        internal readonly List<string> Actions = new();
        public void Dispose() { }
        public Task<JsonNode?> Call(string operation, JsonObject args, CancellationToken token)
        {
            Assert.Equal("SourceCandidate", operation); var call = args["candidate"]!.Deserialize<SourceCall>()!; Actions.Add(call.Action); SourceReply reply;
            if (call.Action == "observe") reply = new() { Observation = adapter.Observe() };
            else
            {
                var locks = new Dictionary<string, Stream>();
                try { foreach (var f in call.Check!.Files) locks.Add(f.Path, File.OpenRead(f.Path)); var a = CandidateExecution.Source(adapter, call.Check, locks); reply = new() { Attempt = a, RequiresSessionReset = a.RequiresSessionReset }; if (Malformed) reply.Attempt = null; if (Forge && a.After != null) a.After.PlcId = "forged"; }
                finally { foreach (var stream in locks.Values) stream.Dispose(); }
            }
            return Task.FromResult(JsonSerializer.SerializeToNode(reply));
        }
    }
    [Theory]
    [InlineData("success")][InlineData("malformed")][InlineData("forged")][InlineData("during")][InlineData("before")]
    public void FoundationUsesOneAtomicExecuteAndPoisonsOtherCandidateFamilies(string fault)
    {
        var a = Existing(); var worker = new Worker(a); var session = FoundationCandidateSession.For(worker); var r = Request("delete");
        Envelope Call(string mode, string hash = "") => session.Source("21", "DeletePlcExternalSource", "test", r, mode, true, hash, Project, default);
        var p = Call("preview"); Assert.True(p.Ok); a.Fault = fault; a.FailIndex = 0; worker.Malformed = fault == "malformed"; worker.Forge = fault == "forged";
        var e = Call("apply", Hash(p)); Assert.Equal(new[] { "observe", "observe", "execute" }, worker.Actions); Assert.Equal(fault == "before" ? 0 : 1, a.Calls);
        Assert.Equal(fault != "success" && fault != "before", e.Meta.RequiresSessionReset);
        if (e.Meta.RequiresSessionReset) { Assert.Equal(ErrorCode.SessionResetRequired, Call("preview").Error!.Code); Assert.Equal(ErrorCode.SessionResetRequired, session.SaveClose("21", "SaveProject", "test", new(), "preview", false, "", "", false, default).Error!.Code); }
    }
}
