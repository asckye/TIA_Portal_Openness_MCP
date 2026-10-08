using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace TiaMcp.WorkerChannel
{
    internal static class ChannelCodec
    {
        internal const int RequestLimit = 1024 * 1024;
        internal const int ResponseLimit = 16 * 1024 * 1024;
        internal const int ProgressLimit = 1024;

        internal static bool ValidMethod(string method, ChannelProfile profile) =>
            profile == ChannelProfile.Studio ? !string.IsNullOrWhiteSpace(method) :
            profile == ChannelProfile.Engine ? method.StartsWith("engine.", StringComparison.Ordinal) && method.Length > 7 :
            method.StartsWith("adapter.", StringComparison.Ordinal) && method.Length > 8;

        internal static JsonDocument Parse(byte[] bytes, int limit)
        {
            if (bytes.Length > limit) throw new IOException("Worker frame exceeds its byte limit.");
            new UTF8Encoding(false, true).GetCharCount(bytes);
            return JsonDocument.Parse(bytes);
        }

        internal static void Fields(JsonElement value, params string[] names)
        {
            if (value.ValueKind != JsonValueKind.Object) throw new IOException("Expected worker envelope object.");
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var field in value.EnumerateObject())
                if (!seen.Add(field.Name) || !names.Contains(field.Name, StringComparer.Ordinal)) throw new IOException("Duplicate or unknown worker envelope field.");
        }
        internal static string Text(JsonElement value, string name)
        {
            if (!value.TryGetProperty(name, out var field) || field.ValueKind != JsonValueKind.String) throw new IOException("Missing worker string: " + name);
            return field.GetString()!;
        }
        internal static long Number(JsonElement value, string name)
        {
            if (!value.TryGetProperty(name, out var field) || field.ValueKind != JsonValueKind.Number || !field.TryGetInt64(out long number) || number < 0)
                throw new IOException("Missing worker integer: " + name);
            return number;
        }
        internal static void Version(JsonElement value)
        { if (Text(value, "jsonrpc") != "2.0") throw new IOException("Worker requires JSON-RPC 2.0."); }

        internal static byte[] Encode(Action<Utf8JsonWriter> body, int limit)
        {
            using (var stream = new MemoryStream())
            {
                using (var writer = new Utf8JsonWriter(stream))
                {
                    writer.WriteStartObject(); writer.WriteString("jsonrpc", "2.0"); body(writer); writer.WriteEndObject();
                }
                if (stream.Length > limit) throw new ChannelLimitException();
                stream.WriteByte(10);
                return stream.ToArray();
            }
        }
        internal static void Raw(Utf8JsonWriter writer, string name, string json, int limit)
        {
            if (Encoding.UTF8.GetByteCount(json) > limit) throw new ChannelLimitException();
            writer.WritePropertyName(name); writer.WriteRawValue(json);
        }
        internal static byte[] Hello(ChannelIdentity identity, ChannelBinding binding) => Encode(w =>
        {
            w.WriteString("method", "hello"); w.WriteStartObject("params");
            w.WriteNumber("protocol", 2); w.WriteString("releaseKey", identity.ReleaseKey);
            w.WriteString("workerSha256", identity.WorkerSha256); w.WriteString("adapterSha256", identity.AdapterSha256);
            w.WriteNumber("pid", identity.ProcessId); w.WriteString("nonce", identity.Nonce);
            w.WriteNumber("bindingEpoch", binding.Epoch); w.WriteBoolean("bound", binding.Bound); w.WriteEndObject();
        }, ResponseLimit);

        internal static void VerifyHello(JsonElement root, ChannelIdentity expected)
        {
            Fields(root, "jsonrpc", "method", "params"); Version(root);
            if (Text(root, "method") != "hello") throw new IOException("Worker hello must be the first frame.");
            var p = root.GetProperty("params");
            Fields(p, "protocol", "releaseKey", "workerSha256", "adapterSha256", "pid", "nonce", "bindingEpoch", "bound");
            if (Number(p, "protocol") != 2 || Text(p, "releaseKey") != expected.ReleaseKey ||
                Text(p, "workerSha256") != expected.WorkerSha256 || Text(p, "adapterSha256") != expected.AdapterSha256 ||
                Number(p, "pid") != expected.ProcessId || Text(p, "nonce") != expected.Nonce ||
                Number(p, "bindingEpoch") != 0 || p.GetProperty("bound").ValueKind != JsonValueKind.False)
                throw new IOException("Worker hello identity mismatch or nonfresh binding.");
        }

        internal static byte[] Request(long id, string method, string args, long epoch) => Encode(w =>
        {
            w.WriteNumber("id", id); w.WriteString("method", method); w.WriteNumber("bindingEpoch", epoch);
            Raw(w, "params", args, RequestLimit);
        }, RequestLimit);

        internal static byte[] Reply(long id, long before, long after, ChannelResponse response, ChannelProfile profile) => Encode(w =>
        {
            w.WriteNumber("id", id); w.WriteNumber("bindingEpochBefore", before); w.WriteNumber("bindingEpochAfter", after);
            if (response.Failure == null) Raw(w, "result", response.ResultJson, ResponseLimit);
            else
            {
                var failure = response.Failure;
                w.WriteStartObject("error"); w.WriteNumber("code", failure.Code); w.WriteString("message", failure.Message);
                w.WriteStartObject("data"); w.WriteString("outcome", failure.Outcome.ToString());
                Raw(w, "evidence", failure.EvidenceJson, ResponseLimit);
                if (profile == ChannelProfile.Studio) Raw(w, "rpc", failure.RpcErrorJson ?? "null", ResponseLimit);
                w.WriteEndObject(); w.WriteEndObject();
            }
        }, ResponseLimit);

        internal static ChannelResponse Response(JsonElement root, ChannelProfile profile)
        {
            bool result = root.TryGetProperty("result", out var value), error = root.TryGetProperty("error", out var failure);
            if (result == error) throw new IOException("Worker response must contain exactly one result or error.");
            if (result) return ChannelResponse.Success(value.GetRawText());
            Fields(failure, "code", "message", "data");
            int code = failure.GetProperty("code").GetInt32();
            if (code != -32602 && code != -32603) throw new IOException("Malformed worker error code.");
            var data = failure.GetProperty("data");
            Fields(data, profile == ChannelProfile.Studio ? new[] { "outcome", "evidence", "rpc" } : new[] { "outcome", "evidence" });
            string outcome = Text(data, "outcome");
            if (outcome != "RejectedBeforeNative" && outcome != "ReadFailed" && outcome != "Unknown") throw new IOException("Malformed worker failure classification.");
            var evidence = data.GetProperty("evidence");
            if (evidence.ValueKind != JsonValueKind.Object && evidence.ValueKind != JsonValueKind.Null) throw new IOException("Malformed worker failure evidence.");
            string? rpc = null;
            if (profile == ChannelProfile.Studio)
            {
                var original = data.GetProperty("rpc");
                Fields(original, "code", "message", "data");
                original.GetProperty("code").GetInt32(); Text(original, "message"); original.GetProperty("data");
                rpc = original.GetRawText();
            }
            return ChannelResponse.Error(new ChannelFailure(Text(failure, "message"), code, (ChannelOutcome)Enum.Parse(typeof(ChannelOutcome), outcome), evidence.GetRawText(), rpc));
        }

        internal static byte[] Progress(long id, int sequence, int percent, string? payload) => Encode(w =>
        {
            w.WriteString("method", "progress"); w.WriteStartObject("params"); w.WriteNumber("requestId", id);
            w.WriteNumber("sequence", sequence); w.WriteNumber("percent", percent);
            if (payload != null) Raw(w, "payload", payload, ResponseLimit);
            w.WriteEndObject();
        }, ResponseLimit);
    }
}
