using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TiaOpenness.Shared
{
    internal sealed class AuditRecord
    {
        internal long Index { get; set; }
        internal string Utc { get; set; } = "";
        internal string Event { get; set; } = "";
        internal string RequestId { get; set; } = "";
        internal string Host { get; set; } = "";
        internal string Release { get; set; } = "";
        internal string Tool { get; set; } = "";
        internal string? Outcome { get; set; }
        internal string? PlanHash { get; set; }
        internal bool? ApprovalEnabled { get; set; }
        internal int ProcessId { get; set; }
        internal string PreviousHash { get; set; } = "";
    }

    internal sealed class AuditVerificationReport
    {
        public bool Passed { get; set; }
        public int Count { get; set; }
        public long? BreakIndex { get; set; }
        public string? File { get; set; }
        public string? Reason { get; set; }
    }

    // An independent, append-only chain. Journal retention never removes audit files.
    internal sealed class AuditLog
    {
        internal const string Genesis = "0000000000000000000000000000000000000000000000000000000000000000";
        private static readonly string[] Events = { "request", "start", "end", "approval-granted", "approval-denied", "approval-timeout", "approval-switch" };
        private readonly string directory;
        private readonly long maxBytes;
        internal static AuditLog Current => new AuditLog(DataLocations.Current.AuditDirectory);
        internal AuditLog(string directory, long maxBytes = 10 * 1024 * 1024)
        {
            this.directory = Path.GetFullPath(directory);
            if (maxBytes < 1) throw new ArgumentOutOfRangeException(nameof(maxBytes));
            this.maxBytes = maxBytes;
        }

        internal AuditRecord Append(string kind, string requestId, string host, string release, string tool,
            string? outcome = null, string? planHash = null, bool? approvalEnabled = null)
        {
            if (!Events.Contains(kind)) throw new ArgumentException("Unknown audit event.", nameof(kind));
            using (JournalFileLock.Acquire(Path.Combine(directory, ".audit.lock")))
            {
                var files = Files();
                string? path = files.LastOrDefault();
                AuditRecord? previous = null;
                if (path != null)
                {
                    using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        if (stream.Length == 0) throw new InvalidDataException("The current audit file is empty.");
                        stream.Position = stream.Length - 1;
                        if (stream.ReadByte() != '\n') throw new InvalidDataException("The audit tail is interrupted; verify before repair.");
                        previous = Parse(JournalRetention.LastCompleteLine(stream));
                    }
                }
                var row = new AuditRecord { Index = previous == null ? 1 : checked(previous.Index + 1),
                    Utc = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture), Event = kind,
                    RequestId = requestId, Host = host, Release = release, Tool = tool, Outcome = outcome,
                    PlanHash = planHash, ApprovalEnabled = approvalEnabled, ProcessId = Process.GetCurrentProcess().Id,
                    PreviousHash = previous == null ? Genesis : Hash(previous) };
                byte[] bytes = Encoding.UTF8.GetBytes(Canonical(row) + "\n");
                if (path == null || new FileInfo(path).Length + bytes.Length > maxBytes)
                    path = Path.Combine(directory, "audit-" + row.Index.ToString("D20", CultureInfo.InvariantCulture) + ".jsonl");
                using (var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read))
                { stream.Write(bytes, 0, bytes.Length); stream.Flush(true); }
                return row;
            }
        }

        // Public-to-the-host API for P6-44; no approval decision or channel is implemented here.
        internal void Approval(string requestId, string host, string release, string tool, string decision, string? planHash = null)
        {
            if (decision != "granted" && decision != "denied" && decision != "timeout") throw new ArgumentException("Invalid approval decision.", nameof(decision));
            Append("approval-" + decision, requestId, host, release, tool, planHash: planHash);
        }
        internal void ApprovalSwitch(string host, bool enabled) => Append("approval-switch", "", host, "", "", approvalEnabled: enabled);

        internal AuditVerificationReport Verify()
        {
            var report = new AuditVerificationReport { Passed = true };
            try
            {
                using (JournalFileLock.Acquire(Path.Combine(directory, ".audit.lock")))
                {
                    string previous = Genesis;
                    foreach (string file in Files())
                    {
                        bool any = false;
                        using (var reader = new StreamReader(file, new UTF8Encoding(false, true)))
                        {
                            string? line;
                            while ((line = reader.ReadLine()) != null)
                            {
                                long index = (long)report.Count + 1;
                                AuditRecord row;
                                try { row = Parse(line); }
                                catch (Exception ex) when (ex is JsonException || ex is FormatException || ex is InvalidDataException || ex is OverflowException)
                                { return Broken(report, index, file, "invalid-record"); }
                                if (row.Index != index) return Broken(report, index, file, "index");
                                if (row.PreviousHash != previous) return Broken(report, index, file, "previous-hash");
                                previous = Hash(row); report.Count++; any = true;
                            }
                        }
                        if (!any) return Broken(report, (long)report.Count + 1, file, "empty-file");
                    }
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is DecoderFallbackException)
            { return Broken(report, (long)report.Count + 1, null, ex.GetType().Name); }
            return report;
        }

        internal IReadOnlyList<AuditRecord> Read()
        {
            using (JournalFileLock.Acquire(Path.Combine(directory, ".audit.lock")))
            {
                var rows = new List<AuditRecord>();
                foreach (string file in Files())
                    foreach (string line in File.ReadLines(file))
                    {
                        try { rows.Add(Parse(line)); }
                        catch (Exception ex) when (ex is JsonException || ex is FormatException || ex is InvalidDataException || ex is OverflowException)
                        {
                            // Keep an addressable row so the Workbench can jump to a malformed record.
                            rows.Add(new AuditRecord { Index = rows.Count + 1L, Event = "end", Outcome = "invalid-record", Utc = DateTimeOffset.MinValue.ToString("O") });
                        }
                    }
                return rows;
            }
        }

        private string[] Files() => Directory.GetFiles(directory, "audit-*.jsonl").OrderBy(p => p, StringComparer.Ordinal).ToArray();
        private static AuditVerificationReport Broken(AuditVerificationReport report, long index, string? file, string reason)
        { report.Passed = false; report.BreakIndex = index; report.File = file == null ? null : Path.GetFileName(file); report.Reason = reason; return report; }

        internal static string Hash(AuditRecord row)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(Canonical(row)))).Replace("-", "").ToLowerInvariant();
        }
        internal static string Canonical(AuditRecord row)
        {
            using (var stream = new MemoryStream())
            {
                using (var writer = new Utf8JsonWriter(stream))
                {
                    writer.WriteStartObject();
                    if (row.ApprovalEnabled.HasValue) writer.WriteBoolean("approvalEnabled", row.ApprovalEnabled.Value); else writer.WriteNull("approvalEnabled");
                    writer.WriteString("event", row.Event); writer.WriteString("host", row.Host); writer.WriteNumber("index", row.Index);
                    writer.WriteString("outcome", row.Outcome); writer.WriteString("planHash", row.PlanHash); writer.WriteString("previousHash", row.PreviousHash);
                    writer.WriteNumber("processId", row.ProcessId); writer.WriteString("release", row.Release); writer.WriteString("requestId", row.RequestId);
                    writer.WriteString("tool", row.Tool); writer.WriteString("utc", row.Utc);
                    writer.WriteEndObject(); writer.Flush();
                }
                return Encoding.UTF8.GetString(stream.ToArray());
            }
        }
        internal static AuditRecord Parse(string line)
        {
            using (var document = JsonDocument.Parse(line))
            {
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 12 ||
                    root.EnumerateObject().Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != 12)
                    throw new InvalidDataException("Invalid audit record fields.");
                string Required(string key) => root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
                    ? value.GetString()! : throw new InvalidDataException("Invalid audit string.");
                string? Optional(string key) => root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Null ? null : Required(key);
                if (!root.TryGetProperty("index", out var index) || index.ValueKind != JsonValueKind.Number || !index.TryGetInt64(out long number) || number < 1 ||
                    !root.TryGetProperty("processId", out var process) || process.ValueKind != JsonValueKind.Number || !process.TryGetInt32(out int pid) || pid < 1 ||
                    !root.TryGetProperty("approvalEnabled", out var enabled) || !(enabled.ValueKind == JsonValueKind.Null || enabled.ValueKind == JsonValueKind.True || enabled.ValueKind == JsonValueKind.False))
                    throw new InvalidDataException("Invalid audit number or switch.");
                string utc = Required("utc"), kind = Required("event"), hash = Required("previousHash");
                if (!DateTimeOffset.TryParseExact(utc, "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out _) || !Events.Contains(kind)
                    || hash.Length != 64 || hash.Any(c => !(c >= '0' && c <= '9' || c >= 'a' && c <= 'f')))
                    throw new InvalidDataException("Invalid audit metadata.");
                return new AuditRecord { Index = number, Utc = utc, Event = kind, RequestId = Required("requestId"), Host = Required("host"),
                    Release = Required("release"), Tool = Required("tool"), Outcome = Optional("outcome"), PlanHash = Optional("planHash"),
                    ApprovalEnabled = enabled.ValueKind == JsonValueKind.Null ? (bool?)null : enabled.GetBoolean(), ProcessId = pid, PreviousHash = hash };
            }
        }
    }
}
