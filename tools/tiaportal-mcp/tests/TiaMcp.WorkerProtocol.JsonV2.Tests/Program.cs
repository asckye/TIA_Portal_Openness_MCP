using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.WorkerProtocol;
using TiaMcp.WorkerProtocol.JsonV2;

int count = 0;
void Check(bool b, string label) { if (!b) throw new Exception(label); count++; }
void Reject(Action a, string label) { try { a(); } catch (IdentityViolation e) { Check(!e.ToString().Contains(Fake.Secret), "exception sanitized: " + label); return; } throw new Exception("Accepted: " + label); }
var engine = new EngineIdentity(2, "20", new string('a', 64), new string('b', 64), new string('c', 32));
var project = new ProjectIdentity(new string('d', 64), 123, 638000000000000000);
var policies = new[] { new OperationPolicy("Bind", false, BindingEffect.Bind), new OperationPolicy("Read", true, readOnly: true), new OperationPolicy("Write", true), new OperationPolicy("Close", true, BindingEffect.Unbind), new OperationPolicy("List", false, readOnly: true) };
JsonElement Json(string s) => JsonDocument.Parse(s).RootElement.Clone();
byte[] Mutate(byte[] bytes, Action<JsonObject> edit) { var n = JsonNode.Parse(bytes)!.AsObject(); edit(n); return Encoding.UTF8.GetBytes(n.ToJsonString()); }
var request = new RequestIdentity(1, new string('e', 32), engine, "Bind", BindingSnapshot.Unbound(0), BindingSnapshot.Bound(1, project));
var req = StrictCodec.Encode(new RequestFrame(new WireId("worker_1"), request, Json("{}")));
var rep = StrictCodec.Encode(new ReplyFrame(new WireId("worker_1"), new ReplyIdentity(request, request.ExpectedAfter, ReplyOutcome.Succeeded), Json("{\"ok\":true}")));
var hello = StrictCodec.Encode(new HelloFrame(engine, BindingSnapshot.Unbound(0)));
var progress = StrictCodec.Encode(new ProgressFrame(new WireId("worker_1"), request, 1, 30));
foreach (var frame in new[] { req, rep, hello, progress })
{
    Check(StrictCodec.Encode(StrictCodec.Decode(frame)).SequenceEqual(frame), "roundtrip frame");
    Reject(() => StrictCodec.Decode(Mutate(frame, n => n["version"] = 1)), "v1 rejected");
    Reject(() => StrictCodec.Decode(Mutate(frame, n => n["extra"] = "x")), "unknown envelope field");
    foreach (string property in JsonNode.Parse(frame)!.AsObject().Select(p => p.Key).ToArray())
        Reject(() => StrictCodec.Decode(Mutate(frame, n => n.Remove(property))), "missing envelope " + property);
    string text = Encoding.UTF8.GetString(frame);
    Reject(() => StrictCodec.Decode(Encoding.UTF8.GetBytes(text.Insert(1, "\"version\":2,"))), "duplicate envelope");
    Reject(() => StrictCodec.Decode(Encoding.UTF8.GetBytes(text.Insert(1, "\"Version\":2,"))), "case ambiguous envelope");
}
foreach (string field in new[] { "requestId", "correlationId", "engine", "operation", "before", "expectedAfter" })
    Reject(() => StrictCodec.Decode(Mutate(req, n => n["identity"]!.AsObject().Remove(field))), "missing identity " + field);
foreach (string field in new[] { "protocol", "releaseKey", "workerSha256", "engineSha256", "sessionId" })
    Reject(() => StrictCodec.Decode(Mutate(req, n => n["identity"]!["engine"]!.AsObject().Remove(field))), "missing engine " + field);
foreach (string field in new[] { "epoch", "state", "project" })
    Reject(() => StrictCodec.Decode(Mutate(req, n => n["identity"]!["before"]!.AsObject().Remove(field))), "missing binding " + field);
