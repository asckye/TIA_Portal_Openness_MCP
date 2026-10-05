using System;
using System.Diagnostics;
using System.Text.Json;

namespace TiaOpenness.Shared
{
    internal sealed class AuditInvocation : IDisposable
    {
        private readonly AuditLog log;
        private readonly string requestId, host, release, tool;
        private bool completed;
        private AuditInvocation(AuditLog log, string requestId, string host, string release, string tool)
        { this.log = log; this.requestId = requestId; this.host = host; this.release = release; this.tool = tool; Emit("request"); Emit("start"); }
        internal static AuditInvocation? Begin(bool write, string host, string release, string tool, string? requestId = null, AuditLog? log = null)
        {
            if (!write) return null;
            try { return new AuditInvocation(log ?? AuditLog.Current, requestId ?? Guid.NewGuid().ToString("N"), host, release, tool); }
            catch (Exception ex) { Trace.TraceWarning("Audit log unavailable: " + ex.GetType().Name); return null; }
        }
        internal void Complete(string? json)
        {
            string outcome = "unknown";
            try
            {
                if (json != null)
                    using (var document = JsonDocument.Parse(json))
                    {
                        var root = document.RootElement;
                        if (root.TryGetProperty("schemaVersion", out var schema) && schema.GetInt32() == 4 && root.TryGetProperty("meta", out var meta)
                            && meta.TryGetProperty("outcome", out var value) && value.ValueKind == JsonValueKind.String)
                            outcome = value.GetString()!;
                    }
            }
            catch (Exception ex) when (ex is JsonException || ex is InvalidOperationException || ex is FormatException)
            { Trace.TraceInformation("Audit result has no readable V4 outcome: " + ex.GetType().Name); }
            if (outcome != "succeeded" && outcome != "rejected-before-operation" && outcome != "failed" && outcome != "read-failed" && outcome != "partial" && outcome != "unknown") outcome = "unknown";
            End(outcome);
        }
        internal void End(string outcome) { if (completed) return; completed = true; Emit("end", outcome); }
        private void Emit(string kind, string? outcome = null)
        {
            try { log.Append(kind, requestId, host, release, tool, outcome); }
            catch (Exception ex) { Trace.TraceWarning("Audit event unavailable: " + ex.GetType().Name); }
        }
        public void Dispose() => End("unknown");
    }
}
