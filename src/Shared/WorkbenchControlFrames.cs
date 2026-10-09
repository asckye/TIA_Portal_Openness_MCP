using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TiaOpenness.Shared
{
    // This parser deliberately shares neither DTOs nor framing code with other channels.
    internal static class WorkbenchControlFrames
    {
        internal const int MaximumBytes = 256 * 1024;
        internal static async Task Write<T>(Stream stream, T frame, CancellationToken token)
        {
            if (frame == null) throw new InvalidDataException("Missing workbench control frame.");
            WorkbenchControlProtocol.Validate(frame);
            byte[] data;
            try { data = JsonSerializer.SerializeToUtf8Bytes(frame, WorkbenchControlProtocol.Json); }
            catch (JsonException error) { throw new InvalidDataException("Invalid workbench control frame.", error); }
            if (data.Length < 1 || data.Length > MaximumBytes) throw new InvalidDataException("Workbench control frame exceeds its budget.");
            byte[] header = new[] { (byte)data.Length, (byte)(data.Length >> 8), (byte)(data.Length >> 16), (byte)(data.Length >> 24) };
            await stream.WriteAsync(header, 0, header.Length, token).ConfigureAwait(false);
            await stream.WriteAsync(data, 0, data.Length, token).ConfigureAwait(false);
            await stream.FlushAsync(token).ConfigureAwait(false);
        }
        internal static async Task<T> Read<T>(Stream stream, CancellationToken token)
        {
            byte[] header = new byte[4]; await Exact(stream, header, token).ConfigureAwait(false);
            int size = header[0] | header[1] << 8 | header[2] << 16 | header[3] << 24;
            if (size < 1 || size > MaximumBytes) throw new InvalidDataException("Invalid workbench control frame length.");
            byte[] data = new byte[size]; await Exact(stream, data, token).ConfigureAwait(false);
            try
            {
                // Reject invalid UTF-8 before any DTO string can be decoded with replacement.
                new UTF8Encoding(false, true).GetCharCount(data);
                using (var doc = JsonDocument.Parse(data, new JsonDocumentOptions { MaxDepth = 32 }))
                {
                    if (doc.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Expected a control object.");
                    Unique(doc.RootElement);
                }
                var frame = JsonSerializer.Deserialize<T>(data, WorkbenchControlProtocol.Json);
                if (frame == null) throw new InvalidDataException("Missing workbench control frame.");
                WorkbenchControlProtocol.Validate(frame);
                return frame;
            }
            catch (JsonException error) { throw new InvalidDataException("Invalid workbench control frame.", error); }
            catch (DecoderFallbackException error) { throw new InvalidDataException("Invalid workbench control UTF-8.", error); }
        }
        internal static async Task<WorkbenchControlResponse> ReadResponse(Stream stream, string requestId, CancellationToken token)
        {
            var response = await Read<WorkbenchControlResponse>(stream, token).ConfigureAwait(false);
            if (response.RequestId != requestId) throw new InvalidDataException("Mismatched workbench control response.");
            return response;
        }
        private static void Unique(JsonElement node)
        {
            if (node.ValueKind == JsonValueKind.Object)
            {
                var fields = new HashSet<string>(StringComparer.Ordinal);
                foreach (var field in node.EnumerateObject())
                {
                    if (!fields.Add(field.Name)) throw new InvalidDataException("Duplicate workbench control field.");
                    Unique(field.Value);
                }
            }
            else if (node.ValueKind == JsonValueKind.Array)
                foreach (var item in node.EnumerateArray()) Unique(item);
        }
        private static async Task Exact(Stream stream, byte[] data, CancellationToken token)
        {
            int offset = 0;
            while (offset < data.Length)
            {
                int read = await stream.ReadAsync(data, offset, data.Length - offset, token).ConfigureAwait(false);
                if (read == 0) throw new EndOfStreamException();
                offset += read;
            }
        }
    }
}