foreach (string field in new[] { "projectSha256", "tiaProcessId", "tiaProcessStartUtcTicks" })
    Reject(() => StrictCodec.Decode(Mutate(req, n => n["identity"]!["expectedAfter"]!["project"]!.AsObject().Remove(field))), "missing project " + field);
foreach (string path in new[] { "identity", "identity.engine", "identity.before", "identity.expectedAfter", "identity.expectedAfter.project" })
    Reject(() => StrictCodec.Decode(Mutate(req, n => { JsonNode x = n; foreach (string key in path.Split('.')) x = x[key]!; x["unknown"] = 1; })), "nested unknown " + path);
foreach (string bad in new[] { "null", "[]", "{}", "{", "{\"version\":2,}", "/*x*/{}", "{}{}" }) Reject(() => StrictCodec.Decode(Encoding.UTF8.GetBytes(bad)), "invalid JSON/root");
foreach (string number in new[] { "1.0", "1e0", "9223372036854775808", "-1", "0", "\"1\"", "null", "true" })
    Reject(() => StrictCodec.Decode(Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(req).Replace("\"requestId\":1", "\"requestId\":" + number))), "bad request ID " + number);
foreach (string payload in new[] { "{\"x\":1,\"x\":2}", "{\"x\":{\"Y\":1,\"y\":2}}", "{\"x\":[{\"a\":1,\"\\u0061\":2}]}" })
    Reject(() => StrictCodec.Decode(Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(req).Replace("\"arguments\":{}", "\"arguments\":" + payload))), "duplicate opaque payload");
Reject(() => StrictCodec.Decode(new byte[StrictCodec.MaxFrameBytes + 1]), "frame cap");
Reject(() => StrictCodec.Decode(Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(req).Replace("\"arguments\":{}", "\"arguments\":" + new string('[', 40) + "0" + new string(']', 40)))), "depth cap");
Reject(() => StrictCodec.Decode(Mutate(req, n => n["identity"]!["engine"]!["protocol"] = 1)), "nested version");
Reject(() => StrictCodec.Decode(Mutate(req, n => n["identity"]!["engine"]!["releaseKey"] = "14")), "exact release");
Reject(() => StrictCodec.Decode(Mutate(req, n => n["identity"]!["before"]!["project"] = JsonNode.Parse("{}"))), "unbound cannot have project");
foreach (string release in new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" })
{
    var exact = new EngineIdentity(2, release, engine.WorkerSha256, engine.EngineSha256, engine.SessionId);
    var h = (HelloFrame)StrictCodec.Decode(StrictCodec.Encode(new HelloFrame(exact, BindingSnapshot.Unbound(0))));
    Check(h.Engine.Matches(exact), "exact-release wire roundtrip " + release);
}
var invalidUtf8 = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(req).Replace("\"arguments\":{}", "\"arguments\":{\"value\":\"Z\"}"));
invalidUtf8[Array.IndexOf(invalidUtf8, (byte)'Z')] = 0xff;
Reject(() => StrictCodec.Decode(invalidUtf8), "invalid UTF-8");
foreach (string path in new[] { "identity", "identity.engine", "identity.before", "identity.expectedAfter", "identity.expectedAfter.project" })
    Reject(() => StrictCodec.Decode(Mutate(req, n => { JsonNode x = n; foreach (string key in path.Split('.')) x = x[key]!; var first = x.AsObject().First(); x[first.Key.ToUpperInvariant()] = first.Value!.DeepClone(); })), "nested case ambiguity " + path);
