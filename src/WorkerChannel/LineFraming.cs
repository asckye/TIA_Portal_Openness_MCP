using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace TiaMcp.WorkerChannel
{
    // Bound bytes while reading, before UTF-8 decoding or JSON parsing. A truncated
    // final line is never a frame, and a UTF-8 BOM is never silently consumed.
    internal sealed class LineFraming
    {
        private readonly Stream input;
        private readonly int limit;
        private readonly byte[] buffer = new byte[4096];
        private int offset, count;
        internal bool HasBufferedData => offset < count;
        internal LineFraming(Stream input, int limit) { this.input = input; this.limit = limit; }

        internal Task<byte[]?> ReadAsync(CancellationToken token) => Read(true, token);
        internal byte[]? Read() => Read(false, CancellationToken.None).GetAwaiter().GetResult();

        private async Task<byte[]?> Read(bool asynchronous, CancellationToken token)
        {
            using (var line = new MemoryStream())
            {
                while (true)
                {
                    if (offset == count)
                    {
                        count = asynchronous
                            ? await input.ReadAsync(buffer, 0, buffer.Length, token).ConfigureAwait(false)
                            : input.Read(buffer, 0, buffer.Length);
                        offset = 0;
                        if (count == 0)
                        {
                            if (line.Length != 0) throw new IOException("Truncated worker frame.");
                            return null;
                        }
                    }
                    int end = offset;
                    while (end < count && buffer[end] != 10) end++;
                    if (line.Length + end - offset > limit) throw new IOException("Worker frame exceeds its byte limit.");
                    line.Write(buffer, offset, end - offset);
                    offset = end;
                    if (end < count)
                    {
                        offset++;
                        var bytes = line.ToArray();
                        if (bytes.Length == 0) throw new IOException("Empty worker frame.");
                        return bytes;
                    }
                }
            }
        }
    }
}
