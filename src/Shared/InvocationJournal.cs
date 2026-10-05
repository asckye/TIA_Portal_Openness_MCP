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
        private static readonly string ProcessKey = ProcessId + "-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N");
        private static readonly AsyncLocal<string?> Current = new AsyncLocal<string?>();
        private static IJournalSink sink = new FileJournalSink();
        private static Func<string>? correlationSource;
        private static long failedWrites;
        private static string? lastWriteFailure;
        internal static string CorrelationId => correlationSource?.Invoke() ?? Current.Value ?? (Current.Value = Guid.NewGuid().ToString("N"));

        // The callback must synchronously flush before returning. Delegates can cross the
        // separately compiled adapter assemblies; step H will supply the engine's sink and id.
        internal static void ConfigureOutput(Action<string>? write, Func<string>? correlation = null)
        {
            lock (Sync) { sink = write == null ? (IJournalSink)new FileJournalSink() : new CallbackJournalSink(write); correlationSource = correlation; }
        }

        internal interface IJournalSink { void Write(Func<string> row); }
        private sealed class CallbackJournalSink : IJournalSink
        {
            private readonly Action<string> write;
            internal CallbackJournalSink(Action<string> write) { this.write = write; }
            public void Write(Func<string> row) => write(row());
        }
        private sealed class FileJournalSink : IJournalSink
        {
            private TiaOpenness.Shared.JournalRetention retention = new TiaOpenness.Shared.JournalRetention();
            private long settingsRead;
            public void Write(Func<string> row)
            {
                var root = TiaOpenness.Shared.DataLocations.Current.DiagnosticsDirectory;
                if (!Path.IsPathRooted(root)) throw new IOException("Diagnostic path must be absolute.");
                Directory.CreateDirectory(root);
                string path = Path.Combine(root, "calls-" + ProcessKey + ".jsonl");
                string text = row();
                long now = Stopwatch.GetTimestamp();
                if (settingsRead == 0 || now - settingsRead >= Stopwatch.Frequency)
                { retention = TiaOpenness.Shared.JournalRetention.Load(TiaOpenness.Shared.JournalRetention.SettingsPath); settingsRead = now; }
                retention.Rotate(path, Encoding.UTF8.GetByteCount(text + Environment.NewLine));
                // Flush BEFORE before calling into native code, even if the native process later crashes.
                using (var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read))
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false))) { writer.WriteLine(text); writer.Flush(); stream.Flush(true); }
            }
        }

        internal static JsonLineObject HealthRow()
        {
            lock (Sync) return new JsonLineObject().Number("failedWrites", failedWrites).String("lastFailure", lastWriteFailure)
                .String("durability", "best-effort; successful writes flushed to disk")
                .String("nativeBoundaryCoverage", typeof(InvocationJournal).Assembly.GetType("TiaMcpServer.Diagnostics.GeneratedNativeCalls", false) != null ? "build-instrumented" : "not-instrumented");
        }
        internal static string Begin(string name, string? correlation = null)
        {
            string id = Guid.TryParseExact(correlation ?? correlationSource?.Invoke(), "N", out var parsed) ? parsed.ToString("N") : Guid.NewGuid().ToString("N");
            Current.Value = id; WriteRow(id, name, "BEFORE"); return id;
        }
        internal static T Native<T>(string stage, Func<T> call, string? objectType = null, string? objectPath = null)
        {
            string id = correlationSource?.Invoke() ?? Current.Value ?? Guid.NewGuid().ToString("N");
            WriteRow(id, "native:" + stage, "BEFORE", objectType, objectPath);
            try { T result = call(); WriteRow(id, "native:" + stage, "RETURNED", objectType, objectPath); return result; }
            catch (Exception ex) { _ = PortalFailureClassifier.IsPortalProcessLost(ex); WriteRow(id, "native:" + stage, "THREW", objectType, objectPath); throw; }
        }
        internal static void Native(string stage, Action call, string? objectType = null, string? objectPath = null) => Native(stage, () => { call(); return true; }, objectType, objectPath);
        internal static void WriteRow(string id, string name, string phase, string? objectType = null, string? objectPath = null, Func<JsonLineObject?>? details = null)
            => Emit(() => FormatRow(DateTime.UtcNow, id, name, phase, ProcessId, objectType, objectPath,
                ReadBinding(), Thread.CurrentThread.ManagedThreadId, Thread.CurrentThread.GetApartmentState().ToString(), details?.Invoke()));
        internal static void WriteLine(string row) => Emit(() => row);
        private static void Emit(Func<string> row)
        {
            try
            {
                lock (Sync) sink.Write(row);
            }
            catch (Exception ex) { lock (Sync) { failedWrites++; lastWriteFailure = ex.GetType().Name; } try { Console.Error.WriteLine("Invocation journal unavailable: " + ex.GetType().Name); } catch /* swallow(logging-failure): stderr may be unavailable while reporting a journal write failure */ { } }
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
