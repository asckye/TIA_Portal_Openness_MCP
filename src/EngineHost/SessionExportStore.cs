using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcp.FoundationHost
{
    // Keep the existing process memory bound while restricting handle access and
    // cleanup to the MCP session that parked the response.
    internal sealed class SessionExports : IDisposable
    {
        internal readonly ConcurrentDictionary<string, byte> Handles = new(StringComparer.Ordinal);
        private readonly object sync = new();
        private readonly Queue<string> issued = new();
        private bool ended;
        internal void Record(string id)
        {
            lock (sync)
            {
                if (ended) { ExportStore.Delete(id); throw new InvalidOperationException("The MCP engine session has ended."); }
                Handles[id] = 0;
                issued.Enqueue(id);
                // Retain the pool's 32 live entries plus 512 tombstones at most.
                while (issued.Count > 544) Handles.TryRemove(issued.Dequeue(), out _);
            }
        }
        public void Dispose()
        {
            lock (sync)
            {
                ended = true;
                foreach (string id in Handles.Keys) ExportStore.Delete(id);
                Handles.Clear(); issued.Clear();
            }
        }
    }

    internal static class SessionExportStore
    {
        internal const int DefaultTtlHours = ExportStore.DefaultTtlHours;
        internal const int MaxSliceChars = ExportStore.MaxSliceChars;
        private static SessionExports Current => EngineHostConfiguration.Current.Exports;
        internal static (string id, ExportSlice head) PutAndSlice(string tool, string target, string content, int length)
        {
            var owner = Current;
            var result = ExportStore.PutAndSlice(tool, target, content, length);
            owner.Record(result.id);
            return result;
        }
        internal static ExportEntry? Get(string? id) => id != null && Current.Handles.ContainsKey(id) ? ExportStore.Get(id) : null;
        internal static ExportSlice Slice(string? id, int offset, int length) => id != null && Current.Handles.ContainsKey(id)
            ? ExportStore.Slice(id, offset, length) : new ExportSlice { Id = id ?? "", Error = "unknown",
                Message = $"Handle {id} was not found. Use ListExportHandles to see the current handles, "
                    + "or run the producing tool again to obtain a new one." };
        internal static IList<ExportEntry> List(string? tool, int limit) => ExportStore.List(tool, int.MaxValue)
            .Where(entry => Current.Handles.ContainsKey(entry.Id)).Take(limit <= 0 ? 20 : limit).ToList();
        internal static bool Delete(string? id) => id != null && Current.Handles.ContainsKey(id) && ExportStore.Delete(id);
        internal static int Clear(int olderThanHours)
        {
            DateTime cutoff = DateTime.UtcNow.AddHours(-olderThanHours);
            int count = 0;
            foreach (var entry in List(null, int.MaxValue))
                if ((olderThanHours <= 0 || entry.CreatedUtc <= cutoff) && Delete(entry.Id)) count++;
            return count;
        }
        internal static (int count, long chars) Stats()
        {
            var entries = List(null, int.MaxValue);
            return (entries.Count, entries.Sum(entry => (long)entry.Length));
        }
    }
}
