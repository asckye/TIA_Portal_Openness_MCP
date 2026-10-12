using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TiaMcp.Logic.V4
{
    public static class EnvelopeMutation
    {
        public static JsonNode SessionReset(JsonNode body, bool reset)
        {
            var value = V4Json.Deserialize<Envelope>(body.ToJsonString());
            var m = value.Meta;
            var meta = new Meta(m.Timestamp, m.ReleaseKey, m.Tool, m.RequestId, m.Outcome, m.Execution,
                reset || value.Error?.Code == ErrorCode.SessionResetRequired, m.BehaviorPolicy, m.Completeness, m.Paging, m.Warnings);
            return JsonNode.Parse(V4Json.Serialize(new Envelope(4, value.Ok, value.Data, value.Error, meta)))!;
        }

        public static JsonNode Decorate(JsonNode body, bool disabled, string? requestId)
        {
            var value = V4Json.Deserialize<Envelope>(body.ToJsonString());
            var warnings = value.Meta.Warnings.ToList();
            if (disabled && !warnings.Any(w => w.Code == WarningCode.ApprovalDisabled))
                warnings.Add(new Warning(WarningCode.ApprovalDisabled, "Workbench approval is disabled for this MCP write call.",
                    new Dictionary<string, JsonElement> { ["enabled"] = JsonSerializer.SerializeToElement(false) }));
            return Replace(value, warnings, requestId);
        }

        public static JsonNode Precheck(JsonNode body, string? requestId)
        {
            var value = V4Json.Deserialize<Envelope>(body.ToJsonString());
            var warnings = value.Meta.Warnings.ToList();
            warnings.Add(new Warning(WarningCode.ApprovalPrecheckRefused, "The write was refused by the approval precheck; no approval was requested.",
                new Dictionary<string, JsonElement> { ["stage"] = JsonSerializer.SerializeToElement("approval-precheck") }));
            // Unknown/partial evidence cannot be turned into a before-operation
            // rejection by the approval decorator.
            bool terminalEvidence = value.Meta.Outcome is Outcome.Unknown or Outcome.Partial;
            return Replace(value, warnings, requestId, terminalEvidence ? null : Outcome.RejectedBeforeOperation,
                terminalEvidence ? null : Execution.NotStarted, terminalEvidence ? null : Completeness.None);
        }

        public static JsonNode AddWarning(JsonNode body, Warning warning)
        {
            var value = V4Json.Deserialize<Envelope>(body.ToJsonString());
            return Replace(value, value.Meta.Warnings.Concat(new[] { warning }).ToArray(), null);
        }

        private static JsonNode Replace(Envelope value, IReadOnlyList<Warning> warnings, string? requestId,
            Outcome? outcome = null, Execution? execution = null, Completeness? completeness = null)
        {
            var m = value.Meta;
            var meta = new Meta(m.Timestamp, m.ReleaseKey, m.Tool, requestId ?? m.RequestId, outcome ?? m.Outcome, execution ?? m.Execution,
                m.RequiresSessionReset, m.BehaviorPolicy, completeness ?? m.Completeness, m.Paging, warnings);
            return JsonNode.Parse(V4Json.Serialize(new Envelope(4, meta.Outcome == Outcome.Succeeded, value.Data, value.Error, meta)))!;
        }
    }
}
