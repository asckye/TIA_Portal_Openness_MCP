using System;
using System.Collections.Generic;
using System.Linq;

namespace TiaMcp.Logic.V4
{
    // Per-session issuance evidence. Internal approval previews never renew a caller's plan.
    internal sealed class OnlinePlanLifetime
    {
        private readonly Dictionary<string, (DateTimeOffset Expires, DateTimeOffset RefreshAfter, string Fingerprint)> _plans =
            new Dictionary<string, (DateTimeOffset Expires, DateTimeOffset RefreshAfter, string Fingerprint)>(StringComparer.Ordinal);

        internal (string Hash, DateTimeOffset Expires) Preview(string fingerprint, DateTimeOffset now, int seconds)
        {
            lock (_plans)
            {
                foreach (var plan in _plans)
                    if (plan.Value.Fingerprint == fingerprint && plan.Value.Expires > now && now <= plan.Value.RefreshAfter)
                        return (plan.Key, plan.Value.Expires);
                foreach (string key in _plans.Where(p => p.Value.Expires <= now).Select(p => p.Key).ToArray()) _plans.Remove(key);
                string hash = TiaOpenness.Shared.PendingApproval.Hash(fingerprint + "\n" + Guid.NewGuid().ToString("N"));
                var deadline = now.AddSeconds(seconds);
                _plans[hash] = (deadline, now.AddSeconds(seconds / 2.0), fingerprint);
                return (hash, deadline);
            }
        }

        internal bool Matches(string hash, string fingerprint)
        {
            lock (_plans)
                return _plans.TryGetValue(hash, out var plan) && plan.Fingerprint == fingerprint;
        }

        internal Error? Refusal(string hash, DateTimeOffset now)
        {
            lock (_plans)
                return _plans.TryGetValue(hash, out var plan) && now < plan.Expires ? null
                    : new Error("This online write preview expired or belongs to another host/session. Preview again before execution.",
                        new PlanStaleDetails(hash, "expired"));
        }
    }
}
