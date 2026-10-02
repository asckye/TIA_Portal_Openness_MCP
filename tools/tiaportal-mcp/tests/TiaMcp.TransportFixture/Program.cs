// Test-only pipe peer. Not an SDK substitute, native worker, or production executable.
using System.Buffers.Binary;
using System.Diagnostics;
using System.Text.Json;
using TiaMcp.WorkerProtocol;
using TiaMcp.WorkerProtocol.JsonV2;

string mode = args[0];
if (args.Length == 2) await File.WriteAllTextAsync(args[1], Environment.ProcessId.ToString());
var input = Console.OpenStandardInput(); var output = Console.OpenStandardOutput();
async Task Header(int length) { byte[] b = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(b,length); await output.WriteAsync(b); await output.FlushAsync(); }
async Task Frame(byte[] b) { await Header(b.Length); await output.WriteAsync(b); await output.FlushAsync(); }
if (mode == "startup-exit") return;
if (mode == "startup-hang") { await Task.Delay(Timeout.Infinite); return; }
byte[] identity = new byte[36];
Convert.FromHexString(Environment.GetEnvironmentVariable("TIA_TRANSPORT_OWNER_TOKEN")!).CopyTo(identity,0);
Environment.SetEnvironmentVariable("TIA_TRANSPORT_OWNER_TOKEN", null);
BinaryPrimitives.WriteInt32LittleEndian(identity.AsSpan(32), Environment.ProcessId);
if (mode == "token") identity[0] ^= 1;
if (mode == "pid") BinaryPrimitives.WriteInt32LittleEndian(identity.AsSpan(32), 1);
await output.WriteAsync(identity); await output.FlushAsync();
var engine = new EngineIdentity(2, mode == "release" ? "21" : "20", new string(mode == "hash" ? 'e' : 'a',64), new string('b',64), new string('c',32));
if (mode == "hello-oversized") { await Header(int.MaxValue); await Task.Delay(Timeout.Infinite); return; }
await Frame(StrictCodec.Encode(new HelloFrame(engine, BindingSnapshot.Unbound(0)))); await Header(0);
if (mode == "ready-exit") return;
if (mode == "no-read") { await Task.Delay(Timeout.Infinite); return; }
while (true)
{
    byte[] h = new byte[4];
    try { await input.ReadExactlyAsync(h); } catch (EndOfStreamException) { return; }
    int length = BinaryPrimitives.ReadInt32LittleEndian(h);
    if (length < 1 || length > StrictCodec.MaxFrameBytes) return;
    byte[] payload = new byte[length]; await input.ReadExactlyAsync(payload);
    var request = (RequestFrame)StrictCodec.Decode(payload);
    if (mode == "exit") return;
    if (mode == "hang") { await Task.Delay(Timeout.Infinite); return; }
    if (mode == "oversized") { await Header(int.MaxValue); await Task.Delay(Timeout.Infinite); return; }
    if (mode == "negative") { await Header(-1); await Task.Delay(Timeout.Infinite); return; }
    if (mode == "partial-header") { await output.WriteAsync(new byte[1]); await output.FlushAsync(); await Task.Delay(Timeout.Infinite); return; }
    if (mode == "progress-flood")
        for (int i=1; i<=40; i++) await Frame(StrictCodec.Encode(new ProgressFrame(request.Id,request.Identity,i,25)));
    if (mode == "truncated") { await Header(100); await output.WriteAsync(new byte[3]); return; }
    using var result = JsonDocument.Parse("{\"ok\":true}");
    byte[] reply = StrictCodec.Encode(new ReplyFrame(request.Id, new ReplyIdentity(request.Identity,
        request.Identity.ExpectedAfter, mode == "unknown" ? ReplyOutcome.Unknown : ReplyOutcome.Succeeded),result.RootElement));
    if (mode == "stderr") await Console.Error.WriteAsync(new string('x', 256 * 1024));
    await Frame(reply);
    if (mode == "trailing") await Frame(reply);
    if (mode == "no-boundary") { await Task.Delay(Timeout.Infinite); return; }
    await Header(0);
}
