using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using TiaMcp.WorkerProtocol;
using TiaMcp.WorkerProtocol.AsyncPreview;
using TiaMcp.WorkerProtocol.HostTransport;
using TiaMcp.WorkerProtocol.JsonV2;

int passed = 0;
void Check(bool condition, string name) { if (!condition) throw new Exception(name); passed++; }
async Task Reject(Func<Task> action, string name)
{
    try { await action().WaitAsync(TimeSpan.FromSeconds(8)); }
    catch (Exception error) when (error is IdentityViolation or OperationCanceledException or IOException) { passed++; return; }
    throw new Exception("Accepted: " + name);
}
string dotnet = Environment.ProcessPath!;
if (!Path.GetFileNameWithoutExtension(dotnet).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
    dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? throw new Exception("Run tests with dotnet or set DOTNET_HOST_PATH");
string fixture = args.Single();
var engine = new EngineIdentity(2, "20", new string('a', 64), new string('b', 64), new string('c', 32));
AsyncJsonSession Session(OuterIdStyle style = OuterIdStyle.Numeric) => new(engine, new[] {
    new OperationPolicy("List", false, readOnly: true),
    new OperationPolicy("Unknown", false, readOnly: true),
    // Deliberately looser host policy proves the worker enforces its own policy.
    new OperationPolicy("BoundList", false, readOnly: true)
}, style);
using var all = JsonDocument.Parse("{}");
using var filtered = JsonDocument.Parse("{\"minimum\":15,\"prefix\":\"Al\"}");
using var empty = JsonDocument.Parse("{\"minimum\":99}");
var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
SampleResult Typed(JsonElement result)
{
    Check(result.ValueKind == JsonValueKind.Object, "typed result object");
    var typed = result.Deserialize<SampleResult>(options) ?? throw new Exception("null typed result");
    Check(typed.Source == "endpoint-fixture", "callback's independent result source");
    Check(typed.Items != null && typed.Count == typed.Items.Length, "typed count matches data");
    Check(result.GetProperty("optional").ValueKind == JsonValueKind.Null, "legacy-to-modern null preserved");
    return typed;
}
string root = Path.Combine(Path.GetTempPath(), "endpoint-fixture-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
int launches = 0;
string Marker() => Path.Combine(root, (++launches).ToString());
Task<OwnedPipeTransport> Start(AsyncJsonSession session, string mode, string marker, int ms = 3000) =>
    OwnedPipeTransport.StartAsync(dotnet, new[] { fixture, mode, marker }, session, TimeSpan.FromMilliseconds(ms));
int Dispatches(string marker) => File.Exists(marker + ".dispatch") ? File.ReadAllLines(marker + ".dispatch").Length : 0;
bool Alive(int pid)
{
    try { using var process = Process.GetProcessById(pid); return !process.HasExited; }
    catch (ArgumentException) { return false; }
}
try
{
    foreach (OuterIdStyle style in new[] { OuterIdStyle.Numeric, OuterIdStyle.ModernString })
    foreach (string mode in new[] { "healthy", "progress" })
    {
        string marker = Marker(); var session = Session(style); await using var pipe = await Start(session, mode, marker);
        Check(pipe.IdentityVerified, "endpoint fixture ownership handshake verified");
        var inputs = new[] { filtered.RootElement, all.RootElement, empty.RootElement };
        for (int i = 0; i < inputs.Length; i++)
        {
            var exchange = new RecordingExchange(pipe.CreateExchange()); SampleResult? sample = null;
            var reply = await session.CallAsync("List", inputs[i], exchange, result => sample = Typed(result), TimeSpan.FromSeconds(2));
            Check(reply.Outcome == ReplyOutcome.Succeeded && sample != null, "real callback result validated");
            Check(sample!.Invocation == i + 1, "fixture ran callback once per exchange");
            if (i == 0) Check(sample.Count == 1 && sample.Items[0] == new SampleItem("Alpine", 30, true), "filter independently computed");
            if (i == 1) Check(sample.Items.SequenceEqual(new[] { new SampleItem("Alpha", 10, true), new SampleItem("Alpine", 30, true), new SampleItem("Beta", 20, false) }), "typed string/number/bool list sorted independently");
            if (i == 2) Check(sample.Count == 0 && sample.Items.Length == 0, "empty typed list preserved");
            var final = (ReplyFrame)exchange.Frames.Last();
            Check(final.Identity.ObservedAfter.Matches(BindingSnapshot.Unbound(0)), "reply uses stable independently observed binding");
            Check(style == OuterIdStyle.Numeric ? final.Id.Number == i + 1 : final.Id.Text == "worker_" + (i + 1), "modern outer ID preserved by legacy endpoint");
            Check(exchange.Frames.Count == (mode == "progress" ? 4 : 1), "explicit progress/reply exchange boundary");
            if (mode == "progress")
            {
                var progress = exchange.Frames.OfType<ProgressFrame>().ToArray();
                Check(progress.Select(p => p.Percent).SequenceEqual(new[] { 0, 40, 100 }), "real callback progress percentages");
                Check(progress.Select(p => p.Sequence).SequenceEqual(new long[] { 1, 2, 3 }), "progress sequence restarts per request");
                Check(progress.All(p => p.Id == final.Id && p.Identity.Matches(final.Identity.Request)), "progress identity correlated to result");
            }
            Check(!session.Faulted && !session.OutcomeUnknown && !pipe.HasExited, "successful session remains reusable");
        }
        Check(Dispatches(marker) == 3, "three exchanges call real endpoint callback three times");
        await pipe.DisposeAsync(); await pipe.DisposeAsync();
        Check(pipe.HasExited && pipe.ShutdownConfirmed, "owned endpoint shutdown confirmed and idempotent");
    }

    foreach (string mode in new[] { "error-once", "progress-error" })
    {
        string marker = Marker(); var session = Session(); await using var pipe = await Start(session, mode, marker);
        var exchange = new RecordingExchange(pipe.CreateExchange()); bool validated = false;
        var reply = await session.CallAsync("List", all.RootElement, exchange, result => {
            Check(result.GetRawText() == "{\"code\":\"ReadOperationFailed\"}", "read failure returns fixed public code only");
            validated = true;
        }, TimeSpan.FromSeconds(2));
        Check(validated && reply.Outcome == ReplyOutcome.ReadFailed, "stable callback exception is typed ReadFailed");
        Check(!reply.Result.GetRawText().Contains("SECRET", StringComparison.Ordinal), "callback exception details sanitized");
        Check(!session.Faulted && !session.OutcomeUnknown, "stable read failure leaves host usable");
        Check(exchange.Frames.Count == (mode == "progress-error" ? 4 : 1), "read failure follows actual progress");
        if (mode == "error-once")
        {
            SampleResult? next = null;
            var recovered = await session.CallAsync("List", filtered.RootElement, pipe.CreateExchange(), r => next = Typed(r), TimeSpan.FromSeconds(2));
            Check(recovered.Outcome == ReplyOutcome.Succeeded && next!.Invocation == 2, "explicit next read succeeds after prior ReadFailed");
            Check(Dispatches(marker) == 2, "failed read not automatically replayed");
        }
    }

    foreach (string mode in new[] { "binding-before", "epoch-before", "observe-before-throws", "binding-after", "epoch-after", "observe-after-throws", "read-error-binding-after", "invalid-progress", "unknown-operation", "requires-binding" })
    {
        string marker = Marker(); var session = Session(); await using var pipe = await Start(session, mode, marker);
        bool validated = false; var exchange = new RecordingExchange(pipe.CreateExchange());
        string operation = mode == "unknown-operation" ? "Unknown" : mode == "requires-binding" ? "BoundList" : "List";
        await Reject(() => session.CallAsync(operation, all.RootElement, exchange, _ => validated = true, TimeSpan.FromSeconds(2)), mode);
        Check(!validated && exchange.Frames.All(f => f is not ReplyFrame), mode + " no validated or emitted reply");
        Check(session.Faulted && session.OutcomeUnknown, mode + " host terminal/no replay");
        await pipe.DisposeAsync();
        int expectedCalls = mode is "binding-before" or "epoch-before" or "observe-before-throws" or "unknown-operation" or "requires-binding" ? 0 : 1;
        Check(Dispatches(marker) == expectedCalls, mode + " callback execution boundary");
        Check(File.Exists(marker + ".fault") && File.ReadAllText(marker + ".fault") == "faulted", mode + " endpoint fail-closed");
        Check(pipe.HasExited && pipe.ShutdownConfirmed, mode + " only owned endpoint reaped");
        await Reject(() => session.CallAsync("List", all.RootElement, pipe.CreateExchange(), _ => { }, TimeSpan.FromSeconds(1)), mode + " follow-up forbidden");
    }

    foreach (string mode in new[] { "wrong-token", "wrong-pid", "bound-hello", "epoch-hello", "observe-hello-throws" })
    {
        string marker = Marker();
        await Reject(async () => { await using var pipe = await Start(Session(), mode, marker); }, mode + " startup rejected");
        Check(File.Exists(marker), mode + " fixture actually launched");
        Check(!Alive(int.Parse(File.ReadAllText(marker))), mode + " rejected owned child reaped");
        Check(Dispatches(marker) == 0, mode + " callback never reached");
    }

    foreach (string mode in new[] { "hang", "exit", "truncated", "no-boundary", "progress-flood" })
    {
        string marker = Marker(); var session = Session(); await using var pipe = await Start(session, mode, marker);
        var watch = Stopwatch.StartNew(); bool validated = false;
        await Reject(() => session.CallAsync("List", all.RootElement, pipe.CreateExchange(), _ => validated = true, TimeSpan.FromMilliseconds(300)), mode);
        Check(watch.Elapsed < TimeSpan.FromSeconds(3), mode + " end-to-end deadline bounded");
        Check(!validated && session.Faulted && session.OutcomeUnknown, mode + " read fault prevents commit");
        await pipe.DisposeAsync();
        Check(Dispatches(marker) == 1, mode + " actual callback entered once");
        Check(pipe.HasExited && pipe.ShutdownConfirmed, mode + " owned process cleanup");
    }

    {
        string marker = Marker(); var session = Session(); await using var pipe = await Start(session, "hang", marker);
        using var cancel = new CancellationTokenSource(100);
        await Reject(() => session.CallAsync("List", all.RootElement, pipe.CreateExchange(), _ => { }, TimeSpan.FromSeconds(3), cancel.Token), "callback hang canceled");
        Check(session.Faulted && session.OutcomeUnknown, "cancellation after dispatch is terminal");
        await pipe.DisposeAsync(); Check(pipe.HasExited && pipe.ShutdownConfirmed, "canceled owned endpoint reaped");
    }

    {
        string marker = Marker(); var session = Session(); await using var pipe = await Start(session, "healthy", marker);
        await Reject(() => session.CallAsync("List", all.RootElement, pipe.CreateExchange(), _ => throw new IdentityViolation("TypedResultRejected"), TimeSpan.FromSeconds(2)), "typed result rejection");
        Check(session.Faulted && session.OutcomeUnknown, "result validation failure prevents commit and replay");
        await pipe.DisposeAsync(); Check(Dispatches(marker) == 1 && pipe.ShutdownConfirmed, "typed rejection cleanup and no replay");
    }

    {
        string first = Marker(), second = Marker(); var a = Session(); var b = Session();
        await using var one = await Start(a, "healthy", first); await using var two = await Start(b, "healthy", second);
        Check(one.OwnedProcessId != two.OwnedProcessId, "independent endpoint process identities");
        await one.DisposeAsync();
        Check(one.HasExited && !two.HasExited, "disposal leaves other endpoint alive");
        var reply = await b.CallAsync("List", all.RootElement, two.CreateExchange(), _ => { }, TimeSpan.FromSeconds(2));
        Check(reply.Outcome == ReplyOutcome.Succeeded && !b.Faulted, "other owned endpoint still usable");
    }
    int integrationPassed = passed;
    Console.WriteLine($"PASS {integrationPassed} real-process/pipe endpoint integration assertions (test fixtures only)");
    passed += EndpointUnitCases.Run();
    Console.WriteLine($"PASS {passed} worker endpoint assertions ({integrationPassed} real-process/pipe integration assertions; test fixtures only)");
}
finally { Directory.Delete(root, recursive: true); }

sealed record SampleItem(string Name, int Code, bool Active);
sealed record SampleResult(string Source, int Invocation, int Count, SampleItem[] Items, string? Optional);
sealed class RecordingExchange(IAsyncV2Exchange inner) : IAsyncV2Exchange
{
    public List<V2Frame> Frames { get; } = new();
    public ValueTask DispatchAsync(ReadOnlyMemory<byte> request, CancellationToken cancellationToken) => inner.DispatchAsync(request, cancellationToken);
    public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadFramesAsync(FrameLimits limits, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var bytes in inner.ReadFramesAsync(limits, cancellationToken))
        {
            Frames.Add(StrictCodec.Decode(bytes));
            yield return bytes;
        }
    }
    public void Abort() => inner.Abort();
    public ValueTask DisposeAsync() => inner.DisposeAsync();
}
