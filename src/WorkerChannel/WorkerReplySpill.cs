using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TiaMcp.WorkerChannel
{
    public static class WorkerReplySpill
    {
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
        private static string Hash(byte[] bytes)
        {
            using var hash = SHA256.Create();
            return BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
        }
        public static string Write(string directory, string json)
        {
            Directory.CreateDirectory(directory);
            string id = Guid.NewGuid().ToString("N");
            string path = Path.Combine(directory, id + ".json"), pending = path + ".pending";
            var bytes = Utf8.GetBytes(json);
            using (var stream = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { stream.Write(bytes, 0, bytes.Length); stream.Flush(true); }
            File.Move(pending, path);
            return JsonSerializer.Serialize(new { workerReplySpill = new { id, byteLength = bytes.LongLength, sha256 = Hash(bytes) } });
        }
        public static string Read(string directory, string json, out bool spilled)
        {
            using var document = JsonDocument.Parse(json);
            JsonElement descriptor = default;
            spilled = document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("workerReplySpill", out descriptor);
            if (!spilled) return json;
            ChannelCodec.Fields(document.RootElement, "workerReplySpill");
            ChannelCodec.Fields(descriptor, "id", "byteLength", "sha256");
            string id = ChannelCodec.Text(descriptor, "id");
            if (id.Length != 32 || !Guid.TryParseExact(id, "N", out _)) throw new IOException("Invalid worker spill identity.");
            string path = Path.Combine(directory, id + ".json");
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0 || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Worker spill must be a regular file in the data directory.");
            var bytes = File.ReadAllBytes(path);
            if (bytes.LongLength != ChannelCodec.Number(descriptor, "byteLength") || Hash(bytes) != ChannelCodec.Text(descriptor, "sha256"))
                throw new IOException("Truncated or modified worker spill.");
            string full = Utf8.GetString(bytes);
            using (JsonDocument.Parse(full)) { }
            File.Delete(path); // Only the validated, server-owned spill is consumed.
            return full;
        }
    }
}
