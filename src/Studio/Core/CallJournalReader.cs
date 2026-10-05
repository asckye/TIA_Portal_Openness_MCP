using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using TiaMcp.Logic.V4;
using TiaOpenness.Shared;

namespace TiaOpenness.Core
{
    public sealed class JournalCall
    {
        public string Identity { get; internal set; }
        public string RequestId { get; internal set; }
        public DateTimeOffset Time { get; internal set; }
        public string Host { get; internal set; }
        public string Release { get; internal set; }
        public string Tool { get; internal set; }
        public bool IsWrite { get; internal set; }
        public string Outcome { get; internal set; }
        public string Execution { get; internal set; }
        public string Completeness { get; internal set; }
        public string DisplayOutcome { get; internal set; }
        public int? DurationMs { get; internal set; }
        public string Target { get; internal set; }
        public string Arguments { get; internal set; }
        public string Result { get; internal set; }
        public string ErrorCode { get; internal set; }
        public bool ArgumentsTruncated { get; internal set; }
        public bool ResultTruncated { get; internal set; }
        internal DateTimeOffset Updated;
    }

    /// <summary>Reads the existing host JSONL files; never creates files or owns a native session.</summary>
    public sealed class CallJournalReader : IDisposable
    {
        public const int PollMilliseconds = 1000;
        public const int MaximumCalls = 5000;
        private const int MaximumLineBytes = 128 * 1024;
        private const int BytesPerPoll = 4 * 1024 * 1024;
        private readonly object sync = new object();
        private readonly string directory;
        private readonly Dictionary<string, FileCursor> files = new Dictionary<string, FileCursor>(StringComparer.OrdinalIgnoreCase);
        private readonly Timer timer;
        private JournalCall[] calls = Array.Empty<JournalCall>();
        private bool disposed;
        public IReadOnlyList<JournalCall> Calls => Volatile.Read(ref calls);
        public event EventHandler Changed;

        public CallJournalReader(string directory = null, bool live = true)
        {
            this.directory = directory ?? DataLocations.Current.DiagnosticsDirectory;
            if (!Path.IsPathRooted(this.directory)) throw new ArgumentException("Journal directory must be absolute.", nameof(directory));
            Poll();
            if (live) timer = new Timer(_ => Poll(), null, PollMilliseconds, PollMilliseconds);
        }

