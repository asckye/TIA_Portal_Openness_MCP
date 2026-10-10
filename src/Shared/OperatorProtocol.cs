using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace TiaOpenness.Shared
{
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    internal sealed class OperatorHandshake
    {
        [JsonRequired] public string Protocol { get; set; } = "tiamcp.operator";
        [JsonRequired] public int Version { get; set; } = 1;
        [JsonRequired] public string Action { get; set; } = "";
        public string? SessionKey { get; set; }
        [JsonRequired] public OperatorWorkbench Workbench { get; set; } = null!;
    }
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    internal sealed class OperatorWorkbench
    {
        [JsonRequired] public int Pid { get; set; }
        [JsonRequired] public string Version { get; set; } = "";
    }
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    internal sealed class OperatorHandshakeResult
    {
        [JsonRequired] public string Protocol { get; set; } = "tiamcp.operator";
        [JsonRequired] public int Version { get; set; } = 1;
        [JsonRequired] public bool Accepted { get; set; }
        public string? Error { get; set; }
        public string? SessionKey { get; set; }
        public JsonObject[] Sessions { get; set; } = Array.Empty<JsonObject>();
    }
    internal static class OperatorProtocol
    {
        internal const string SessionStateNotification = "notifications/tiamcp/sessionState";
        internal const int MaximumBytes = 256 * 1024;
        internal static readonly JsonSerializerOptions Json = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            MaxDepth = 32
        };
        internal static void Validate(OperatorHandshake request)
        {
            if (request.Protocol != "tiamcp.operator" || request.Version < 1
                || request.Workbench == null || request.Workbench.Pid <= 0
                || string.IsNullOrWhiteSpace(request.Workbench.Version) || request.Workbench.Version.Length > 128)
                throw new InvalidDataException("Invalid operator handshake.");
            if (request.Action != "list" && request.Action != "attach" && request.Action != "create")
                throw new InvalidDataException("Unknown operator action.");
            if (request.Action == "attach" ? !Guid.TryParseExact(request.SessionKey, "N", out _) : request.SessionKey != null)
                throw new InvalidDataException("Invalid operator session key.");
        }
        internal static async Task Write<T>(Stream stream, T frame, CancellationToken token)
        {
            byte[] data = JsonSerializer.SerializeToUtf8Bytes(frame, Json);
            if (data.Length < 1 || data.Length > MaximumBytes) throw new InvalidDataException("Operator frame exceeds its budget.");
            var header = new[] { (byte)data.Length, (byte)(data.Length >> 8), (byte)(data.Length >> 16), (byte)(data.Length >> 24) };
            await stream.WriteAsync(header, 0, header.Length, token).ConfigureAwait(false);
            await stream.WriteAsync(data, 0, data.Length, token).ConfigureAwait(false);
            await stream.FlushAsync(token).ConfigureAwait(false);
        }
        internal static async Task<T> Read<T>(Stream stream, CancellationToken token)
        {
            var header = new byte[4]; await Exact(stream, header, token).ConfigureAwait(false);
            int size = header[0] | header[1] << 8 | header[2] << 16 | header[3] << 24;
            if (size < 1 || size > MaximumBytes) throw new InvalidDataException("Invalid operator frame length.");
            var data = new byte[size]; await Exact(stream, data, token).ConfigureAwait(false);
            try
            {
                new UTF8Encoding(false, true).GetCharCount(data);
                using (var document = JsonDocument.Parse(data, new JsonDocumentOptions { MaxDepth = 32 }))
                {
                    if (document.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Expected an operator object.");
                    Unique(document.RootElement);
                }
                var frame = JsonSerializer.Deserialize<T>(data, Json);
                if (frame == null) throw new InvalidDataException("Missing operator frame.");
                if (frame is OperatorHandshake request) Validate(request);
                return frame;
            }
            catch (JsonException error) { throw new InvalidDataException("Invalid operator frame.", error); }
            catch (DecoderFallbackException error) { throw new InvalidDataException("Invalid operator UTF-8.", error); }
        }
        private static void Unique(JsonElement node)
        {
            if (node.ValueKind == JsonValueKind.Object)
            {
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var field in node.EnumerateObject())
                {
                    if (!names.Add(field.Name)) throw new InvalidDataException("Duplicate operator field.");
                    Unique(field.Value);
                }
            }
            else if (node.ValueKind == JsonValueKind.Array)
                foreach (var item in node.EnumerateArray()) Unique(item);
        }
        private static async Task Exact(Stream stream, byte[] bytes, CancellationToken token)
        {
            int offset = 0;
            while (offset < bytes.Length)
            {
                int read = await stream.ReadAsync(bytes, offset, bytes.Length - offset, token).ConfigureAwait(false);
                if (read == 0) throw new EndOfStreamException();
                offset += read;
            }
        }
    }
}