var replayWorker = new Fake(engine, project, policies);
_ = replayWorker.Exchange(req).ToArray();
Reject(() => replayWorker.Exchange(req).ToArray(), "duplicate request rejected before operation");
Check(replayWorker.Operations == 1, "worker duplicate is never executed");
foreach (string token in new[] { "\\uD800", "\\uDC00", "\\uD800x", "\\uDC00\\uD800" })
{
    string bad = Encoding.UTF8.GetString(req).Replace("\"arguments\":{}", "\"arguments\":{\"value\":\"" + token + "\"}");
    Reject(() => StrictCodec.Decode(Encoding.UTF8.GetBytes(bad)), "unpaired surrogate argument");
    bad = Encoding.UTF8.GetString(rep).Replace("true", "\"" + token + "\"");
    Reject(() => StrictCodec.Decode(Encoding.UTF8.GetBytes(bad)), "unpaired surrogate result");
    bad = Encoding.UTF8.GetString(req).Replace("\"arguments\":{}", "\"arguments\":{\"" + token + "\":0}");
    Reject(() => StrictCodec.Decode(Encoding.UTF8.GetBytes(bad)), "unpaired surrogate property");
    bad = Encoding.UTF8.GetString(req).Replace(engine.SessionId, token);
    Reject(() => StrictCodec.Decode(Encoding.UTF8.GetBytes(bad)), "unpaired surrogate identity");
}
var paired = StrictCodec.Decode(Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(req).Replace("\"arguments\":{}", "\"arguments\":{\"value\":\"\\uD83D\\uDE00\"}")));
Check(((RequestFrame)paired).Arguments.GetProperty("value").GetString() == "😀", "paired surrogate accepted");
foreach (int depth in new[] { 32, 33 })
{
    // Root object + arguments object consume two levels.
    byte[] nested = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(req).Replace("\"arguments\":{}", "\"arguments\":{\"x\":" + new string('[', depth - 2) + "0" + new string(']', depth - 2) + "}"));
    if (depth == 32) Check(StrictCodec.Decode(nested) is RequestFrame, "exact depth accepted");
    else Reject(() => StrictCodec.Decode(nested), "depth plus one rejected");
}
byte[] cap = new byte[StrictCodec.MaxFrameBytes]; req.CopyTo(cap, 0); Array.Fill(cap, (byte)' ', req.Length, cap.Length - req.Length);
Check(StrictCodec.Decode(cap) is RequestFrame, "exact frame cap accepted");
foreach (string field in new[] { "sequence", "percent" })
foreach (long value in new[] { long.MinValue, -1, 0, 100, 101, long.MaxValue })
{
    byte[] changed = Mutate(progress, n => n[field] = value);
    bool valid = field == "sequence" ? value > 0 : value is >= 0 and <= 100;
    if (valid) Check(StrictCodec.Decode(changed) is ProgressFrame, "progress limit accepted");
    else Reject(() => StrictCodec.Decode(changed), "progress limit rejected");
}
foreach (JsonNode? badId in new JsonNode?[] { null, JsonValue.Create(0), JsonValue.Create(-1), JsonValue.Create(1.5), JsonValue.Create(""), JsonValue.Create("bad id"), JsonValue.Create(true), new JsonObject() })
    Reject(() => StrictCodec.Decode(Mutate(req, n => n["id"] = badId?.DeepClone())), "outer ID admission");