        public void Poll()
        {
            lock (sync)
            {
                if (disposed) return;
                string[] paths;
                try { paths = Directory.GetFiles(directory, "calls-*.jsonl*"); }
                catch (IOException) /* swallow(probe-optional): absent or rotating directories are retried at the next bounded poll */ { paths = Array.Empty<string>(); }
                catch (UnauthorizedAccessException) /* swallow(probe-optional): inaccessible journals cannot be consumed */ { paths = Array.Empty<string>(); }
                foreach (string missing in files.Keys.Except(paths, StringComparer.OrdinalIgnoreCase).ToArray()) files.Remove(missing);
                foreach (string path in paths.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
                {
                    try
                    {
                        var info = new FileInfo(path);
                        if (!files.TryGetValue(path, out var cursor) || info.Length < cursor.Position || info.CreationTimeUtc != cursor.Created
                            || info.Length == cursor.Position && info.LastWriteTimeUtc != cursor.Modified)
                            files[path] = cursor = new FileCursor { Created = info.CreationTimeUtc };
                        Read(path, cursor);
                        cursor.Modified = info.LastWriteTimeUtc;
                    }
                    catch (IOException) /* swallow(probe-optional): deletion, replacement and sharing conflicts are retried on the next poll */ { files.Remove(path); }
                    catch (UnauthorizedAccessException) /* swallow(probe-optional): a file can become inaccessible during enumeration */ { files.Remove(path); }
                }
                var next = files.Values.SelectMany(f => f.Rows.Values).GroupBy(c => c.Identity, StringComparer.Ordinal)
                    .Select(g => g.OrderByDescending(c => c.Updated).ThenBy(c => c.DisplayOutcome == "executing" ? 1 : 0).First())
                    .OrderByDescending(c => c.Time).ThenBy(c => c.Identity, StringComparer.Ordinal).Take(MaximumCalls).ToArray();
                if (next.Length == calls.Length && next.Zip(calls, (a, b) => ReferenceEquals(a, b)).All(equal => equal)) return;
                Volatile.Write(ref calls, next);
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }

        private static void Read(string path, FileCursor cursor)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            stream.Position = cursor.Position;
            var buffer = new byte[8192];
            int remaining = BytesPerPoll;
            while (remaining > 0)
            {
                int count = stream.Read(buffer, 0, Math.Min(buffer.Length, remaining));
                if (count == 0) break;
                remaining -= count; cursor.Position += count;
                for (int i = 0; i < count; i++)
                {
                    byte value = buffer[i];
                    if (value == 10)
                    {
                        if (!cursor.Oversized && TryRead(Encoding.UTF8.GetString(cursor.Pending.ToArray()), out var row)) cursor.Rows[row.Identity] = row;
                        cursor.Pending.Clear(); cursor.Oversized = false;
                    }
                    else if (!cursor.Oversized)
                    {
                        if (cursor.Pending.Count < MaximumLineBytes) cursor.Pending.Add(value);
                        else { cursor.Pending.Clear(); cursor.Oversized = true; }
                    }
                }
            }
            if (cursor.Rows.Count > MaximumCalls)
                foreach (var row in cursor.Rows.Values.OrderByDescending(r => r.Time).Skip(MaximumCalls).ToArray()) cursor.Rows.Remove(row.Identity);
        }

        private static bool TryRead(string line, out JournalCall row)
        {
            row = null;
            try
            {
                using var document = JsonDocument.Parse(line);
                var value = document.RootElement;
                if (!value.TryGetProperty("callProjection", out var projection) || projection.GetInt32() != 1) return false;
                string Text(string key) => value.TryGetProperty(key, out var field) && field.ValueKind == JsonValueKind.String ? field.GetString() : "";
                bool Flag(string key) => value.TryGetProperty(key, out var field) && field.ValueKind == JsonValueKind.True;
                string outcome = Text("outcome"), execution = Text("execution"), completeness = Text("completeness");
                string display = Text("phase") == "BEFORE" ? "executing" : "unknown";
                if (outcome.Length > 0 && execution.Length > 0 && completeness.Length > 0)
                    display = EngineToolResult.Classify(Flag("ok"), V4Json.Deserialize<Outcome>(JsonSerializer.Serialize(outcome)),
                        V4Json.Deserialize<Execution>(JsonSerializer.Serialize(execution)), V4Json.Deserialize<Completeness>(JsonSerializer.Serialize(completeness)));
                string arguments = Text("arguments"), result = Text("result");
                bool argumentsCut = Flag("argumentsTruncated"), resultCut = Flag("resultTruncated");
                // A truncated JSON prefix cannot safely be parsed again. Only producer-marked previews are retained.
                string Preview(string text, bool cut) => cut ? CallJournalPayload.Bound(text) : CallJournalPayload.Redact(text);
                row = new JournalCall
                {
                    Identity = value.GetProperty("mcpProcessId").GetInt32().ToString(CultureInfo.InvariantCulture) + ":" + Text("id") + ":" + Text("tool"),
                    RequestId = Text("requestId"), Time = DateTimeOffset.Parse(Text("startedUtc"), CultureInfo.InvariantCulture),
                    Updated = DateTimeOffset.Parse(Text("utc"), CultureInfo.InvariantCulture), Host = Text("host"), Release = Text("releaseKey"), Tool = Text("tool"),
                    IsWrite = Flag("isWrite"), Outcome = outcome, Execution = execution, Completeness = completeness, DisplayOutcome = display,
                    DurationMs = value.TryGetProperty("durationMs", out var duration) && duration.ValueKind == JsonValueKind.Number && duration.TryGetInt32(out int milliseconds) ? milliseconds : (int?)null,
                    Target = CallJournalPayload.Bound(Text("target")), Arguments = Preview(arguments, argumentsCut), Result = Preview(result, resultCut),
                    ArgumentsTruncated = argumentsCut, ResultTruncated = resultCut, ErrorCode = Text("errorCode")
                };
                return row.RequestId.Length > 0 && row.Tool.Length > 0;
            }
            catch (Exception ex) when (ex is JsonException || ex is ArgumentException || ex is InvalidOperationException || ex is FormatException || ex is OverflowException)
            { return false; }
        }

        public void Dispose() { lock (sync) { disposed = true; timer?.Dispose(); } }

        private sealed class FileCursor
        {
            internal long Position;
            internal DateTime Created, Modified;
            internal bool Oversized;
            internal readonly List<byte> Pending = new List<byte>();
            internal readonly Dictionary<string, JournalCall> Rows = new Dictionary<string, JournalCall>(StringComparer.Ordinal);
        }
    }
}
