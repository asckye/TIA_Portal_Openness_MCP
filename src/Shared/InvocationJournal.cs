using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace TiaMcpServer.ModelContextProtocol
{
    // No native getters: logging cannot re-enter Openness. Call payloads use the separate privacy projection.
    internal static partial class InvocationJournal
    {
        private static readonly object Sync = new object();
        private static readonly int ProcessId = Process.GetCurrentProcess().Id;
        private static readonly string ProcessKey = TiaOpenness.Shared.DataLocations.ProcessKey;
        private static readonly AsyncLocal<string?> Current = new AsyncLocal<string?>();
        private static readonly AsyncLocal<NativeCallScope?> ActiveNativeCallScope = new AsyncLocal<NativeCallScope?>();
        private static IJournalSink sink = new FileJournalSink();
        private static Func<string>? correlationSource;
        private static long failedWrites;
        private static string? lastWriteFailure;
        internal static string CorrelationId => Current.Value ?? correlationSource?.Invoke() ?? (Current.Value = Guid.NewGuid().ToString("N"));
        internal static bool NativeCallIssued => ActiveNativeCallScope.Value?.NativeCallIssued == true;

        internal static IDisposable UseCorrelation(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("A correlation id is required.", nameof(id));
            string? previous = Current.Value;
            Current.Value = id;
            return new CorrelationScope(previous);
        }
        private sealed class CorrelationScope : IDisposable
        {
            private readonly string? previous;
            private bool disposed;
            internal CorrelationScope(string? previous) { this.previous = previous; }
            public void Dispose() { if (!disposed) { disposed = true; Current.Value = previous; } }
        }

        internal sealed class NativeCallScope : IDisposable
        {
            internal readonly NativeCallScope? Parent;
            private int calls;
            internal bool NativeCallIssued => Volatile.Read(ref calls) != 0;
            internal NativeCallScope(NativeCallScope? parent) { Parent = parent; }
            internal void Record() => Interlocked.Increment(ref calls);
            public void Dispose()
            {
                if (ReferenceEquals(ActiveNativeCallScope.Value, this)) ActiveNativeCallScope.Value = Parent;
            }
        }

        internal static NativeCallScope BeginNativeCallScope()
        {
            var scope = new NativeCallScope(ActiveNativeCallScope.Value);
            ActiveNativeCallScope.Value = scope;
            return scope;
        }

        internal static void NativeCallStarted()
        {
            for (var scope = ActiveNativeCallScope.Value; scope != null; scope = scope.Parent) scope.Record();
        }

        // The callback must synchronously flush before returning. Delegates can cross the
        // separately compiled adapter assemblies; step H will supply the engine's sink and id.
        internal static void ConfigureOutput(Action<string>? write, Func<string>? correlation = null)
        {
            lock (Sync) { (sink as IDisposable)?.Dispose(); sink = write == null ? (IJournalSink)new FileJournalSink() : new CallbackJournalSink(write); correlationSource = correlation; }
        }

        internal interface IJournalSink { void Write(Func<string> row, bool flush); }
        private sealed class CallbackJournalSink : IJournalSink
        {
            private readonly Action<string> write;
            internal CallbackJournalSink(Action<string> write) { this.write = write; }
            public void Write(Func<string> row, bool flush) => write(row());
        }
        private sealed class FileJournalSink : IJournalSink, IDisposable
        {
            private TiaOpenness.Shared.JournalRetention retention = new TiaOpenness.Shared.JournalRetention();
            private long settingsRead;
            private string? currentPath;
            public void Write(Func<string> row, bool flush)
            {
                var root = TiaOpenness.Shared.DataLocations.Current.DiagnosticsDirectory;
                if (!Path.IsPathRooted(root)) throw new IOException("Diagnostic path must be absolute: " + root);
                Directory.CreateDirectory(root);
                string path = Path.Combine(root, "calls-" + ProcessKey + ".jsonl");
                string text = row();
                RequireDiskFlush(text, ref flush);
                if (text.IndexOf("\"tool\":\"native:", StringComparison.Ordinal) >= 0 || text.IndexOf("\"nativeCallId\":", StringComparison.Ordinal) >= 0) flush = true;
                long now = Stopwatch.GetTimestamp();
                bool checkRetention = settingsRead == 0 || now - settingsRead >= Stopwatch.Frequency;
                if (checkRetention)
                { retention = TiaOpenness.Shared.JournalRetention.Load(TiaOpenness.Shared.JournalRetention.SettingsPath); settingsRead = now; }
                byte[] bytes = Encoding.UTF8.GetBytes(text + Environment.NewLine);
                // The size check reads the open handle, so a file changed outside this writer still rotates on time.
                bool rotate = currentPath != path || checkRetention || !File.Exists(path);
                FileStream? stream = rotate ? null : new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
                if (stream != null && stream.Length + bytes.Length > (long)retention.FileSizeMb * 1024 * 1024) { stream.Dispose(); stream = null; rotate = true; }
                if (rotate)
                {
                    retention.Rotate(path, bytes.Length);
                    currentPath = path;
                }
                // Projection BEFORE, native boundaries and completion force the disk.
                // Begin's introductory row is coalesced with the projection BEFORE.
                // Keep reader/recovery compatibility: close the handle after every row.
                // Retention copies need scanning only at refresh/rotation boundaries.
                using (stream ??= new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read))
                { stream.Write(bytes, 0, bytes.Length); if (flush) stream.Flush(true); else stream.Flush(); }
            }
            public void Dispose() { currentPath = null; }
        }
        static partial void RequireDiskFlush(string row, ref bool flush);

        internal static JsonLineObject HealthRow()
        {
            lock (Sync) return new JsonLineObject().Number("failedWrites", failedWrites).String("lastFailure", lastWriteFailure)
                .String("durability", "best-effort; successful writes flushed to disk")
                .String("nativeBoundaryCoverage", typeof(InvocationJournal).Assembly.GetType("TiaMcpServer.Diagnostics.GeneratedNativeCalls", false) != null ? "build-instrumented" : "not-instrumented");
        }
        internal static string NewId(string? correlation = null)
            => Guid.TryParseExact(correlation ?? correlationSource?.Invoke(), "N", out var parsed) ? parsed.ToString("N") : Guid.NewGuid().ToString("N");
        internal static string Begin(string name, string? correlation = null)
        {
            string id = NewId(correlation);
            Current.Value = id;
            Emit(() => FormatRow(DateTime.UtcNow, id, name, "BEFORE", ProcessId, null, null,
                ReadBinding(), Thread.CurrentThread.ManagedThreadId, Thread.CurrentThread.GetApartmentState().ToString(), null), flush: false);
            return id;
        }
        internal static T Native<T>(string stage, Func<T> call, string? objectType = null, string? objectPath = null)
        {
            string id = correlationSource?.Invoke() ?? Current.Value ?? Guid.NewGuid().ToString("N");
            NativeCallStarted();
            WriteRow(id, "native:" + stage, "BEFORE", objectType, objectPath);
            try { T result = call(); WriteRow(id, "native:" + stage, "RETURNED", objectType, objectPath); return result; }
            catch (Exception ex) { _ = PortalFailureClassifier.IsPortalProcessLost(ex); WriteRow(id, "native:" + stage, "THREW", objectType, objectPath); throw; }
        }
        internal static void Native(string stage, Action call, string? objectType = null, string? objectPath = null) => Native(stage, () => { call(); return true; }, objectType, objectPath);
        internal static void WriteRow(string id, string name, string phase, string? objectType = null, string? objectPath = null, Func<JsonLineObject?>? details = null)
            => Emit(() => FormatRow(DateTime.UtcNow, id, name, phase, ProcessId, objectType, objectPath,
                ReadBinding(), Thread.CurrentThread.ManagedThreadId, Thread.CurrentThread.GetApartmentState().ToString(), details?.Invoke()));
        internal static void WriteLine(string row) => Emit(() => row);
        private static void Emit(Func<string> row, bool flush = true)
        {
            try
            {
                // Projection and redaction are per-call managed work. Only the
                // append/flush needs the shared writer lock; native BEFORE still
                // completes synchronously on the dispatching thread.
                string text = row();
                lock (Sync) sink.Write(() => text, flush);
            }
            catch (Exception ex) { lock (Sync) { failedWrites++; lastWriteFailure = ex.GetType().Name; } try { Console.Error.WriteLine("DIAGNOSTIC_WRITE_FAILED: " + ex.Message); } catch /* swallow(logging-failure): stderr may be unavailable while reporting a journal write failure */ { } }
        }
        internal static string FormatRow(DateTime utc, string id, string name, string phase, int processId, string? objectType, string? objectPath,
            string? binding, int threadId, string apartment, JsonLineObject? details)
        {
            var entry = new JsonLineObject().String("utc", utc.ToString("O")).String("id", id).String("tool", name).String("phase", phase)
                .Number("mcpProcessId", processId).String("objectType", objectType).String("objectPath", objectPath).Raw("binding", binding)
                .Number("threadId", threadId).String("apartment", apartment);
            if (details != null) foreach (var pair in details.members) entry.Raw(pair.Key, pair.Value);
            return entry.ToString();
        }

        // Only cached diagnostic values enter this writer. Raw values come from the
        // host's existing JSON boundary or from this writer, never from native objects.
        internal sealed class JsonLineObject
        {
            internal readonly List<KeyValuePair<string, string>> members = new List<KeyValuePair<string, string>>();
            internal JsonLineObject String(string name, string? value) => Raw(name, Quote(value));
            internal JsonLineObject Number(string name, long? value) => Raw(name, value?.ToString(CultureInfo.InvariantCulture));
            internal JsonLineObject Raw(string name, string? value)
            {
                var pair = new KeyValuePair<string, string>(name, value ?? "null");
                int index = members.FindIndex(p => p.Key == name);
                if (index < 0) members.Add(pair); else members[index] = pair;
                return this;
            }
            public override string ToString()
            {
                var text = new StringBuilder("{");
                foreach (var pair in members)
                { if (text.Length > 1) text.Append(','); text.Append(Quote(pair.Key)).Append(':').Append(pair.Value); }
                return text.Append('}').ToString();
            }
            internal static string Quote(string? value)
            {
                if (value == null) return "null";
                var text = new StringBuilder("\"");
                for (int i = 0; i < value.Length; i++)
                {
                    char c = value[i];
                    switch (c)
                    {
                        case '\\': text.Append("\\\\"); break;
                        case '\b': text.Append("\\b"); break;
                        case '\f': text.Append("\\f"); break;
                        case '\n': text.Append("\\n"); break;
                        case '\r': text.Append("\\r"); break;
                        case '\t': text.Append("\\t"); break;
                        default:
                            // STJ's default encoder allows Basic Latin except HTML-sensitive
                            // characters, backtick and controls. Invalid UTF-16 becomes U+FFFD.
                            if (char.IsSurrogate(c) && !(char.IsHighSurrogate(c) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1])) &&
                                !(char.IsLowSurrogate(c) && i > 0 && char.IsHighSurrogate(value[i - 1]))) c = '\uFFFD';
                            if (c < ' ' || c > '~' || c == '"' || c == '&' || c == '\'' || c == '+' || c == '<' || c == '>' || c == '`')
                                text.Append("\\u").Append(((int)c).ToString("X4", CultureInfo.InvariantCulture));
                            else text.Append(c);
                            break;
                    }
                }
                return text.Append('"').ToString();
            }
        }
    }
}
