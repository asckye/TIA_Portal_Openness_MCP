using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using TiaOpenness.Shared;

namespace TiaOpenness.Gui.Services;

// The display is not verification. Only complete new lines are parsed and hashed, and only
// a recent page is retained. Each read lease covers bytes, never JSON parsing or hashing.
internal sealed class AuditTailReader(string directory)
{
    private sealed class Cursor
    {
        internal long Position;
        internal DateTime Created, Modified;
        internal readonly List<byte> Pending = [];
        internal bool Oversized;
    }
    private readonly Dictionary<string, Cursor> _files = new(StringComparer.Ordinal);
    private readonly Queue<(long Index, string Line)> _recent = new();
    private readonly Dictionary<long, AuditEvent> _projected = new();
    internal int TotalCount { get; private set; }
    internal int Generation { get; private set; }
    internal long BytesRead { get; private set; }
    internal int ReadThreadId { get; private set; }
    private string[] Paths() => Directory.Exists(directory) ? Directory.GetFiles(directory, "audit-*.jsonl").OrderBy(p => p, StringComparer.Ordinal).ToArray() : [];
    internal AuditEvent[] Poll()
    {
        ReadThreadId = Environment.CurrentManagedThreadId;
        var paths = Paths();
        if (_files.Keys.Except(paths).Any() || paths.Any(path => _files.TryGetValue(path, out var cursor)
            && (new FileInfo(path).Length < cursor.Position || new FileInfo(path).CreationTimeUtc != cursor.Created
                || new FileInfo(path).Length == cursor.Position && new FileInfo(path).LastWriteTimeUtc != cursor.Modified))
            || paths.Any(path => !_files.ContainsKey(path) && _files.Keys.Any(old => StringComparer.Ordinal.Compare(path, old) < 0)))
        { _files.Clear(); _recent.Clear(); _projected.Clear(); TotalCount = 0; Generation++; }
        foreach (string path in paths)
        {
            var info = new FileInfo(path);
            if (!_files.TryGetValue(path, out var cursor)) _files[path] = cursor = new Cursor { Created = info.CreationTimeUtc };
            if (info.Length == cursor.Position) continue;
            Read(path, cursor, line =>
            {
                TotalCount++;
                _recent.Enqueue((TotalCount, line));
                while (_recent.Count > AuditLogService.PageSize) _recent.Dequeue();
            });
            cursor.Modified = info.LastWriteTimeUtc;
        }
        var keep = _recent.Select(row => row.Index).ToHashSet();
        foreach (long index in _projected.Keys.Where(index => !keep.Contains(index)).ToArray()) _projected.Remove(index);
        return _recent.Select(row =>
        {
            if (!_projected.TryGetValue(row.Index, out var value)) _projected[row.Index] = value = Project(row.Line, row.Index);
            return value;
        }).ToArray();
    }
    internal AuditEvent[] Page(long end)
    {
        var rows = new List<AuditEvent>();
        long index = 0, start = Math.Max(1, end - AuditLogService.PageSize + 1);
        foreach (string path in Paths())
        {
            var cursor = new Cursor();
            Read(path, cursor, line => { index++; if (index >= start && index <= end) rows.Add(Project(line, index)); }, () => index >= end);
            if (index >= end) break;
        }
        return rows.ToArray();
    }
    private void Read(string path, Cursor cursor, Action<string> line, Func<bool>? finished = null)
    {
        var buffer = new byte[256 * 1024];
        while (finished?.Invoke() != true)
        {
            int count;
            using (JournalFileLock.Acquire(Path.Combine(directory, ".audit.lock"), readOnly: true))
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            { stream.Position = cursor.Position; count = stream.Read(buffer, 0, buffer.Length); }
            if (count == 0) break;
            BytesRead += count; cursor.Position += count;
            for (int i = 0; i < count; i++)
            {
                if (buffer[i] == 10)
                {
                    line(cursor.Oversized ? "" : Encoding.UTF8.GetString(cursor.Pending.ToArray()));
                    cursor.Pending.Clear(); cursor.Oversized = false;
                    if (finished?.Invoke() == true) return;
                }
                else if (!cursor.Oversized)
                {
                    if (cursor.Pending.Count < 128 * 1024) cursor.Pending.Add(buffer[i]);
                    else { cursor.Pending.Clear(); cursor.Oversized = true; }
                }
            }
        }
    }
    private static AuditEvent Project(string line, long index)
    {
        try
        {
            var row = AuditLog.Parse(line);
            return new AuditEvent(index, DateTimeOffset.Parse(row.Utc, CultureInfo.InvariantCulture), row.Event switch
            {
                "request" => AuditEventType.Request, "start" => AuditEventType.Start, "end" => AuditEventType.End,
                "approval-granted" => AuditEventType.Approve, "approval-denied" => AuditEventType.Reject,
                "approval-timeout" => AuditEventType.Timeout, _ => AuditEventType.Toggle,
            }, row.Tool, row.Outcome ?? (row.ApprovalEnabled.HasValue ? (row.ApprovalEnabled.Value ? "enabled" : "disabled") : ""), AuditLog.Hash(row)[..8])
            { Actor = row.Actor };
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidDataException or OverflowException)
        { return new AuditEvent(index, DateTimeOffset.MinValue, AuditEventType.End, "", "invalid-record", ""); }
    }
}
