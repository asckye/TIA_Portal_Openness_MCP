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
        public static V4.Error? PagingError(JsonElement arguments)
        {
            foreach (var bound in new[] { (Name: "take", Minimum: 1, Maximum: 500), (Name: "offset", Minimum: 0, Maximum: 10000) })
                if (arguments.TryGetProperty(bound.Name, out var value) && value.ValueKind == JsonValueKind.Number
                    && value.TryGetDouble(out double number) && (number < bound.Minimum || number > bound.Maximum))
                    return new V4.Error(bound.Name + " must be " + bound.Minimum + ".." + bound.Maximum + ".",
                        new V4.InvalidArgumentDetails(bound.Name, Array.Empty<string>(), expected: "integer within the declared range"));
            return null;
        }

        public static JsonObject Read(string root, int take) => Read(root, take, null, null);

        public static JsonObject Read(string root, int take, string? requestId, DateTimeOffset? sinceUtc,
            string? diagnosticId, int offset = 0, string? excludeRequestId = null)
        {
            if (take < 1 || take > 500) throw new ArgumentException("take must be 1..500.", nameof(take));
            if (offset < 0 || offset > 10000) throw new ArgumentException("offset must be 0..10000.", nameof(offset));
            if (!Path.IsPathRooted(root)) throw new ArgumentException("Diagnostics directory must be absolute.");
            var files = Directory.Exists(root) ? new DirectoryInfo(root).GetFiles("calls-*.jsonl*")
                .Where(f => f.Name.EndsWith(".jsonl", StringComparison.Ordinal) || f.Name.EndsWith(".jsonl.previous", StringComparison.Ordinal))
                .OrderBy(f => f.LastWriteTimeUtc).ThenBy(f => f.Name, StringComparer.Ordinal).ToArray() : Array.Empty<FileInfo>();
            var rows = new SortedSet<(DateTimeOffset Time, long Order, JsonNode Row)>(
                Comparer<(DateTimeOffset Time, long Order, JsonNode Row)>.Create((a, b) => {
                    int timeOrder = a.Time.CompareTo(b.Time);
                    return timeOrder != 0 ? timeOrder : a.Order.CompareTo(b.Order);
                }));
            int malformed = 0, matched = 0;
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
                            var time = DateTimeOffset
                                .TryParse(row["utc"]?.ToString(), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None,
                                    out var utc)
                                ? utc : new DateTimeOffset(file.LastWriteTimeUtc);
                            if (requestId != null && !string.Equals((string?)row["id"], requestId, StringComparison.Ordinal)) continue;
                            if (diagnosticId != null && !string.Equals((string?)row["id"], diagnosticId, StringComparison.Ordinal)) continue;
                            if (excludeRequestId != null && string.Equals((string?)row["id"], excludeRequestId, StringComparison.Ordinal)) continue;
                            if (sinceUtc.HasValue && (!DateTimeOffset.TryParse((string?)row["utc"],
                                System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var recorded)
                                || recorded < sinceUtc.Value)) continue;
                            matched++;
                            rows.Add((time, order++, row));
                            // Files can contain interleaved and out-of-order timestamps. Count all matches,
                            // retaining only the newest offset + take rows needed for this page.
                            if (rows.Count > offset + take) rows.Remove(rows.Min);
                        }
                        catch (JsonException) /* swallow(parse-fallback): count partial or malformed journal rows and preserve the remaining crash evidence */ { malformed++; }
                    }
            var page = rows.Take(Math.Max(0, rows.Count - offset)).ToArray();
            return new JsonObject { ["success"] = true, ["directory"] = root, ["offset"] = offset,
                ["nextOffset"] = offset + page.Length < matched ? offset + page.Length : (int?)null,
                ["records"] = new JsonArray(page.Select(r => r.Row).ToArray()), ["malformedLines"] = malformed, ["matchedRecords"] = matched, ["filesRead"] =
                    files.Length };
        }

        public static JsonObject Read(string root, int take, string? requestId, DateTimeOffset? sinceUtc)
            => Read(root, take, requestId, sinceUtc, null);

    }
}
