using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;

namespace TiaMcpServer.Siemens
{
    // A cursor owns the lazy iterator, not a background worker. No Openness read
    // survives a request timeout in a second competing reader. Budgets are checked
    // between records; an individual synchronous Openness call cannot be cancelled.
    internal sealed class MigrationPages : IDisposable
    {
        private sealed class Session
        {
            internal object? Project;
            internal string Scope = "", Id = "";
            internal IEnumerator<JsonObject>? Iterator;
            internal DateTime Used;
            internal long Access;
            internal WeakReference? ProjectIdentity;
            internal int Sequence, Total, Failures, ReadFailures, ClassificationFailures;
            internal int? Expected;
            internal JsonObject? Last;
        }
        private readonly Dictionary<string, Session> sessions = new Dictionary<string, Session>();
        private readonly object gate = new object();
        private readonly Func<DateTime> now;
        private readonly Timer timer;
        private bool disposed;
        private long access;
        private const int ActiveCapacity = 16, ReplayCapacity = 32;
        private static readonly TimeSpan ActiveLifetime = TimeSpan.FromMinutes(30), ReplayLifetime = TimeSpan.FromMinutes(10);
        internal MigrationPages(Func<DateTime>? clock = null)
        {
            now = clock ?? (() => DateTime.UtcNow);
            timer = new Timer(_ => { lock (gate) { Expire(); } }, null, 60000, 60000);
        }
        internal JsonObject Read(object project, string scope, string cursor, int pageSize, int budgetMs,
            Func<IEnumerable<JsonObject>> factory, int? expected = null)
        {
            if (pageSize < 1 || pageSize > 500 || budgetMs < 50 || budgetMs > 20000)
                throw new ArgumentException("pageSize must be 1..500; budgetMs 50..20000.");
            lock (gate)
            {
                if (disposed) throw new ObjectDisposedException(nameof(MigrationPages));
                Expire();
                foreach (var id in sessions.Where(p => !ReferenceEquals(p.Value.ProjectIdentity?.Target, project)).Select(p => p.Key).ToArray())
                { Release(sessions[id]); sessions.Remove(id); }
                Session s;
                if (string.IsNullOrEmpty(cursor))
                {
                    if (sessions.Values.Count(p => p.Iterator != null) >= ActiveCapacity)
                        throw new InvalidOperationException("CursorCapacity: 16 collections are still active. Complete or release an unfinished collection; completed replay caches do not consume active capacity.");
                    s = new Session { Project = project, ProjectIdentity = new WeakReference(project), Scope = scope, Id = Guid.NewGuid().ToString("N"), Used = now(), Expected = expected };
                    // The factory must be lazy; no traversal before the first page.
                    s.Iterator = factory().GetEnumerator(); sessions.Add(s.Id, s);
                }
                else
                {
                    var parts = cursor.Split(':');
                    if (parts.Length != 2 || !int.TryParse(parts[1], out int sequence) || !sessions.TryGetValue(parts[0], out s!))
                        throw new InvalidOperationException("CursorExpiredOrUnknown: start a new collection; do not append it to the old collection.");
                    if (s.Scope != scope || (s.Project != null && !ReferenceEquals(s.Project, project)))
                        throw new InvalidOperationException("CursorScopeMismatch: use the same tool, project and arguments.");
                    s.Used = now();
                    s.Access = ++access;
                    if (sequence == s.Sequence - 1 && s.Last != null) return (JsonObject)s.Last.DeepClone();
                    if (sequence != s.Sequence || s.Iterator == null)
                        throw new InvalidOperationException("CursorConsumedOrInvalid: only the most recently requested page can be replayed.");
                }
                var inputCursor = s.Id + ":" + s.Sequence;
                var rows = new JsonArray(); var failures = new JsonArray(); var watch = Stopwatch.StartNew(); int chars = 0;
                bool finished = false, aborted = false;
                while (rows.Count < pageSize && chars < 500000 && (rows.Count == 0 || watch.ElapsedMilliseconds < budgetMs))
                {
                    JsonObject? row = null;
                    try { if (s.Iterator!.MoveNext()) row = s.Iterator.Current; else finished = true; }
                    catch (Exception ex) { row = MigrationRead.Failure(scope, "TraversalFailed", ex); finished = true; aborted = true; }
                    if (row != null)
                    {
                        rows.Add(row); s.Total++; chars += row.ToJsonString().Length;
                        if (row["status"]?.ToString() == "failed" || row["status"]?.ToString() == "unsupported")
                        {
                            s.Failures++; failures.Add(row.DeepClone());
                            if (row["kind"]?.ToString() == "tagSource") s.ClassificationFailures++;
                            else s.ReadFailures++;
                        }
                    }
                    if (finished) break;
                }
                if (finished) { Release(s); }
                var result = new JsonObject {
                    ["schemaVersion"] = 1, ["scope"] = JsonNode.Parse(scope), ["readOnly"] = true,
                    ["collectionId"] = s.Id, ["pageIndex"] = s.Sequence, ["pageCursor"] = inputCursor,
                    ["apiCallSuccess"] = !aborted, ["dataComplete"] = finished && s.Failures == 0,
                    ["readComplete"] = finished && !aborted && s.ReadFailures == 0,
                    ["classificationComplete"] = finished && !aborted && s.ClassificationFailures == 0,
                    ["readFailureCount"] = s.ReadFailures, ["classificationFailureCount"] = s.ClassificationFailures,
                    ["traversalComplete"] = finished && !aborted, ["truncated"] = !finished || aborted,
                    ["expectedCount"] = s.Expected.HasValue ? JsonValue.Create(s.Expected.Value) : null,
                    ["countUnit"] = "evidence records (not tags or screens)", ["expectedCountReason"] = s.Expected.HasValue ? "known before traversal" : "unknown until traversal completes; collection records carry local counts",
                    ["actualCount"] = rows.Count, ["cumulativeCount"] = s.Total,
                    ["failureCount"] = s.Failures, ["failures"] = failures,
                    ["nextCursor"] = finished ? null : s.Id + ":" + (s.Sequence + 1),
                    ["elapsedMs"] = watch.ElapsedMilliseconds,
                    ["consistency"] = "live read; not an atomic snapshot; do not edit the project during collection",
                    ["budgetNote"] = "Time budget is checked between reads; a synchronous Openness call cannot be interrupted.",
                    ["releaseCursor"] = s.Id,
                    ["cursorPolicy"] = "ReleaseUnifiedReadCursor(releaseCursor) after persisting all pages. Active: 16 collections / 30 min idle. Completed: latest page only, 32 collections LRU / 10 min idle; may be evicted earlier for capacity. Expired cursors never silently restart.",
                    ["records"] = rows
                };
                s.Sequence++; s.Last = (JsonObject)result.DeepClone(); s.Used = now(); s.Access = ++access;
                foreach (var id in sessions.Values.Where(p => p.Iterator == null).OrderByDescending(p => p.Access).Skip(ReplayCapacity).Select(p => p.Id).ToArray())
                    sessions.Remove(id);
                return result;
            }
        }
        private void Expire()
        {
            foreach (var id in sessions.Where(p => now() - p.Value.Used > (p.Value.Iterator == null ? ReplayLifetime : ActiveLifetime)).Select(p => p.Key).ToArray())
            { Release(sessions[id]); sessions.Remove(id); }
        }
        internal bool Cancel(string cursor)
        {
            lock (gate) { var id = cursor.Split(':')[0]; if (!sessions.TryGetValue(id, out var s)) return false; Release(s); sessions.Remove(id); return true; }
        }
        private static void Release(Session s) { try { s.Iterator?.Dispose(); } finally { s.Iterator = null; s.Project = null; } }
        public void Dispose() { timer.Dispose(); lock (gate) { disposed = true; foreach (var s in sessions.Values) Release(s); sessions.Clear(); } }
    }
}