Check(((RequestFrame)StrictCodec.Decode(Mutate(req, n => n["id"] = 1))).Id == new WireId(1), "numeric outer ID retained");
Check(((RequestFrame)StrictCodec.Decode(Mutate(req, n => n["id"] = "1"))).Id == new WireId("1"), "string outer ID retained");
Check(new WireId(1) != new WireId("1"), "outer ID types distinct");
foreach (var style in Enum.GetValues<OuterIdStyle>())
{
    var session = new JsonSession(engine, policies, style); session.AcceptHello(hello);
    var wire = new Fake(engine, project, policies);
    session.Call("Bind", Json("{\"password\":\"" + Fake.Secret + "\"}"), wire, _ => { }, project);
    session.Call("Read", Json("{}"), wire, r => Check(r.GetProperty("ok").GetBoolean(), "validated result"));
    Check(!session.Faulted && wire.Calls == 2 && wire.Operations == 2, "real JSON exchange success");
    Check(wire.Logs.All(l => !JsonSerializer.Serialize(l).Contains(Fake.Secret)), "logs omit credentials");
    Check(wire.Logs.All(l => l.Keys.All(k => !new[] { "arguments", "projectPath", "exceptionMessage", "result" }.Contains(k))), "log allowlist");
    Check(wire.Logs.Select(l => l["correlationId"]).Distinct().Count() == 2, "correlation retained per exchange");
    wire.Outcome = ReplyOutcome.ReadFailed; Check(session.Call("Read", Json("{}"), wire, _ => { }).Outcome == ReplyOutcome.ReadFailed, "read failure outcome preserved");
    wire.Outcome = ReplyOutcome.RejectedBeforeOperation; Check(session.Call("Write", Json("{}"), wire, _ => { }).Outcome == ReplyOutcome.RejectedBeforeOperation, "rejection outcome preserved");
    Check(!session.Faulted, "known failures usable");
    wire.Outcome = ReplyOutcome.Succeeded; session.Call("Close", Json("{}"), wire, _ => { });
    Check(wire.Binding.Epoch == 2 && !wire.Binding.IsBound, "close epoch");
}
foreach (string fault in new[] { "wrong-id", "id-type", "request-id", "correlation", "release", "worker", "engine", "session", "epoch", "project", "pid", "start", "missing", "duplicate", "unknown", "version", "progress-id", "progress-identity", "progress-repeat", "no-reply", "trailing", "timeout", "cancel", "pipe", "malformed", "worker-binding", "unknown-outcome", "write-read-failed", "after-reply-move", "after-reply-dispose" })
{
    var session = new JsonSession(engine, policies, OuterIdStyle.ModernString); session.AcceptHello(hello);
    var wire = new Fake(engine, project, policies); session.Call("Bind", Json("{}"), wire, _ => { }, project);
    wire.Fault = fault;
    int validated = 0;
    Reject(() => session.Call("Write", Json("{}"), wire, _ => { validated++; }), fault);
    Check(validated == 0, "invalid completion never reaches validator " + fault);
    Check(session.Faulted && session.OutcomeUnknown, "unknown sent state " + fault);
    int calls = wire.Calls; Reject(() => session.Call("Write", Json("{}"), wire, _ => { }), "no replay " + fault);
    Check(calls == wire.Calls, "no extra transport call " + fault);
    if (fault == "worker-binding") Check(wire.Operations == 1, "mismatch blocked before worker operation");
}
var invalidHello = new JsonSession(engine, policies, OuterIdStyle.Numeric);
Reject(() => invalidHello.AcceptHello(Mutate(hello, n => n["engine"]!["releaseKey"] = "21")), "wrong hello");
Check(invalidHello.Faulted && !invalidHello.OutcomeUnknown, "hello has no sent operation");
var invalidArgs = new JsonSession(engine, policies, OuterIdStyle.Numeric); invalidArgs.AcceptHello(hello); var untouched = new Fake(engine, project, policies);
Reject(() => invalidArgs.Call("List", Json("[]"), untouched, _ => { }), "encode validation before send");
Check(!invalidArgs.Faulted && untouched.Calls == 0, "invalid outgoing frame not sent");
invalidArgs.Call("List", Json("{}"), untouched, _ => { });
Reject(() => invalidArgs.Call("List", Json("{}"), untouched, _ => throw new Exception(Fake.Secret)), "result validation failure");
Check(invalidArgs.Faulted && invalidArgs.OutcomeUnknown, "result rejected poisons");
var disposing = new JsonSession(engine, policies, OuterIdStyle.Numeric); disposing.AcceptHello(hello);
var disposalTransport = new Fake(engine, project, policies); int disposalValidated = 0;
Reject(() => disposing.Call("List", Json("{}"), new DisposeFailureExchange(disposalTransport), _ => disposalValidated++), "IEnumerator.Dispose failure after valid reply");
Check(disposing.Faulted && disposing.OutcomeUnknown && disposalValidated == 0, "IEnumerator.Dispose poisons before validator");
Reject(() => disposing.Call("List", Json("{}"), disposalTransport, _ => disposalValidated++), "IEnumerator.Dispose no replay");
Check(disposalTransport.Calls == 1, "IEnumerator.Dispose single exchange");
foreach (string phase in new[] { "exchange", "enumerator" })
{
    var acquiring = new JsonSession(engine, policies, OuterIdStyle.Numeric); acquiring.AcceptHello(hello);
    var failing = new AcquisitionFailure(phase); int validated = 0;
    Reject(() => acquiring.Call("List", Json("{}"), failing, _ => validated++), "acquisition failure " + phase);
    Check(acquiring.Faulted && acquiring.OutcomeUnknown && validated == 0, "acquisition poisons before callback");
    Reject(() => acquiring.Call("List", Json("{}"), failing, _ => validated++), "acquisition no replay");
    Check(failing.Calls == 1, "single exchange acquisition attempt");
}
foreach (string field in new[] { "sequence", "percent" })
foreach (string number in new[] { "1.0", "1e0", "\"1\"", "null", "true" })
{
    string original = Encoding.UTF8.GetString(progress);
    string needle = field == "sequence" ? "\"sequence\":1" : "\"percent\":30";
    Reject(() => StrictCodec.Decode(Encoding.UTF8.GetBytes(original.Replace(needle, "\"" + field + "\":" + number))), "progress integer token");
}
Console.WriteLine($"PASS {count}: strict JSON v2 codec and in-memory IPC checks; no native/process execution.");

