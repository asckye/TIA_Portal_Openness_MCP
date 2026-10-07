using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Adapters.Contracts.Candidates;
using TiaMcp.Logic.V4;
using Xunit;

namespace TiaMcp.LegacyHost.Tests;

public sealed class SessionCandidateTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-10-03T00:00:00Z");
    private sealed class Adapter : ISessionCandidateAdapter, ISessionCandidateBoundary
    {
        internal SessionObservation Value = new() { Processes = new[] { new SessionProcess { ProcessId = 12, ProcessStartUtc = Start, Complete = true }, new SessionProcess { ProcessId = 99, ProcessStartUtc = Start, Complete = true } }, UpgradeSupported = true };
        internal Action? AtBefore, AtNative, AfterNative;
        internal string Fault = "";
        internal int Calls, Owner = Environment.CurrentManagedThreadId;
        internal bool Poisoned;
        internal readonly List<string> Trace = new();
        public SessionObservation Observe()
        {
            Assert.Equal(Owner, Environment.CurrentManagedThreadId); Trace.Add("observe");
            if (Calls > 0 && Fault == "after") throw new IOException("readback lost");
            return JsonSerializer.Deserialize<SessionObservation>(JsonSerializer.Serialize(Value))!;
        }
        public void BeforeAction() { Trace.Add("before"); AtBefore?.Invoke(); if (Fault == "before") throw new IOException("before issuance"); }
        public void Attach(SessionRequest r) => Native(r, null, "none");
        public void Bind(SessionRequest r) => Native(r, r.ProjectPath, "borrowed");
        public void Open(SessionRequest r) => Native(r, r.Upgrade == "allow" ? Path.ChangeExtension(r.CopyPath, ".ap21") : r.ProjectPath, "owned");
        private void Native(SessionRequest r, string? file, string ownership)
        {
            Assert.Equal(Owner, Environment.CurrentManagedThreadId); Calls++; Trace.Add(r.Action); AtNative?.Invoke();
            if (Fault == "during") throw new IOException("native fault");
            if (Fault == "timeout") throw new TimeoutException("unrelated native message");
            Value.State = new() { ProcessId = r.ProcessId, ProcessStartUtc = r.ProcessStartUtc, ProjectFile = file == null ? null : SessionCandidateSession.PathValue(file), Ownership = ownership,
                Epoch = Value.State.Epoch + 1, WorkerEpoch = Value.State.WorkerEpoch };
            if (file != null) Value.Processes[0].ProjectFiles = new[] { SessionCandidateSession.PathValue(file) };
            AfterNative?.Invoke();
        }
        public void MarkUncertain() { Poisoned = true; }
        public SessionAttempt Execute(SessionCheck check) => CandidateExecution.Session(this, check);
    }
    private static SessionRequest Request(string action = "attach", string project = "") => new() { Action = action, ProcessId = 12, ProcessStartUtc = Start, ProjectPath = project };
    private static string Hash(Envelope result) => result.Data!.Value.GetProperty("plan").GetProperty("hash").GetString()!;
    private static Envelope Run(SessionCandidateSession session, Adapter adapter, SessionRequest request, string mode = "preview", string hash = "", bool confirm = true, bool confirmUpgrade = false)
        => session.Run(adapter, "21", request.Action == "attach" ? "ConnectPortal" : request.Action == "bind" ? "ConnectProject" : "OpenProject", "session-test", request, mode, confirm, hash,
            request.Action == "attach" ? "" : request.Upgrade == "allow" ? Path.ChangeExtension(request.CopyPath, ".ap21") : request.ProjectPath, confirmUpgrade);
    private static void Attached(Adapter adapter) => adapter.Value.State = new() { ProcessId = 12, ProcessStartUtc = Start, Epoch = 1 };
    private static string Project(string extension = ".ap21")
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "session-fixtures", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "Project" + extension); File.WriteAllText(path, "original"); return path;
    }
    [Theory]
    [InlineData("borrowed",false)][InlineData("owned",false)][InlineData("none",true)]
    public void OpenRefusalNamesAlreadyOpenOrBorrowedProject(string ownership,bool processOnly)
    {
        var a=new Adapter();Attached(a);string path=SessionCandidateSession.PathValue(@"C:\Fixture\Project.ap21");
        if(processOnly) a.Value.Processes[0].ProjectFiles=new[]{path};
        else { a.Value.State.ProjectFile=path;a.Value.State.Ownership=ownership; }
        foreach(string mode in new[]{"preview","apply"})
        {
            var result=Run(new SessionCandidateSession(),a,Request("open",path),mode,new string('a',64));
            Assert.Equal(ErrorCode.PreconditionFailed,result.Error!.Code);Assert.Contains("already open",result.Error.Message);
            if(ownership=="borrowed") Assert.Contains("borrowed",result.Error.Message);
            Assert.Equal(Execution.NotStarted,result.Meta.Execution);Assert.Equal(0,a.Calls);Assert.False(a.Poisoned);
        }
    }

    [Fact]
    public void AttachDoesNotBindOrStartAndConsumesOnce()
    {
        var s = new SessionCandidateSession(); var a = new Adapter(); var r = Request();
        var p = Run(s, a, r); Assert.True(p.Ok); Assert.Equal(0, a.Calls);
        Assert.Equal(Hash(p), Hash(Run(s, a, r))); Assert.Equal(2, p.Data!.Value.GetProperty("candidateProcesses").GetArrayLength());
        var result = Run(s, a, r, "apply", Hash(p)); Assert.True(result.Ok); Assert.Equal(1, a.Calls); Assert.Null(a.Value.State.ProjectFile);
        Assert.Equal("none", a.Value.State.Ownership); Assert.Equal(1, a.Value.State.Epoch);
        Assert.False(Run(s, a, r, "apply", Hash(p)).Ok); Assert.Equal(1, a.Calls);
    }
    [Theory]
    [InlineData("pid")][InlineData("start")][InlineData("project")][InlineData("epoch")][InlineData("worker-epoch")][InlineData("ownership")]
    public void IdentityChangesBeforeApplyIssueNothing(string change)
    {
        var s = new SessionCandidateSession(); var a = new Adapter(); var r = Request(); var p = Run(s, a, r);
        Change(a, change); var result = Run(s, a, r, "apply", Hash(p)); Assert.False(result.Ok); Assert.Equal(0, a.Calls); Assert.False(result.Meta.RequiresSessionReset);
    }
    private static void Change(Adapter a, string change)
    {
        switch (change)
        {
            case "pid": a.Value.Processes[0].ProcessId = 13; break;
            case "start": a.Value.Processes[0].ProcessStartUtc = Start.AddSeconds(1); break;
            case "project": a.Value.Processes[0].ProjectFiles = new[] { @"C:\switched.ap21" }; break;
            case "epoch": a.Value.State.Epoch++; break;
            case "worker-epoch": a.Value.State.WorkerEpoch++; break;
            case "ownership": a.Value.State.Ownership = "unknown"; break;
        }
    }
    [Theory]
    [InlineData("start")][InlineData("project")][InlineData("epoch")][InlineData("worker-epoch")][InlineData("ownership")]
    public void FinalRecheckAndNativeCallAreSynchronous(string change)
    {
        var s = new SessionCandidateSession(); var a = new Adapter(); var r = Request(); var p = Run(s, a, r); a.AtBefore = () => Change(a, change);
        var result = Run(s, a, r, "apply", Hash(p)); Assert.False(result.Ok); Assert.Equal(ErrorCode.IdentityMismatch, result.Error!.Code); Assert.Equal(0, a.Calls);
    }
    [Theory]
    [InlineData("attach", "before")][InlineData("attach", "during")][InlineData("attach", "after")][InlineData("attach", "timeout")]
    [InlineData("open", "before")][InlineData("open", "during")][InlineData("open", "after")]
    [InlineData("upgrade", "before")][InlineData("upgrade", "during")][InlineData("upgrade", "after")]
    [InlineData("bind", "before")][InlineData("bind", "during")][InlineData("bind", "after")]
    public void FaultBeforeDuringAfterHasNoRetry(string action, string fault)
    {
        var s = new SessionCandidateSession(); var a = new Adapter(); var r = Request(action == "upgrade" ? "open" : action, action == "attach" ? "" : Project(action == "upgrade" ? ".ap20" : ".ap21"));
        if (action != "attach") Attached(a);
        if (action == "bind") { r.ReuseOpen = true; a.Value.Processes[0].ProjectFiles = new[] { SessionCandidateSession.PathValue(r.ProjectPath) }; }
        if (action == "upgrade") { r.Upgrade = "allow"; r.CopyPath = Project(".ap20"); }
        var p = Run(s, a, r); Assert.True(p.Ok, V4Json.Serialize(p)); a.Fault = fault;
        var result = Run(s, a, r, "apply", Hash(p), confirmUpgrade: true);
        Assert.Equal(fault == "before" ? Outcome.RejectedBeforeOperation : Outcome.Unknown, result.Meta.Outcome);
        Assert.Equal(fault == "before" ? 0 : 1, a.Calls); Assert.Equal(fault != "before", result.Meta.RequiresSessionReset);
        if (fault != "before") { Assert.True(a.Poisoned); Assert.Equal(ErrorCode.SessionResetRequired, Run(s, a, r).Error!.Code); }
        Assert.Equal(fault == "before" ? 0 : 1, a.Calls);
        if (fault == "timeout") { Assert.Contains("Yes / Yes to all", result.Error!.Message); Assert.Equal(SessionPrimitives.ConfirmationReason, ((OutcomeUnknownDetails)result.Error.Details).Reason); }
    }
    [Fact]
    public void BorrowingRequiresExplicitReuseAndNoSilentSwitch()
    {
        var a = new Adapter(); Attached(a); string path = SessionCandidateSession.PathValue(Project()); a.Value.Processes[0].ProjectFiles = new[] { path };
        var s = new SessionCandidateSession(); var r = Request("bind", path);
        Assert.Equal(ErrorCode.PreconditionFailed, Run(s, a, r).Error!.Code); r.ReuseOpen = true;
        var p = Run(s, a, r); Assert.True(p.Ok); var result = Run(s, a, r, "apply", Hash(p)); Assert.True(result.Ok); Assert.Equal("borrowed", a.Value.State.Ownership);
        Assert.Equal(ErrorCode.PreconditionFailed, Run(s, a, Request("open", path)).Error!.Code); Assert.Equal(1, a.Calls);
    }
    [Fact]
    public void UpgradeNeedsAnIdenticalIndependentCopyAndSeparateConfirmation()
    {
        var a = new Adapter(); Attached(a); var s = new SessionCandidateSession(); var r = Request("open", Project(".ap20"));
        Assert.Equal(ErrorCode.UnsupportedCapability, Run(s, a, r).Error!.Code); r.Upgrade = "allow";
        Assert.Equal(ErrorCode.InvalidArgument, Run(s, a, r).Error!.Code); r.CopyPath = r.ProjectPath;
        Assert.False(Run(s, a, r).Ok); r.CopyPath = Project(".ap20");
        var p = Run(s, a, r); Assert.True(p.Ok); Assert.Equal(ErrorCode.ConfirmationRequired, Run(s, a, r, "apply", Hash(p)).Error!.Code); Assert.Equal(0, a.Calls);
        var result = Run(s, a, r, "apply", Hash(p), confirmUpgrade: true); Assert.True(result.Ok, V4Json.Serialize(result)); Assert.Equal(1, a.Calls);
        Assert.Equal("original", File.ReadAllText(r.ProjectPath)); Assert.Equal(SessionCandidateSession.PathValue(Path.ChangeExtension(r.CopyPath, ".ap21")), a.Value.State.ProjectFile);
    }
    [Theory]
    [InlineData("source")][InlineData("copy")][InlineData("capability")]
    public void UpgradePlanInvalidatesOnFileOrNativeSupportChange(string change)
    {
        var a = new Adapter(); Attached(a); var s = new SessionCandidateSession(); var r = Request("open", Project(".ap20")); r.Upgrade = "allow"; r.CopyPath = Project(".ap20");
        var p = Run(s, a, r); Assert.True(p.Ok);
        if (change == "capability") a.Value.UpgradeSupported = false; else File.WriteAllText(change == "source" ? r.ProjectPath : r.CopyPath, "changed");
        Assert.False(Run(s, a, r, "apply", Hash(p), confirmUpgrade: true).Ok); Assert.Equal(0, a.Calls);
    }
    [Fact]
    public void MissingConfirmationAndImplicitStartIssueNothing()
    {
        var a = new Adapter(); var s = new SessionCandidateSession(); var r = Request(); var p = Run(s, a, r);
        Assert.Equal(ErrorCode.ConfirmationRequired, Run(s, a, r, "apply", Hash(p), confirm: false).Error!.Code);
        r.StartNew = true; Assert.Equal(ErrorCode.UnsupportedCapability, Run(s, a, r).Error!.Code); Assert.Equal(0, a.Calls);
    }
    [Fact]
    public void FoundationLocalSessionExecutionGateIsPreserved()
    {
        var a = new Adapter(); Attached(a); var s = new SessionCandidateSession(); var r = Request("open", Project(".als21"));
        Assert.Equal(ErrorCode.UnsupportedCapability, Run(s, a, r).Error!.Code); Assert.Equal(0, a.Calls);
        a.Value.LocalSessionOpenSupported = true;
        var p = Run(s, a, r); Assert.True(p.Ok); a.Value.LocalSessionOpenSupported = false;
        Assert.False(Run(s, a, r, "apply", Hash(p)).Ok); Assert.Equal(0, a.Calls);
    }
    [Theory]
    [InlineData(false, false)][InlineData(false, true)][InlineData(true, false)][InlineData(true, true)]
    public void ConfirmationReasonIsTypedAndNeverGuessedFromText(bool timeout, bool matching)
    {
        Assert.Equal(timeout && matching ? SessionPrimitives.ConfirmationReason : null, SessionPrimitives.TimeoutReason(timeout, matching));
        Assert.False(SessionPrimitives.IsTimeout(new IOException("Attach timed out; Openness confirmation")));
        var error = new Error("test", new OutcomeUnknownDetails("attach", new Dictionary<string, JsonElement>(), timeout && matching ? SessionPrimitives.ConfirmationReason : null));
        var read = V4Json.Deserialize<Error>(V4Json.Serialize(error)); Assert.Equal(error.Code, read.Code);
        Assert.Equal(timeout && matching, V4Json.Serialize(error).Contains("\"reason\""));
    }
    [Theory]
    [InlineData("ConnectPortal")][InlineData("ConnectIsolatedPortal")][InlineData("ConnectProject")][InlineData("AttachOpenProject")][InlineData("OpenProject")]
    public void CandidateSchemaHasExactDefaults(string tool)
    {
        var props = SessionCandidateContract.Schema(tool).GetProperty("properties");
        Assert.False(props.GetProperty("startNew").GetProperty("default").GetBoolean()); Assert.False(props.GetProperty("reuseOpen").GetProperty("default").GetBoolean());
        Assert.Equal("reject", props.GetProperty("upgrade").GetProperty("default").GetString()); Assert.Equal("preview", props.GetProperty("mode").GetProperty("default").GetString());
    }

    private sealed class Worker(Adapter adapter) : IFoundationWorker
    {
        internal readonly List<string> Actions = new();
        internal bool Malformed;
        internal bool InvalidReason;
        public void Dispose() { }
        public System.Threading.Tasks.Task<JsonNode?> Call(string operation, JsonObject args, System.Threading.CancellationToken token)
        {
            Assert.Equal("SessionCandidate", operation); token.ThrowIfCancellationRequested();
            var call = args["candidate"]!.Deserialize<SessionCandidateCall>()!; Actions.Add(call.Action);
            SessionCandidateReply reply;
            if (call.Action == "observe") reply = new() { Observation = adapter.Observe() };
            else
            {
                var attempt = CandidateExecution.Session(adapter, call.Check!);
                reply = new() { Attempt = attempt, RequiresSessionReset = attempt.RequiresSessionReset };
                if (Malformed) reply.Attempt = null;
                if (InvalidReason) reply.Attempt!.Reason = "untrusted-message-text";
            }
            return System.Threading.Tasks.Task.FromResult(JsonSerializer.SerializeToNode(reply));
        }
    }
    [Fact]
    public void FoundationUsesOneAtomicExecuteAndPoisonsAllCandidateFamilies()
    {
        var adapter = new Adapter(); var worker = new Worker(adapter); var session = FoundationCandidateSession.For(worker); var request = Request();
        Envelope Run(string mode, string hash = "") => session.Session("21", "ConnectPortal", "foundation-test", request, mode, true, hash, "", false, default);
        var preview = Run("preview"); Assert.True(preview.Ok); Assert.Equal(new[] { "observe" }, worker.Actions);
        worker.Malformed = true;
        var result = Run("apply", Hash(preview)); Assert.Equal(Outcome.Unknown, result.Meta.Outcome); Assert.Equal(1, adapter.Calls);
        Assert.Equal(new[] { "observe", "observe", "execute" }, worker.Actions);
        Assert.Equal(ErrorCode.SessionResetRequired, Run("preview").Error!.Code);
        Assert.Equal(ErrorCode.SessionResetRequired, session.Device("19", "id", "type", "name", "S7-1500", "preview", false, "", "", default).Error!.Code);
        Assert.Equal(ErrorCode.SessionResetRequired, session.Import("21", "ImportPlcBlock", "id", new(), "preview", false, "", "", default).Error!.Code);
        Assert.Equal(ErrorCode.SessionResetRequired, session.Export("21", "ExportPlcBlock", "id", new(), "preview", false, "", "", default).Error!.Code);
        Assert.Equal(3, worker.Actions.Count);
    }
    [Theory]
    [InlineData("project")][InlineData("start")]
    public void ProjectOrPidReuseDuringAttachIsUnknown(string change)
    {
        var s = new SessionCandidateSession(); var a = new Adapter(); var r = Request(); var p = Run(s, a, r);
        a.AfterNative = () => Change(a, change);
        var result = Run(s, a, r, "apply", Hash(p)); Assert.Equal(Outcome.Unknown, result.Meta.Outcome); Assert.Equal(1, a.Calls);
    }
    [Fact]
    public void MalformedWorkerReasonRetainsUnknownEnvelopeAndReset()
    {
        var adapter = new Adapter(); var worker = new Worker(adapter); var session = FoundationCandidateSession.For(worker); var request = Request();
        var preview = session.Session("21", "ConnectPortal", "reason-test", request, "preview", false, "", "", false, default); Assert.True(preview.Ok);
        adapter.Fault = "timeout"; worker.InvalidReason = true;
        var result = session.Session("21", "ConnectPortal", "reason-test", request, "apply", true, Hash(preview), "", false, default);
        Assert.Equal(Outcome.Unknown, result.Meta.Outcome); Assert.True(result.Meta.RequiresSessionReset); Assert.Equal(1, adapter.Calls);
        Assert.Null(((OutcomeUnknownDetails)result.Error!.Details).Reason);
    }
    [Fact]
    public void NativeBoundaryRejectsMutatedRequestWithoutIssuance()
    {
        var s = new SessionCandidateSession(); var a = new Adapter(); var r = Request(); var p = Run(s, a, r);
        a.AtBefore = () => r.ProcessId = 99;
        var result = Run(s, a, r, "apply", Hash(p)); Assert.False(result.Ok); Assert.Equal(0, a.Calls);
    }
    [Fact]
    public void OriginalTreeChangedDuringUpgradePoisonsSession()
    {
        var a = new Adapter(); Attached(a); var s = new SessionCandidateSession(); var r = Request("open", Project(".ap20")); r.Upgrade = "allow"; r.CopyPath = Project(".ap20");
        var p = Run(s, a, r); Assert.True(p.Ok); a.AtNative = () => File.WriteAllText(r.ProjectPath, "external-change");
        Assert.Equal(Outcome.Unknown, Run(s, a, r, "apply", Hash(p), confirmUpgrade: true).Meta.Outcome); Assert.True(s.RequiresSessionReset); Assert.Equal(1, a.Calls);
    }
    [Theory]
    [InlineData(0, false)][InlineData(1, false)][InlineData(2, true)]
    public async System.Threading.Tasks.Task SessionApplyAcceptsOnlyUnchangedOrOneAdvancedWorkerEpoch(int after, bool poison)
        => await CheckChannelEpoch("apply", after, poison);
    [Fact]
    public async System.Threading.Tasks.Task SessionPreviewCannotAdvanceWorkerEpoch() => await CheckChannelEpoch("preview", 1, true);
    private static async System.Threading.Tasks.Task CheckChannelEpoch(string mode, int after, bool poison)
    {
        using var input = new SessionFeedStream(); using var output = new SessionReplyStream();
        var identity = new TiaMcp.WorkerChannel.ChannelIdentity("19", new string('a', 64), new string('b', 64), 42, new string('c', 64));
        using var client = new TiaMcp.WorkerChannel.ChannelClient(input, output, identity);
        input.Feed(JsonSerializer.Serialize(new { jsonrpc = "2.0", method = "hello", @params = new { protocol = 2, releaseKey = "19", workerSha256 = identity.WorkerSha256,
            adapterSha256 = identity.AdapterSha256, pid = 42, nonce = identity.Nonce, bindingEpoch = 0, bound = false } }));
        await client.ConnectAsync(TimeSpan.FromSeconds(3));
        output.OnWrite = frame =>
        {
            var request = JsonNode.Parse(frame)!; Assert.Equal(0, request["bindingEpoch"]!.GetValue<int>());
            Assert.Equal("adapter.SessionCandidate", request["method"]!.GetValue<string>());
            input.Feed(JsonSerializer.Serialize(new { jsonrpc = "2.0", id = request["id"]!.GetValue<long>(), bindingEpochBefore = 0, bindingEpochAfter = after, result = (object?)null }));
        };
        var args = new JsonObject { ["mode"] = mode };
        var call = client.CallAsync("adapter.SessionCandidate", args.ToJsonString(), WorkerClient.BindingChangeFor("SessionCandidate", args), mode == "preview", TimeSpan.FromSeconds(3));
        if (poison) await Assert.ThrowsAsync<TiaMcp.WorkerChannel.ChannelFault>(() => call);
        else { Assert.Equal("null", await call); Assert.Equal(after, client.BindingEpoch); }
        Assert.Equal(poison, client.Poisoned);
    }
    private sealed class SessionFeedStream : Stream
    {
        private readonly System.Threading.Channels.Channel<byte[]> chunks = System.Threading.Channels.Channel.CreateUnbounded<byte[]>();
        private byte[] current = Array.Empty<byte>(); private int offset;
        internal void Feed(string line) => chunks.Writer.TryWrite(System.Text.Encoding.UTF8.GetBytes(line + "\n"));
        public override async System.Threading.Tasks.Task<int> ReadAsync(byte[] buffer, int index, int count, System.Threading.CancellationToken token)
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
    private sealed class SessionReplyStream : MemoryStream
    {
        internal Action<string>? OnWrite;
        public override System.Threading.Tasks.Task WriteAsync(byte[] buffer, int offset, int count, System.Threading.CancellationToken token)
        { OnWrite?.Invoke(System.Text.Encoding.UTF8.GetString(buffer, offset, count)); return System.Threading.Tasks.Task.CompletedTask; }
    }
}
