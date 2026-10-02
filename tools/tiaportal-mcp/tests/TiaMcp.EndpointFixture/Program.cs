// Test-only, independently stateful read endpoint. No Siemens assemblies, native calls,
// production launcher integration, or SDK substitutes are used by this executable.
using System.Buffers.Binary;
using System.Text.Json;
using TiaMcp.WorkerProtocol;
using TiaMcp.WorkerProtocol.Endpoint;
using TiaMcp.WorkerProtocol.JsonLegacy;

string mode = args[0];
string? marker = args.Length > 1 ? args[1] : null;
if (marker != null) File.WriteAllText(marker, Environment.ProcessId.ToString());
using Stream input = Console.OpenStandardInput(), output = Console.OpenStandardOutput();
void Header(int length)
{
    byte[] header = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(header, length);
    output.Write(header); output.Flush();
}
void Frame(byte[] payload) { Header(payload.Length); output.Write(payload); output.Flush(); }
byte[] owner = new byte[36];
Convert.FromHexString(Environment.GetEnvironmentVariable("TIA_TRANSPORT_OWNER_TOKEN")!).CopyTo(owner, 0);
Environment.SetEnvironmentVariable("TIA_TRANSPORT_OWNER_TOKEN", null);
BinaryPrimitives.WriteInt32LittleEndian(owner.AsSpan(32), Environment.ProcessId);
if (mode == "wrong-token") owner[0] ^= 1;
if (mode == "wrong-pid") BinaryPrimitives.WriteInt32LittleEndian(owner.AsSpan(32), 1);
output.Write(owner); output.Flush();

var engine = new EngineIdentity(2, "20", new string('a', 64), new string('b', 64), new string('c', 32));
// This state is owned by the fixture, never copied from any request identity.
BindingSnapshot binding = BindingSnapshot.Unbound(0);
if (mode == "bound-hello") binding = ChangedBinding();
if (mode == "epoch-hello") binding = BindingSnapshot.Unbound(1);
int observations = 0, invocations = 0;
var samples = new[] { new Sample("Beta", 20, false), new Sample("Alpha", 10, true), new Sample("Alpine", 30, true) };
BindingSnapshot Observe()
{
    int current = ++observations;
    if (mode == "observe-hello-throws" || mode == "observe-before-throws" && current == 2 ||
        mode == "observe-after-throws" && current == 3)
        throw new InvalidOperationException("SECRET observer details must not cross the wire");
    return binding;
}
JsonPayload List(JsonPayload arguments, Action<int> progress)
{
    invocations++;
    if (marker != null) File.AppendAllText(marker + ".dispatch", "List\n");
    if (mode == "hang") Thread.Sleep(Timeout.Infinite);
    if (mode == "exit") Environment.Exit(0);
    if (mode == "progress" || mode == "progress-error") { progress(0); progress(40); progress(100); }
    if (mode == "invalid-progress") progress(101);
    if (mode == "progress-flood") for (int i = 0; i < 40; i++) progress(i);
    if (mode == "binding-after" || mode == "read-error-binding-after") binding = ChangedBinding();
    if (mode == "epoch-after") binding = BindingSnapshot.Unbound(1);
    if (mode == "read-error-binding-after" || mode == "progress-error" || mode == "error-once" && invocations == 1)
        throw new InvalidOperationException("SECRET dispatch details must not cross the wire");
    long minimum = arguments.TryGetProperty("minimum", out var min) ? min.GetInt64() : 0;
    string prefix = arguments.TryGetProperty("prefix", out var p) ? p.GetString()! : "";
    Sample[] selected = samples.Where(s => s.Code >= minimum && s.Name.StartsWith(prefix, StringComparison.Ordinal))
        .OrderBy(s => s.Name, StringComparer.Ordinal).ToArray();
    return JsonPayload.Parse(JsonSerializer.Serialize(new {
        source = "endpoint-fixture", invocation = invocations, count = selected.Length,
        items = selected.Select(s => new { name = s.Name, code = s.Code, active = s.Active }).ToArray(),
        optional = (string?)null
    }));
}
var endpoint = new WorkerEndpoint(engine,
    new[] { new ReadOnlyOperation("List", false, List), new ReadOnlyOperation("BoundList", true, List) }, Observe);
try
{
    Frame(endpoint.CreateHello()); Header(0);
    if (mode == "binding-before") binding = ChangedBinding();
    if (mode == "epoch-before") binding = BindingSnapshot.Unbound(1);
    while (true)
    {
        byte[] header = new byte[4];
        try { input.ReadExactly(header); } catch (EndOfStreamException) { return; }
        int length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length < 1 || length > StrictCodec.MaxFrameBytes) return;
        byte[] request = new byte[length]; input.ReadExactly(request);
        endpoint.Handle(request, payload => {
            if (mode == "truncated") { Header(payload.Length); output.Write(payload.AsSpan(0, 3)); output.Flush(); Environment.Exit(0); }
            Frame(payload);
        });
        if (mode == "no-boundary") Thread.Sleep(Timeout.Infinite);
        Header(0);
        if (endpoint.Faulted) return;
    }
}
catch (Exception)
{
    // No exception text or arbitrary callback data is sent on either wire stream.
    if (marker != null) File.WriteAllText(marker + ".fault", endpoint.Faulted ? "faulted" : "unexpected");
}

static BindingSnapshot ChangedBinding() => BindingSnapshot.Bound(1, new ProjectIdentity(new string('d', 64), 31415, 638000000000000000));
sealed record Sample(string Name, int Code, bool Active);
