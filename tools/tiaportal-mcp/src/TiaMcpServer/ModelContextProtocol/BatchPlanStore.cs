using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace TiaMcpServer.ModelContextProtocol
{
    internal sealed class BatchPlanStore
    {
        internal sealed class Plan
        {
            internal string State = "";
            internal string Project = "";
            internal JsonArray Operations = new JsonArray();
            internal JsonArray Previews = new JsonArray();
            internal DateTime Expires;
        }
        private readonly Dictionary<string, Plan> _plans = new Dictionary<string, Plan>(StringComparer.Ordinal);
        internal string Add(Plan plan, DateTime now)
        {
            lock (_plans)
            {
                foreach (var key in _plans.Where(p => p.Value.Expires <= now).Select(p => p.Key).ToList()) _plans.Remove(key);
                if (_plans.Count >= 32) throw new InvalidOperationException("Too many pending previews; use or wait for expiry.");
                plan.Expires = now.AddMinutes(10);
                var token = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
                _plans.Add(token, new Plan { State = plan.State, Project = plan.Project, Operations = (JsonArray)plan.Operations.DeepClone(), Previews = (JsonArray)plan.Previews.DeepClone(), Expires = plan.Expires });
                return token;
            }
        }
        internal Plan Take(string token, DateTime now)
        {
            lock (_plans)
            {
                if (!_plans.TryGetValue(token, out var plan)) throw new ArgumentException("Unknown or already-used preview token.");
                _plans.Remove(token); // consumed before any native action; failures cannot accidentally retry writes
                if (plan.Expires <= now) throw new InvalidOperationException("Preview expired; preview again.");
                return plan;
            }
        }
        internal static string Stable(JsonNode? value)
        {
            if (value is JsonObject obj)
            {
                var result = new JsonObject();
                foreach (var item in obj.OrderBy(p => p.Key, StringComparer.Ordinal))
                    if (!string.Equals(item.Key, "timestamp", StringComparison.OrdinalIgnoreCase)) result[item.Key] = JsonNode.Parse(Stable(item.Value));
                return result.ToJsonString();
            }
            if (value is JsonArray array) return new JsonArray(array.Select(p => JsonNode.Parse(Stable(p))).ToArray()).ToJsonString();
            return value?.ToJsonString() ?? "null";
        }
    }
}
