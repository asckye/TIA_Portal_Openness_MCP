using System.Text.Json;
using System.Text;
using TiaMcp.WorkerProtocol;

namespace TiaMcp.WorkerProtocol.JsonV2;

// This is a new, unwired protocol. It is NOT a replacement decoder for MCP or v1.
public sealed record WireId
{
    public long? Number { get; }
    public string? Text { get; }
    public WireId(long number) { StrictCodec.Require(number > 0, "OuterIdRequired"); Number = number; }
    public WireId(string text)
    {
        StrictCodec.Require(text is { Length: > 0 and <= 128 } && text.All(c => (c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9') || c is '_' or '-'), "OuterIdRequired");
        Text = text;
    }
    internal object Value => Number is long n ? n : Text!;
}
public abstract record V2Frame;
public sealed record HelloFrame(EngineIdentity Engine, BindingSnapshot Binding) : V2Frame;
public sealed record RequestFrame(WireId Id, RequestIdentity Identity, JsonElement Arguments) : V2Frame;
public sealed record ReplyFrame(WireId Id, ReplyIdentity Identity, JsonElement Result) : V2Frame;
public sealed record ProgressFrame(WireId Id, RequestIdentity Identity, long Sequence, int Percent) : V2Frame;

public static class StrictCodec
{
    // Limit applies to the whole UTF-8 frame before parsing. Transport must also bound reads.
    public const int MaxFrameBytes = 1024 * 1024;
    static readonly UTF8Encoding Utf8 = new(false, true);
    internal static void Require(bool condition, string code) { if (!condition) throw new IdentityViolation(code); }
    public static V2Frame Decode(ReadOnlyMemory<byte> bytes)
    {
        Require(bytes.Length is > 0 and <= MaxFrameBytes, "FrameSizeInvalid");
        try
        {
            _ = Utf8.GetCharCount(bytes.ToArray());
            using var doc = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32, AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow });
            var root = doc.RootElement;
            Unique(root);
            Require(root.ValueKind == JsonValueKind.Object, "ObjectRequired");
            Require(root.TryGetProperty("version", out var version) && version.ValueKind == JsonValueKind.Number && version.TryGetInt32(out var v) && v == 2, "ProtocolVersionMismatch");
            var type = String(root, "type");
            switch (type)
            {
                case "hello":
                    Fields(root, "version", "type", "engine", "binding");
                    return new HelloFrame(Engine(root.GetProperty("engine")), Binding(root.GetProperty("binding")));
                case "request":
                    Fields(root, "version", "type", "id", "identity", "arguments");
                    var args = root.GetProperty("arguments"); Require(args.ValueKind == JsonValueKind.Object, "ArgumentsObjectRequired");
                    return new RequestFrame(Id(root.GetProperty("id")), Request(root.GetProperty("identity")), args.Clone());
                case "reply":
                    Fields(root, "version", "type", "id", "identity", "observedAfter", "outcome", "result");
                    var outcome = String(root, "outcome") switch { "succeeded" => ReplyOutcome.Succeeded, "rejectedBeforeOperation" => ReplyOutcome.RejectedBeforeOperation, "readFailed" => ReplyOutcome.ReadFailed, "unknown" => ReplyOutcome.Unknown, _ => throw new IdentityViolation("ReplyOutcomeRequired") };
                    return new ReplyFrame(Id(root.GetProperty("id")), new ReplyIdentity(Request(root.GetProperty("identity")), Binding(root.GetProperty("observedAfter")), outcome), root.GetProperty("result").Clone());
                case "progress":
                    Fields(root, "version", "type", "id", "identity", "sequence", "percent");
                    long sequence = Integer(root, "sequence"), percent = Integer(root, "percent");
                    Require(sequence > 0 && percent is >= 0 and <= 100, "ProgressInvalid");
                    return new ProgressFrame(Id(root.GetProperty("id")), Request(root.GetProperty("identity")), sequence, (int)percent);
                default: throw new IdentityViolation("FrameTypeInvalid");
            }
        }
        catch (DecoderFallbackException) { throw new IdentityViolation("MalformedUtf8"); }
        catch (JsonException) { throw new IdentityViolation("MalformedJson"); }
        catch (InvalidOperationException ex) when (ex is not IdentityViolation) { throw new IdentityViolation("FieldTypeInvalid"); }
        catch (KeyNotFoundException) { throw new IdentityViolation("MissingField"); }
        catch (FormatException) { throw new IdentityViolation("FieldTypeInvalid"); }
        catch (OverflowException) { throw new IdentityViolation("FieldTypeInvalid"); }
    }
    public static byte[] Encode(V2Frame frame)
    {
        object body = frame switch
        {
            HelloFrame h => new { version = 2, type = "hello", engine = EngineObject(h.Engine), binding = BindingObject(h.Binding) },
            RequestFrame r => new { version = 2, type = "request", id = r.Id.Value, identity = RequestObject(r.Identity), arguments = r.Arguments },
            ReplyFrame r => new { version = 2, type = "reply", id = r.Id.Value, identity = RequestObject(r.Identity.Request), observedAfter = BindingObject(r.Identity.ObservedAfter), outcome = r.Identity.Outcome switch { ReplyOutcome.Succeeded => "succeeded", ReplyOutcome.RejectedBeforeOperation => "rejectedBeforeOperation", ReplyOutcome.ReadFailed => "readFailed", ReplyOutcome.Unknown => "unknown", _ => throw new IdentityViolation("ReplyOutcomeRequired") }, result = r.Result },
            ProgressFrame p => new { version = 2, type = "progress", id = p.Id.Value, identity = RequestObject(p.Identity), sequence = p.Sequence, percent = p.Percent },
            _ => throw new IdentityViolation("FrameTypeInvalid")
        };
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(body);
        _ = Decode(bytes); // Apply exactly the same structural/size checks on outgoing frames.
        return bytes;
    }
    static object EngineObject(EngineIdentity e) => new { protocol = 2, releaseKey = e.ReleaseKey, workerSha256 = e.WorkerSha256, engineSha256 = e.EngineSha256, sessionId = e.SessionId };
    static object BindingObject(BindingSnapshot b) => new { epoch = b.Epoch, state = b.IsBound ? "bound" : "unbound", project = b.Project is { } p ? new { projectSha256 = p.ProjectSha256, tiaProcessId = p.TiaProcessId, tiaProcessStartUtcTicks = p.TiaProcessStartUtcTicks } : null };
    static object RequestObject(RequestIdentity r) => new { requestId = r.RequestId, correlationId = r.CorrelationId, engine = EngineObject(r.Engine), operation = r.Operation, before = BindingObject(r.Before), expectedAfter = BindingObject(r.ExpectedAfter) };
    static EngineIdentity Engine(JsonElement e)
    {
        Fields(e, "protocol", "releaseKey", "workerSha256", "engineSha256", "sessionId");
        long protocol = Integer(e, "protocol"); Require(protocol == 2, "ProtocolVersionMismatch");
        return new EngineIdentity(2, String(e, "releaseKey"), String(e, "workerSha256"), String(e, "engineSha256"), String(e, "sessionId"));
    }
    static BindingSnapshot Binding(JsonElement b)
    {
        Fields(b, "epoch", "state", "project");
        long epoch = Integer(b, "epoch"); string state = String(b, "state"); var p = b.GetProperty("project");
        if (state == "unbound") { Require(p.ValueKind == JsonValueKind.Null, "UnboundProjectForbidden"); return BindingSnapshot.Unbound(epoch); }
        Require(state == "bound", "BindingStateRequired");
        Fields(p, "projectSha256", "tiaProcessId", "tiaProcessStartUtcTicks");
        long pid = Integer(p, "tiaProcessId"); Require(pid is > 0 and <= int.MaxValue, "TiaProcessIdentityRequired");
        return BindingSnapshot.Bound(epoch, new ProjectIdentity(String(p, "projectSha256"), (int)pid, Integer(p, "tiaProcessStartUtcTicks")));
    }
    static RequestIdentity Request(JsonElement r)
    {
        Fields(r, "requestId", "correlationId", "engine", "operation", "before", "expectedAfter");
        return new RequestIdentity(Integer(r, "requestId"), String(r, "correlationId"), Engine(r.GetProperty("engine")), String(r, "operation"), Binding(r.GetProperty("before")), Binding(r.GetProperty("expectedAfter")));
    }
    static WireId Id(JsonElement e) => e.ValueKind switch { JsonValueKind.Number when e.TryGetInt64(out long n) => new WireId(n), JsonValueKind.String => new WireId(e.GetString()!), _ => throw new IdentityViolation("OuterIdRequired") };
    static string String(JsonElement e, string key) { Require(e.TryGetProperty(key, out var v), "MissingField"); Require(v.ValueKind == JsonValueKind.String, "FieldTypeInvalid"); return v.GetString()!; }
    static long Integer(JsonElement e, string key) { Require(e.TryGetProperty(key, out var v), "MissingField"); Require(v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out _), "IntegerRequired"); return v.GetInt64(); }
    static void Fields(JsonElement e, params string[] allowed)
    {
        Require(e.ValueKind == JsonValueKind.Object, "ObjectRequired");
        foreach (var p in e.EnumerateObject()) Require(allowed.Contains(p.Name, StringComparer.Ordinal), "UnknownField");
        foreach (string field in allowed) Require(e.TryGetProperty(field, out _), "MissingField");
    }
    static void Unique(JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.Object)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in e.EnumerateObject()) { Require(seen.Add(p.Name), "DuplicateOrAmbiguousField"); Unique(p.Value); }
        }
        else if (e.ValueKind == JsonValueKind.Array) foreach (var item in e.EnumerateArray()) Unique(item);
        else if (e.ValueKind == JsonValueKind.String) _ = e.GetString(); // Force deferred escaped-surrogate validation, including opaque payloads.
    }
}