sealed class Fake : IV2Exchange
{
    public const string Secret = "DO_NOT_LOG_credential_path_exception";
    readonly EngineIdentity engine; readonly ProjectIdentity project; readonly WorkerRequestGuard guard;
    public BindingSnapshot Binding = BindingSnapshot.Unbound(0);
    public string Fault = ""; public ReplyOutcome Outcome = ReplyOutcome.Succeeded;
    public int Calls, Operations;
    public List<IReadOnlyDictionary<string, string>> Logs = new();
    public Fake(EngineIdentity engine, ProjectIdentity project, OperationPolicy[] policies) { this.engine = engine; this.project = project; guard = new WorkerRequestGuard(engine, policies); }
    public IEnumerable<ReadOnlyMemory<byte>> Exchange(ReadOnlyMemory<byte> bytes)
    {
        using var disposal = new Disposal(() => { if (Fault == "after-reply-dispose") throw new IOException(Secret); });
        Calls++;
        var r = (RequestFrame)StrictCodec.Decode(bytes);
        guard.Accept(r.Identity, Fault == "worker-binding" ? BindingSnapshot.Unbound(Binding.Epoch + 1) : Binding);
        using var scope = RequestLogContext.Open(r.Identity);
        Logs.Add(scope.SnapshotFields());
        if (Outcome != ReplyOutcome.RejectedBeforeOperation) Operations++;
        if (Fault == "timeout") throw new TimeoutException(Secret);
        if (Fault == "cancel") throw new OperationCanceledException(Secret);
        if (Fault == "pipe") throw new IOException(Secret);
        // Fake native observation is independent of request.ExpectedAfter.
        if (Outcome == ReplyOutcome.Succeeded && r.Identity.Operation == "Bind") Binding = BindingSnapshot.Bound(Binding.Epoch + 1, project);
        if (Outcome == ReplyOutcome.Succeeded && r.Identity.Operation == "Close") Binding = BindingSnapshot.Unbound(Binding.Epoch + 1);
        var p = new ProgressFrame(r.Id, r.Identity, 1, 50);
        var progress = StrictCodec.Encode(p);
        if (Fault == "progress-id") progress = Change(progress, n => n["id"] = "worker_999");
        if (Fault == "progress-identity") progress = Change(progress, n => n["identity"]!["correlationId"] = new string('f', 32));
        yield return progress;
        if (Fault == "progress-repeat") yield return progress;
        if (Fault == "no-reply") yield break;
        scope.ObserveCompletion(Binding, Outcome); Logs.Add(scope.SnapshotFields());
        var reply = StrictCodec.Encode(new ReplyFrame(r.Id, guard.Finish(Binding, Outcome), JsonDocument.Parse("{\"ok\":true}").RootElement.Clone()));
        reply = Change(reply, n =>
        {
            var i = n["identity"]!; var e = i["engine"]!; var b = n["observedAfter"]!;
            switch (Fault)
            {
                case "wrong-id": n["id"] = "worker_999"; break;
                case "id-type": n["id"] = r.Identity.RequestId; break;
                case "request-id": i["requestId"] = 999; break;
                case "correlation": i["correlationId"] = new string('f', 32); break;
                case "release": e["releaseKey"] = "21"; break;
                case "worker": e["workerSha256"] = new string('f', 64); break;
                case "engine": e["engineSha256"] = new string('f', 64); break;
                case "session": e["sessionId"] = new string('f', 32); break;
                case "epoch": b["epoch"] = 999; break;
                case "project": b["project"]!["projectSha256"] = new string('f', 64); break;
                case "pid": b["project"]!["tiaProcessId"] = 999; break;
                case "start": b["project"]!["tiaProcessStartUtcTicks"] = 1; break;
                case "missing": n.Remove("identity"); break;
                case "unknown": n["unexpected"] = Secret; break;
                case "version": n["version"] = 1; break;
                case "unknown-outcome": n["outcome"] = "unknown"; break;
                case "write-read-failed": n["outcome"] = "readFailed"; break;
            }
        });
        if (Fault == "duplicate") reply = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(reply).Insert(1, "\"version\":2,"));
        if (Fault == "malformed") reply = Encoding.UTF8.GetBytes("{" + Secret);
        yield return reply;
        if (Fault == "after-reply-move") throw new IOException(Secret);
        if (Fault == "trailing") yield return reply;
    }
    static byte[] Change(byte[] bytes, Action<JsonObject> edit) { var n = JsonNode.Parse(bytes)!.AsObject(); edit(n); return Encoding.UTF8.GetBytes(n.ToJsonString()); }
}

