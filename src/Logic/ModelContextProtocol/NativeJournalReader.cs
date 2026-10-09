using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TiaMcp.Logic.ModelContextProtocol
{
    public static class NativeJournalReader
    {
        public static JsonObject Read(string root, int take)
        {
            if (take < 1 || take > 500) throw new ArgumentException("take must be 1..500.");
            if (!Path.IsPathRooted(root)) throw new ArgumentException("Diagnostics directory must be absolute.");
            var files = Directory.Exists(root) ? new DirectoryInfo(root).GetFiles("calls-*.jsonl*")
                .Where(f => f.Name.EndsWith(".jsonl", StringComparison.Ordinal) || f.Name.EndsWith(".jsonl.previous", StringComparison.Ordinal))
                .OrderBy(f => f.LastWriteTimeUtc).ThenBy(f => f.Name, StringComparer.Ordinal).ToArray() : Array.Empty<FileInfo>();
            var rows = new List<(DateTimeOffset Time, long Order, JsonNode Row)>();
            int malformed = 0;
            long order = 0;
            foreach (var file in files)
                using (var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new StreamReader(stream))
                    while (reader.ReadLine() is string line)
                    {
                        try
                        {
                            var row = JsonNode.Parse(line);
                            if (row == null) continue;
                            if (row is not JsonObject) { malformed++; continue; }
                            var time = DateTimeOffset.TryParse(row["utc"]?.ToString(), out var utc) ? utc : new DateTimeOffset(file.LastWriteTimeUtc);
                            rows.Add((time, order++, row));
                            rows.Sort((a, b) => { int timeOrder = a.Time.CompareTo(b.Time); return timeOrder != 0 ? timeOrder : a.Order.CompareTo(b.Order); });
                            if (rows.Count > take) rows.RemoveAt(0);
                        }
                        catch (JsonException) /* swallow(parse-fallback): count partial or malformed journal rows and preserve the remaining crash evidence */ { malformed++; }
                    }
            return new JsonObject { ["success"] = true, ["directory"] = root,
                ["records"] = new JsonArray(rows.Select(r => r.Row).ToArray()), ["malformedLines"] = malformed, ["filesRead"] = files.Length };
        }
    }
}
