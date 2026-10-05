using System;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaOpenness.Shared;

namespace TiaMcpServer.ModelContextProtocol
{
    // Keep the engine's JSON contract at its existing boundary.
    internal static partial class InvocationJournal
    {
        internal static Func<JsonObject?>? BindingSnapshot;
        private static string? ReadBinding() => BindingSnapshot?.Invoke()?.ToJsonString();
        internal static JsonObject Health() => JsonNode.Parse(HealthRow().ToString())!.AsObject();
        internal static void Write(string id, string name, string phase, string? objectType = null, string? objectPath = null, JsonObject? details = null)
            => WriteRow(id, name, phase, objectType, objectPath, () => Details(details));
        internal static JsonLineObject? Details(JsonObject? details)
        {
            if (details == null) return null;
            var row = new JsonLineObject();
            foreach (var pair in details) row.Raw(pair.Key, pair.Value?.DeepClone().ToJsonString());
            return row;
        }

        internal static CallSpan Observe(string id, string tool, string host, string? release, bool write, Func<string> arguments)
            => new CallSpan(id, tool, host, release, write, arguments);

        internal sealed class CallSpan : IDisposable
        {
            private readonly string id, tool, host;
            private readonly string? release;
            private readonly bool write;
            private readonly long started = Stopwatch.GetTimestamp();
            private readonly DateTime time = DateTime.UtcNow;
            private readonly string arguments;
            private bool completed;

            internal CallSpan(string id, string tool, string host, string? release, bool write, Func<string> arguments)
            {
                this.id = id; this.tool = tool; this.host = host; this.release = release; this.write = write;
                try { this.arguments = arguments(); }
                catch (Exception) /* swallow(logging-failure): serialization cannot change invocation behavior or echo unsupported objects */ { this.arguments = "null"; }
                Emit(() => Row(null, "BEFORE"));
            }

            internal void Complete(Func<string> result)
            {
                completed = true;
                Emit(() => Row(result(), "RETURNED"));
            }

            public void Dispose()
            {
                if (!completed) Emit(() => Row(null, "INTERRUPTED"));
            }

            private string Row(string? result, string phase)
            {
                JsonNode? wire = result == null ? null : JsonNode.Parse(result);
                JsonNode? envelope = wire is JsonObject obj && obj.ContainsKey("schemaVersion") ? wire
                    : wire?["structuredContent"];
                if (envelope == null && wire?["content"] is JsonArray content && content.Count == 1 && (string?)content[0]?["type"] == "text")
                {
                    try { envelope = JsonNode.Parse((string?)content[0]?["text"] ?? "null"); }
                    catch (JsonException) /* swallow(parse-fallback): legacy text has no V4 classification */ { }
                }
                var meta = envelope?["meta"] as JsonObject;
                var safe = JsonNode.Parse(CallJournalPayload.Sanitize(new JsonObject
                { ["arguments"] = JsonNode.Parse(arguments), ["result"] = wire?.DeepClone() }.ToJsonString())) as JsonObject;
                string parameters = CallJournalPayload.Redact(safe?["arguments"]?.ToJsonString() ?? "null");
                string response = CallJournalPayload.Redact(safe?["result"]?.ToJsonString() ?? "null");
                string target = "";
                if (safe?["arguments"] is JsonObject args)
                    target = string.Join(" · ", args.Where(p => new[] { "target", "softwarePath", "devicePath", "blockPath", "typePath", "projectPath", "projectFile", "name", "action" }.Contains(p.Key))
                        .Select(p => p.Key + "=" + p.Value?.ToJsonString()));
                var details = new JsonLineObject().Number("callProjection", 1).String("host", host).String("releaseKey", release)
                    .String("requestId", (string?)meta?["requestId"] ?? id).Raw("isWrite", write ? "true" : "false")
                    .String("startedUtc", time.ToString("O")).Number("durationMs", phase == "BEFORE" ? (long?)null :
                        (long)((Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency))
                    .String("target", CallJournalPayload.Bound(target)).String("arguments", parameters).String("result", response)
                    .Raw("argumentsTruncated", parameters.EndsWith(CallJournalPayload.Truncated, StringComparison.Ordinal) ? "true" : "false")
                    .Raw("resultTruncated", response.EndsWith(CallJournalPayload.Truncated, StringComparison.Ordinal) ? "true" : "false")
                    .Raw("ok", envelope?["schemaVersion"]?.ToJsonString() == "4" ? envelope?["ok"]?.ToJsonString() : null)
                    .String("outcome", (string?)meta?["outcome"]).String("execution", (string?)meta?["execution"])
                    .String("completeness", (string?)meta?["completeness"]).String("errorCode", (string?)envelope?["error"]?["code"]);
                return FormatRow(DateTime.UtcNow, id, tool, phase, ProcessId, null, null, null,
                    System.Threading.Thread.CurrentThread.ManagedThreadId, System.Threading.Thread.CurrentThread.GetApartmentState().ToString(), details);
            }
        }
    }
}
