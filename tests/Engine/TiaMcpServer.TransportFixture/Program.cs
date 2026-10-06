using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.WorkerChannel;

// Synthetic worker: no Siemens references, native loading or network operations.
Console.InputEncoding = new UTF8Encoding(false);
Console.OutputEncoding = new UTF8Encoding(false);
var log = Environment.GetEnvironmentVariable("TIA_FIXTURE_LOG") ?? throw new ArgumentException("Fixture log required");
var fault = Environment.GetEnvironmentVariable("TIA_FIXTURE_FAULT") ?? "";
void Record(object value) => File.AppendAllText(log, JsonSerializer.Serialize(value) + "\n", new UTF8Encoding(false));
Record(new { stage = "start", pid = Environment.ProcessId, args });
if (args.Length != 4 || args[0] != "--native-session") throw new ArgumentException("Protocol 2 launch arguments required.");
string Hash(string path) { using var file=File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant(); }
var identity = new ChannelIdentity(args[1], Hash(Environment.ProcessPath!),
    Hash(Path.Combine(AppContext.BaseDirectory,"TiaMcp.Adapter."+args[1]+".dll")), Environment.ProcessId, args[3]);
int? attached = null;
long epoch = 0;
using var output = new FaultOutput(Console.OpenStandardOutput(), fault);
var server = new ChannelServer(Console.OpenStandardInput(), output, identity, () => new ChannelBinding(epoch, attached.HasValue), request =>
{
    var operation = request.Method.Substring("adapter.".Length);
    var values = JsonNode.Parse(request.ArgumentsJson)!;
    Record(new { stage = "call", pid = Environment.ProcessId, operation, request.Id, request.Method });
    if (fault == "broken-pipe") Environment.Exit(0);
    if (fault == "timeout" || fault == "timeout-attach" && operation == "Attach") Thread.Sleep(10000);
    if (fault == "read-failed") return ChannelResponse.Error(new ChannelFailure("Fixture read failed", -32603, ChannelOutcome.ReadFailed, "{\"inputFile\":\"生产线\"}"));
    if (fault == "rejected") return ChannelResponse.Error(new ChannelFailure("Fixture rejected", -32602, ChannelOutcome.RejectedBeforeNative));
    if (fault == "session-fault") return ChannelResponse.Error(new ChannelFailure("Fixture outcome unknown", -32603, ChannelOutcome.Unknown));
    if (fault == "progress") { request.Progress(10); request.Progress(100); }
    JsonNode? result;
    switch (operation)
    {
        case "Attach":
            attached = values["processId"]!.GetValue<int>(); epoch++;
            result = new JsonObject { ["Stage"]="attached", ["OwnsPortal"]=false, ["AttemptedPids"]=new JsonArray(attached.Value), ["Strategy"]="non-owning-attachment-only", ["LaunchMode"]="never" };
            break;
        case "ReadState": result = new JsonObject { ["IsAttached"]=attached.HasValue, ["ProcessId"]=attached }; break;
        case "ReadProjectTree": result = JsonValue.Create("生产线 / PLC_测试 / 程序块"); break;
        case "Disconnect":
            result = new JsonObject { ["Stage"]="disconnected", ["SessionState"]="terminal", ["Strategy"]="non-owning-attachment-only", ["WorkerAcknowledged"]=true, ["Detached"]=attached.HasValue, ["ProcessId"]=attached, ["SavedProject"]=false, ["ClosedProject"]=false, ["LaunchMode"]="never" };
            if (attached.HasValue) epoch++;
            attached = null;
            break;
        default: result = values; break;
    }
    return ChannelResponse.Success(result?.ToJsonString() ?? "null");
});
try { server.Run(); }
catch (ChannelFault error) { Console.Error.WriteLine(error.Message); return 1; }
finally { Record(new { stage = "exit", pid = Environment.ProcessId }); }
return 0;

sealed class FaultOutput(Stream stream, string fault) : Stream
{
    private byte[]? hello;
    public override void Write(byte[] buffer, int offset, int count)
    {
        var bytes = buffer.AsSpan(offset,count).ToArray();
        var root = JsonNode.Parse(bytes)!;
        bool greeting = root["method"]?.GetValue<string>() == "hello";
        if (greeting)
        {
            hello = bytes;
            var p = root["params"]!;
            switch (fault)
            {
                case "release": p["releaseKey"]="wrong"; break;
                case "worker-hash": p["workerSha256"]=new string('0',64); break;
                case "adapter-hash": p["adapterSha256"]=new string('0',64); break;
                case "pid": p["pid"]=Environment.ProcessId+1; break;
                case "nonce": p["nonce"]=new string('0',64); break;
                case "version": p["protocol"]=1; break;
                case "nonfresh": p["bindingEpoch"]=1; break;
                case "bound": p["bound"]=true; break;
                case "missing-hello": return;
                case "startup-exit": Environment.Exit(0); return;
                case "hello-oversized": Oversized(); return;
            }
        }
        else if (root["id"] != null)
        {
            switch (fault)
            {
                case "late-hello": stream.Write(hello!); return;
                case "unknown-id": root["id"]=999999; break;
                case "old-id": root["id"]=0; break;
                case "epoch-before": root["bindingEpochBefore"]=999; break;
                case "epoch-after": root["bindingEpochAfter"]=999; break;
                case "bad-framing": stream.Write(Encoding.UTF8.GetBytes("{bad}\n")); return;
                case "invalid-utf8": stream.Write(new byte[]{255,10}); return;
                case "truncated": stream.Write(Encoding.UTF8.GetBytes("{\"jsonrpc\":")); stream.Flush(); Environment.Exit(0); return;
                case "oversized": Oversized(); return;
            }
        }
        var line = Encoding.UTF8.GetBytes(root.ToJsonString()+"\n");
        stream.Write(line);
        if (greeting && fault=="duplicate-hello") stream.Write(line);
        if (!greeting && root["id"]!=null && fault=="second-reply") stream.Write(line);
        if (!greeting && root["id"]!=null && fault=="late-progress")
            stream.Write(Encoding.UTF8.GetBytes(new JsonObject { ["jsonrpc"]="2.0",["method"]="progress",["params"]=new JsonObject { ["requestId"]=root["id"]!.DeepClone(),["sequence"]=1,["percent"]=50 } }.ToJsonString()+"\n"));
    }
    private void Oversized()
    {
        var chunk = Encoding.UTF8.GetBytes(new string('x',4096));
        for(int i=0;i<4097;i++) stream.Write(chunk);
        stream.WriteByte(10);
    }
    public override void Flush() => stream.Flush();
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer,int offset,int count) => throw new NotSupportedException();
    public override long Seek(long offset,SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
