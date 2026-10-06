using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Adapters.Contracts.Candidates;
using TiaMcp.LegacyHost;
using TiaMcp.Logic.V4;
using Xunit;

public sealed class SaveCloseCandidateTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-10-03T00:00:00Z");
    private const string Project = "C:\\authorized-copy\\Project.ap21";
    private sealed class Adapter : ISaveCloseAdapter, ISaveCloseBoundary
    {
        internal SaveCloseObservation Value = new() { Binding = new() { ProcessId = 12, ProcessStartUtc = Start, ProjectFile = Project, Ownership = "owned", Epoch = 3, WorkerEpoch = 7 },
            Dirty = true, ObjectValidity = "valid", DisconnectSupported = true, WorkerCleanup = "deferred-until-channel-close" };
        internal int Calls, Owner = Environment.CurrentManagedThreadId;
        internal string Fault = "";
        internal bool Poisoned;
        internal string CopyDirectory = "";
        internal Action? Before, During, After;
        internal readonly List<string> Trace = new();
        public SaveCloseObservation Observe()
        {
            Assert.Equal(Owner, Environment.CurrentManagedThreadId); Trace.Add("observe");
            if (Calls > 0 && Fault == "after") throw new IOException("observation lost");
            return JsonSerializer.Deserialize<SaveCloseObservation>(JsonSerializer.Serialize(Value))!;
        }
        public void BeforeAction() { Trace.Add("before"); Before?.Invoke(); if (Fault == "before") throw new IOException("not issued"); }
        private void Native(string action)
        {
            Assert.Equal(Owner, Environment.CurrentManagedThreadId); Calls++; Trace.Add(action); During?.Invoke();
            if (Fault == "during") throw new IOException("native interrupted");
            if (action.EndsWith("Save", StringComparison.Ordinal)) Value.Dirty = false;
            else if (action == "Project.SaveAs") { Value.Binding.ProjectFile = CandidatePrimitives.CanonicalProject(Path.Combine(CopyDirectory, "Project.ap21")); Value.Binding.Epoch++; Value.Dirty = false; }
            else if (action.EndsWith("Close", StringComparison.Ordinal)) { Value.Binding.ProjectFile = null; Value.Binding.Ownership = "none"; Value.Binding.Epoch++; Value.ObjectValidity = "invalid"; Value.Dirty = null; }
            else { Value.Binding = new() { Epoch = Value.Binding.Epoch + 1, WorkerEpoch = Value.Binding.WorkerEpoch }; Value.ObjectValidity = "absent"; Value.Dirty = null; Value.WorkerCleanup = "detached"; }
            After?.Invoke(); if (Fault == "after-native") throw new IOException("ack lost after mutation");
        }
        public void Save() => Native(Value.LocalSession ? "LocalSession.Save" : "Project.Save");
        public void SaveCopy(string directory) { CopyDirectory = directory; Native("Project.SaveAs"); }
        public void Close() => Native(Value.LocalSession ? "LocalSession.Close" : "Project.Close");
        public void Disconnect() => Native("TiaPortal.Dispose");
        public void MarkUncertain() => Poisoned = true;
        public SaveCloseAttempt Execute(SaveCloseCheck check) => CandidateExecution.SaveClose(this, check);
    }
    private static SaveCloseRequest Request(string action = "save") => new() { Action = action };
    private static string Tool(string action) => action == "save" ? "SaveProject" : action == "close" ? "CloseProject" : action == "save-copy" ? "SaveProjectCopy" : "DisconnectPortal";
    private static string Hash(Envelope result) => result.Data!.Value.GetProperty("plan").GetProperty("hash").GetString()!;
    private static Envelope Run(SaveCloseSession s, Adapter a, SaveCloseRequest r, string mode = "preview", string hash = "", bool confirm = true, bool discard = false)
        => s.Run(a, "21", Tool(r.Action), "save-close-test", r, mode, confirm, hash, a.Value.Binding.ProjectFile ?? "", discard);
    [Theory]
    [InlineData(false, "save")][InlineData(true, "save")][InlineData(false, "close")][InlineData(true, "close")]
    public void OnePlannedNativeCallOnTheOwnerThread(bool local, string action)
    {
        var a = new Adapter(); a.Value.LocalSession = local; a.Value.Dirty = action == "save";
        var s = new SaveCloseSession(); var r = Request(action); var p = Run(s, a, r); Assert.True(p.Ok, V4Json.Serialize(p));
        Assert.Equal(Hash(p), Hash(Run(s, a, r))); Assert.Equal(0, a.Calls);
        string native = (local ? "LocalSession." : "Project.") + (action == "save" ? "Save" : "Close");
        var operation = p.Data!.Value.GetProperty("plan").GetProperty("operations")[0].GetProperty("arguments");
        Assert.Equal(native, operation.GetProperty("nativeCall").GetString()); Assert.Equal("owned", operation.GetProperty("ownership").GetString());
        a.Trace.Clear(); var result = Run(s, a, r, "apply", Hash(p)); Assert.True(result.Ok, V4Json.Serialize(result));
        Assert.Equal(new[] { "observe", "observe", "before", "observe", native, "observe" }, a.Trace); Assert.Equal(1, a.Calls);
        Assert.False(Run(s, a, r, "apply", Hash(p)).Ok); Assert.Equal(1, a.Calls);
    }
    [Fact]
    public void BorrowedProjectsCannotCloseButCanBeExplicitlySavedAndDetached()
    {
        var a = new Adapter(); a.Value.Binding.Ownership = "borrowed"; var s = new SaveCloseSession();
        Assert.Equal(ErrorCode.PreconditionFailed, Run(s, a, Request("close")).Error!.Code); Assert.Equal(0, a.Calls);
        var save = Request(); var p = Run(s, a, save); Assert.True(Run(s, a, save, "apply", Hash(p)).Ok);
        var detach = Request("disconnect"); p = Run(s, a, detach); Assert.True(Run(s, a, detach, "apply", Hash(p)).Ok);
        Assert.Equal(2, a.Calls); Assert.DoesNotContain("Project.Close", a.Trace);
    }
    [Fact]
    public void DirtyCloseRequiresSeparateSaveOrFreshDiscardAndOwnConfirmation()
    {
        var a = new Adapter(); var s = new SaveCloseSession(); var close = Request("close");
        Assert.Equal(ErrorCode.PreconditionFailed, Run(s, a, close).Error!.Code); close.SaveChanges = true;
        Assert.Equal(ErrorCode.PreconditionFailed, Run(s, a, close).Error!.Code); Assert.Equal(0, a.Calls); close.SaveChanges = false;
        close.DiscardChanges = true; var p = Run(s, a, close); Assert.True(p.Ok);
        Assert.True(p.Data!.Value.GetProperty("plan").GetProperty("operations")[0].GetProperty("arguments").GetProperty("dirty").GetBoolean());
        Assert.Equal(ErrorCode.ConfirmationRequired, Run(s, a, close, "apply", Hash(p)).Error!.Code); Assert.Equal(0, a.Calls);
        Assert.True(Run(s, a, close, "apply", Hash(p), discard: true).Ok); Assert.Equal(1, a.Calls); Assert.DoesNotContain("Project.Save", a.Trace);
    }
    [Fact]
    public void ExplicitSaveThenFreshCloseHasTwoSeparatePlansAndTwoCalls()
    {
        var a = new Adapter(); var s = new SaveCloseSession(); var r = Request(); var p = Run(s, a, r);
        Assert.True(Run(s, a, r, "apply", Hash(p)).Ok); r = Request("close"); var close = Run(s, a, r);
        Assert.NotEqual(Hash(p), Hash(close)); Assert.True(Run(s, a, r, "apply", Hash(close)).Ok);
        Assert.Equal(new[] { "Project.Save", "Project.Close" }, a.Trace.Where(t => t.StartsWith("Project."))); Assert.Equal(2, a.Calls);
    }
    [Theory]
    [InlineData(false)][InlineData(true)]
    public void ClosedOwnedProjectCanBeExplicitlyDetachedOnAFreshPlan(bool local)
    {
        var a = new Adapter(); a.Value.Dirty = false; a.Value.LocalSession = local; var s = new SaveCloseSession();
        var close = Request("close"); var p = Run(s, a, close);
        Assert.True(Run(s, a, close, "apply", Hash(p)).Ok); Assert.Equal(12, a.Value.Binding.ProcessId);
        var detach = Request("disconnect"); var next = Run(s, a, detach); Assert.True(next.Ok);
        Assert.NotEqual(Hash(p), Hash(next)); Assert.True(Run(s, a, detach, "apply", Hash(next)).Ok);
        Assert.Equal(new[] { local ? "LocalSession.Close" : "Project.Close", "TiaPortal.Dispose" },
            a.Trace.Where(t => t.EndsWith("Close", StringComparison.Ordinal) || t == "TiaPortal.Dispose"));
        Assert.Equal(2, a.Calls); Assert.Null(a.Value.Binding.ProcessId); Assert.Equal("detached", a.Value.WorkerCleanup);
    }
    [Theory]
    [InlineData("save", false, "before")][InlineData("save", false, "during")][InlineData("save", false, "after")][InlineData("save", false, "after-native")]
    [InlineData("save", true, "before")][InlineData("save", true, "during")][InlineData("save", true, "after")][InlineData("save", true, "after-native")]
    [InlineData("close", false, "before")][InlineData("close", false, "during")][InlineData("close", false, "after")][InlineData("close", false, "after-native")]
    [InlineData("close", true, "before")][InlineData("close", true, "during")][InlineData("close", true, "after")][InlineData("close", true, "after-native")]
    [InlineData("disconnect", false, "before")][InlineData("disconnect", false, "during")][InlineData("disconnect", false, "after")][InlineData("disconnect", false, "after-native")]
    public void FaultMatrixNeverRetriesAndRetainsBindingValidityAndCleanup(string action, bool local, string fault)
    {
        var a = new Adapter(); a.Value.LocalSession = local; a.Value.Dirty = false;
        if (action == "disconnect") { a.Value.Binding.ProjectFile = null; a.Value.Binding.Ownership = "none"; a.Value.ObjectValidity = "absent"; }
        var s = new SaveCloseSession(); var r = Request(action); var p = Run(s, a, r); Assert.True(p.Ok); a.Fault = fault;
        var result = Run(s, a, r, "apply", Hash(p)); bool issued = fault != "before";
        Assert.Equal(issued ? Outcome.Unknown : Outcome.RejectedBeforeOperation, result.Meta.Outcome);
        Assert.Equal(issued, result.Meta.RequiresSessionReset); Assert.Equal(issued ? 1 : 0, a.Calls);
        var observed = result.Data!.Value.GetProperty("observedState"); Assert.True(observed.TryGetProperty("binding", out _));
        Assert.True(observed.TryGetProperty("objectValidity", out _)); Assert.True(observed.TryGetProperty("workerCleanup", out _));
        if (issued) { Assert.True(a.Poisoned); Assert.Equal(ErrorCode.SessionResetRequired, Run(s, a, r).Error!.Code); }
        else Assert.False(Run(s, a, r, "apply", Hash(p)).Ok);
        Assert.Equal(issued ? 1 : 0, a.Calls);
    }
    private static void Change(Adapter a, string change)
    {
        var s = a.Value.Binding;
        switch (change)
        {
            case "pid": s.ProcessId++; break; case "start": s.ProcessStartUtc = Start.AddSeconds(1); break;
            case "project": s.ProjectFile = "C:\\other\\Other.ap21"; break; case "epoch": s.Epoch++; break;
            case "worker-epoch": s.WorkerEpoch++; break; case "ownership": s.Ownership = "borrowed"; break;
            case "dirty": a.Value.Dirty = false; break; case "local": a.Value.LocalSession = true; break;
            case "validity": a.Value.ObjectValidity = "invalid"; break;
        }
    }
    [Theory]
    [InlineData("pid", false)][InlineData("start", false)][InlineData("project", false)][InlineData("epoch", false)][InlineData("worker-epoch", false)][InlineData("ownership", false)][InlineData("dirty", false)][InlineData("local", false)][InlineData("validity", false)]
    [InlineData("pid", true)][InlineData("start", true)][InlineData("project", true)][InlineData("epoch", true)][InlineData("worker-epoch", true)][InlineData("ownership", true)][InlineData("dirty", true)][InlineData("local", true)][InlineData("validity", true)]
    public void PreviewAndFinalBoundaryChangesInvalidateWithoutNativeCall(string change, bool final)
    {
        var a = new Adapter(); var s = new SaveCloseSession(); var r = Request(); var p = Run(s, a, r); Assert.True(p.Ok);
        if (final) a.Before = () => Change(a, change); else Change(a, change);
        var result = Run(s, a, r, "apply", Hash(p)); Assert.False(result.Ok); Assert.Equal(0, a.Calls); Assert.False(result.Meta.RequiresSessionReset);
    }
    [Fact]
    public void DiscardArgumentsNeedFreshPlanAndFinalRequestCannotMutate()
    {
        var a = new Adapter(); a.Value.Dirty = false; var s = new SaveCloseSession(); var r = Request("close"); var p = Run(s, a, r);
        r.DiscardChanges = true; Assert.Equal(ErrorCode.PlanStale, Run(s, a, r, "apply", Hash(p), discard: true).Error!.Code);
        p = Run(s, a, r); a.Before = () => r.SaveChanges = true;
        Assert.False(Run(s, a, r, "apply", Hash(p), discard: true).Ok); Assert.Equal(0, a.Calls);
    }
    [Theory]
    [InlineData("save", "pid")][InlineData("save", "project")][InlineData("save", "epoch")][InlineData("save", "worker-epoch")][InlineData("save", "dirty")][InlineData("save", "validity")]
    [InlineData("close", "pid")][InlineData("close", "epoch")][InlineData("close", "worker-epoch")]
    public void ChangedPostNativeStateIsUnknownAndNeverReplayed(string action, string change)
    {
        var a = new Adapter(); a.Value.Dirty = false; var s = new SaveCloseSession(); var r = Request(action); var p = Run(s, a, r);
        a.After = () => { if (change == "dirty") a.Value.Dirty = true; else Change(a, change); };
        Assert.Equal(Outcome.Unknown, Run(s, a, r, "apply", Hash(p)).Meta.Outcome); Assert.Equal(1, a.Calls); Assert.True(s.RequiresSessionReset);
    }
    [Fact]
    public void HashExcludesRequestIdTimestampAndConfirmationAndIsCultureStable()
    {
        var a = new Adapter(); var s = new SaveCloseSession(); var r = Request(); var p = Run(s, a, r);
        var prior = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("tr-TR");
            var other = s.Run(a, "21", "SaveProject", "another-id", r, confirm: false); Assert.Equal(Hash(p), Hash(other));
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = prior; }
        a.During = () => Assert.Equal(1, a.Calls); Assert.True(Run(s, a, r, "apply", Hash(p)).Ok);
    }
    [Fact]
    public void DisconnectNeedsKnownNonOwningAttachmentAndExplicitOwnedClose()
    {
        var a = new Adapter(); var s = new SaveCloseSession(); var r = Request("disconnect");
        Assert.Equal(ErrorCode.PreconditionFailed, Run(s, a, r).Error!.Code); a.Value.Binding.ProjectFile = null; a.Value.Binding.Ownership = "none";
        a.Value.DisconnectSupported = false; Assert.Equal(ErrorCode.UnsupportedCapability, Run(s, a, r).Error!.Code); Assert.Equal(0, a.Calls);
    }
    [Theory]
    [InlineData("SaveProject")][InlineData("CloseProject")][InlineData("DisconnectPortal")][InlineData("SaveProjectCopy")]
    public void CandidateSchemaDefaultsAndClosedShape(string tool)
    {
        var schema = SaveCloseContract.Schema(tool); var props = schema.GetProperty("properties"); Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal("preview", props.GetProperty("mode").GetProperty("default").GetString()); Assert.False(props.GetProperty("confirm").GetProperty("default").GetBoolean());
        if (tool == "CloseProject") foreach (string key in new[] { "saveChanges", "discardChanges", "confirmDiscard" }) Assert.False(props.GetProperty(key).GetProperty("default").GetBoolean());
    }
    [Theory]
    [InlineData("success")][InlineData("before")][InlineData("during")][InlineData("after")][InlineData("after-native")][InlineData("raced-destination")]
    public void SaveCopyIsOneExplicitSaveWithReviewedNewIdentity(string fault)
    {
        string root = Path.Combine(AppContext.BaseDirectory, "save-copy-fixtures", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var r = Request("save-copy"); r.NewProjectPath = Path.Combine(root, "New"); var s = new SaveCloseSession(); var a = new Adapter();
        var p = Run(s, a, r); Assert.True(p.Ok, V4Json.Serialize(p)); a.Fault = fault;
        if (fault == "raced-destination") a.Before = () => Directory.CreateDirectory(r.NewProjectPath);
        var result = Run(s, a, r, "apply", Hash(p));
        Assert.Equal(fault == "before" || fault == "raced-destination" ? 0 : 1, a.Calls);
        Assert.Equal(fault == "success", result.Ok); Assert.Equal(fault == "during" || fault == "after" || fault == "after-native", result.Meta.RequiresSessionReset);
        if (result.Ok) Assert.Equal(CandidatePrimitives.CanonicalProject(Path.Combine(r.NewProjectPath, "Project.ap21")), a.Value.Binding.ProjectFile);
    }
    [Fact]
    public void SaveCopyRefusesLocalSessionAndSaveRequiresExplicitConfirmation()
    {
        string root = Path.Combine(AppContext.BaseDirectory, "save-copy-fixtures", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var a = new Adapter(); a.Value.LocalSession = true; var s = new SaveCloseSession(); var r = Request("save-copy"); r.NewProjectPath = Path.Combine(root, "New");
        Assert.Equal(ErrorCode.UnsupportedCapability, Run(s, a, r).Error!.Code); r = Request(); var p = Run(s, a, r);
        Assert.Equal(ErrorCode.ConfirmationRequired, Run(s, a, r, "apply", Hash(p), confirm: false).Error!.Code); Assert.Equal(0, a.Calls);
    }
    private sealed class Worker(Adapter adapter) : IFoundationWorker
    {
        internal bool Malformed, Forge;
        internal readonly List<string> Actions = new();
        public void Dispose() { }
        public Task<JsonNode?> Call(string operation, JsonObject args, CancellationToken token)
        {
            Assert.Equal("SaveCloseCandidate", operation); token.ThrowIfCancellationRequested();
            var call = args["candidate"]!.Deserialize<SaveCloseCall>()!; Actions.Add(call.Action);
            SaveCloseReply reply;
            if (call.Action == "observe") reply = new() { Observation = adapter.Observe() };
            else { var attempt = CandidateExecution.SaveClose(adapter, call.Check!); reply = new() { Attempt = attempt, RequiresSessionReset = attempt.RequiresSessionReset }; if (Malformed) reply.Attempt = null; if (Forge) reply.Attempt!.After!.Dirty = true; }
            return Task.FromResult(JsonSerializer.SerializeToNode(reply));
        }
    }
    [Theory]
    [InlineData("success")][InlineData("malformed")][InlineData("forged")][InlineData("during")][InlineData("after")][InlineData("before")]
    public void FoundationHasOneSynchronousExecuteAndCrossFamilyUnknownPoison(string fault)
    {
        var a = new Adapter(); var worker = new Worker(a); var s = FoundationCandidateSession.For(worker); var r = Request();
        Envelope Call(string mode, string hash = "") => s.SaveClose("21", "SaveProject", "test", r, mode, true, hash, Project, false, default);
        var p = Call("preview"); Assert.True(p.Ok); worker.Malformed = fault == "malformed"; worker.Forge = fault == "forged"; a.Fault = fault;
        var result = Call("apply", Hash(p)); Assert.Equal(new[] { "observe", "observe", "execute" }, worker.Actions);
        Assert.Equal(fault == "before" ? 0 : 1, a.Calls); bool unknown = fault != "success" && fault != "before";
        Assert.Equal(unknown, result.Meta.RequiresSessionReset); Assert.Equal(fault == "success", result.Ok);
        if (unknown)
        {
            Assert.Equal(ErrorCode.SessionResetRequired, Call("preview").Error!.Code);
            Assert.Equal(ErrorCode.SessionResetRequired, s.Session("21", "ConnectPortal", "test", new(), "preview", false, "", "", false, default).Error!.Code);
            Assert.Equal(ErrorCode.SessionResetRequired, s.Import("21", "ImportPlcBlock", "test", new(), "preview", false, "", "", default).Error!.Code);
            Assert.Equal(ErrorCode.SessionResetRequired, s.Export("21", "ExportPlcBlock", "test", new(), "preview", false, "", "", default).Error!.Code);
        }
    }
    [Theory]
    [InlineData("14sp1")][InlineData("15.1")][InlineData("16")][InlineData("17")][InlineData("18")][InlineData("19")]
    public void FoundationSelectsOnlyFamilyEntriesAndCurrentSchemaRemainsFrozen(string release)
    {
        var worker = new Worker(new Adapter());
        foreach (var source in FoundationTools.Create(worker, release))
        {
            var current = new FoundationV4Tool(source, release, _ => BehaviorPolicy.Current);
            var safe = new FoundationV4Tool(source, release, f => f == "P6-CLOSE" ? BehaviorPolicy.SafeV4 : BehaviorPolicy.Current);
            bool selected = SaveCloseContract.Entries.Contains(current.ProtocolTool.Name);
            if (selected) Assert.Equal(SaveCloseContract.Schema(current.ProtocolTool.Name).GetRawText(), safe.ProtocolTool.InputSchema.GetRawText());
            else Assert.Equal(current.ProtocolTool.InputSchema.GetRawText(), safe.ProtocolTool.InputSchema.GetRawText());
            if (selected) Assert.False(current.ProtocolTool.InputSchema.GetProperty("properties").TryGetProperty("mode", out _));
        }
    }
    [Theory]
    [InlineData("preview", "close", 1, true)]
    [InlineData("apply", "save", 0, false)][InlineData("apply", "save", 1, true)]
    [InlineData("apply", "close", 0, false)][InlineData("apply", "close", 1, false)][InlineData("apply", "close", 2, true)]
    [InlineData("apply", "disconnect", 1, false)]
    public async Task ChannelAcceptsOnlyThePlannedBindingEpochTransition(string mode, string action, int after, bool poison)
    {
        using var feed = new Feed(); using var output = new Reply();
        var identity = new TiaMcp.WorkerChannel.ChannelIdentity("19", new string('a', 64), new string('b', 64), 42, new string('c', 64));
        using var client = new TiaMcp.WorkerChannel.ChannelClient(feed, output, identity);
        feed.Send(JsonSerializer.Serialize(new { jsonrpc = "2.0", method = "hello", @params = new { protocol = 2, releaseKey = "19", workerSha256 = identity.WorkerSha256,
            adapterSha256 = identity.AdapterSha256, pid = 42, nonce = identity.Nonce, bindingEpoch = 0, bound = false } }));
        await client.ConnectAsync(TimeSpan.FromSeconds(3));
        output.Sent = frame => { var request = JsonNode.Parse(frame)!; Assert.Equal("adapter.SaveCloseCandidate", (string?)request["method"]);
            feed.Send(JsonSerializer.Serialize(new { jsonrpc = "2.0", id = request["id"]!.GetValue<long>(), bindingEpochBefore = 0, bindingEpochAfter = after, result = (object?)null })); };
        var args = new JsonObject { ["mode"] = mode, ["candidate"] = JsonSerializer.SerializeToNode(new SaveCloseCall { Action = "execute", Check = new() { Request = Request(action) } }) };
        var call = client.CallAsync("adapter.SaveCloseCandidate", args.ToJsonString(), WorkerClient.BindingChangeFor("SaveCloseCandidate", args), mode == "preview", TimeSpan.FromSeconds(3));
        if (poison) await Assert.ThrowsAsync<TiaMcp.WorkerChannel.ChannelFault>(() => call); else Assert.Equal("null", await call);
        Assert.Equal(poison, client.Poisoned);
    }
    private sealed class Feed : Stream
    {
        private readonly System.Threading.Channels.Channel<byte[]> chunks = System.Threading.Channels.Channel.CreateUnbounded<byte[]>();
        private byte[] current = Array.Empty<byte>(); private int offset;
        internal void Send(string line) => chunks.Writer.TryWrite(System.Text.Encoding.UTF8.GetBytes(line + "\n"));
        public override async Task<int> ReadAsync(byte[] buffer, int index, int count, CancellationToken token)
        {
            if (offset == current.Length) { current = await chunks.Reader.ReadAsync(token); offset = 0; }
            int take = Math.Min(count, current.Length - offset); Array.Copy(current, offset, buffer, index, take); offset += take; return take;
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override bool CanRead => true; public override bool CanWrite => false; public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
    private sealed class Reply : MemoryStream
    {
        internal Action<string>? Sent;
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken token)
        { Sent?.Invoke(System.Text.Encoding.UTF8.GetString(buffer, offset, count)); return Task.CompletedTask; }
    }
}