sealed class Disposal(Action action) : IDisposable { public void Dispose() => action(); }

sealed class AcquisitionFailure(string phase) : IV2Exchange, IEnumerable<ReadOnlyMemory<byte>>
{
    public int Calls;
    public IEnumerable<ReadOnlyMemory<byte>> Exchange(ReadOnlyMemory<byte> request)
    {
        Calls++;
        if (phase == "exchange") throw new IOException(Fake.Secret);
        return this;
    }
    public IEnumerator<ReadOnlyMemory<byte>> GetEnumerator() => throw new IOException(Fake.Secret);
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}

sealed class DisposeFailureExchange(IV2Exchange inner) : IV2Exchange
{
    public IEnumerable<ReadOnlyMemory<byte>> Exchange(ReadOnlyMemory<byte> request) => new Frames(inner.Exchange(request));
    sealed class Frames(IEnumerable<ReadOnlyMemory<byte>> frames) : IEnumerable<ReadOnlyMemory<byte>>
    {
        public IEnumerator<ReadOnlyMemory<byte>> GetEnumerator() => new Enumerator(frames.GetEnumerator());
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
    sealed class Enumerator(IEnumerator<ReadOnlyMemory<byte>> inner) : IEnumerator<ReadOnlyMemory<byte>>
    {
        public ReadOnlyMemory<byte> Current => inner.Current;
        object System.Collections.IEnumerator.Current => Current;
        public bool MoveNext() => inner.MoveNext();
        public void Reset() => throw new NotSupportedException();
        public void Dispose() { inner.Dispose(); throw new IOException(Fake.Secret); }
    }
}
