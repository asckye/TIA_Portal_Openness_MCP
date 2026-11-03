using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Resources;
using System.Text.Json.Nodes;
using System.Text.Json;

namespace TiaMcp.Logic.V4
{
    public static class BehaviorCapabilities
    {
        private static readonly Lazy<JsonObject> Catalog = new Lazy<JsonObject>(() =>
        {
            var resource = new ResourceManager("TiaMcp.Logic.ModelContextProtocol.ToolProfiles", typeof(BehaviorCapabilities).Assembly);
            return JsonNode.Parse(resource.GetString("Catalog", CultureInfo.InvariantCulture)!)!.AsObject();
        });

        public const string CurrentDisclosure = "Native behaviorPolicy=current; V4 native acceptance is pending.";

        // Discovery and envelope disclosure consume the same ledger-derived resource as selection.
        public static JsonArray Table(Assembly product, string release)
        {
            return new JsonArray(Catalog.Value["behaviorPolicies"]!.AsArray().OfType<JsonObject>()
                .Where(r => (string?)r["releaseKey"] == release).Select(r =>
                {
                    string family = (string)r["family"]!;
                    return (JsonNode)new JsonObject { ["family"] = family,
                        ["state"] = Select(product, release, family) == BehaviorPolicy.SafeV4 ? "safe-v4" : "current",
                        ["l5"] = r["l5"]!.DeepClone(),
                        ["entries"] = new JsonArray(Catalog.Value["behaviorEntries"]!.AsArray().OfType<JsonObject>()
                            .Where(e => (string?)e["releaseKey"] == release && (string?)e["family"] == family)
                            .Select(e => (string)e["entry"]!).Distinct(StringComparer.Ordinal).OrderBy(e => e, StringComparer.Ordinal)
                            .Select(e => (JsonNode)JsonValue.Create(e)!).ToArray()) };
                }).ToArray());
        }

        public static Envelope Disclose(Envelope envelope, bool currentTarget = false)
        {
            var meta = Disclose(envelope.Meta, currentTarget);
            return ReferenceEquals(meta, envelope.Meta) ? envelope : new Envelope(4, envelope.Ok, envelope.Data, envelope.Error, meta);
        }

        private static Meta Disclose(Meta meta, bool currentTarget)
        {
            if (meta.ReleaseKey == null || meta.BehaviorPolicy == BehaviorPolicy.SafeV4
                || !currentTarget && !Catalog.Value["behaviorEntries"]!.AsArray().Any(e => (string?)e!["releaseKey"] == meta.ReleaseKey
                    && (string?)e["entry"] == meta.Tool)) return meta;
            // SafeV4 results already declare their policy. Shared admission/failure paths
            // still need disclosure even when they reject before reaching a tool body.
            var policy = currentTarget ? BehaviorPolicy.Current
                : EntryPolicy(typeof(BehaviorCapabilities).Assembly, meta.ReleaseKey, meta.Tool, BehaviorPolicy.NotApplicable);
            var warnings = meta.Warnings.ToList();
            if (policy == meta.BehaviorPolicy && (policy != BehaviorPolicy.Current || warnings.Any(w => w.Code == WarningCode.UnverifiedBehavior))) return meta;
            if (policy == BehaviorPolicy.Current && !warnings.Any(w => w.Code == WarningCode.UnverifiedBehavior))
                warnings.Add(new Warning(WarningCode.UnverifiedBehavior, CurrentDisclosure, new Dictionary<string, JsonElement>()));
            return new Meta(meta.Timestamp, meta.ReleaseKey, meta.Tool, meta.RequestId, meta.Outcome, meta.Execution,
                meta.RequiresSessionReset, policy, meta.Completeness, meta.Paging, warnings);
        }

        public static BehaviorPolicy Released(string release, string family)
        {
            var record = Catalog.Value["behaviorPolicies"]!.AsArray().Single(r => (string?)r!["releaseKey"] == release && (string?)r["family"] == family)!;
            return (string?)record["state"] == "safe-v4" ? BehaviorPolicy.SafeV4 : BehaviorPolicy.Current;
        }

        private static JsonObject? Entry(string release, string entry) => Catalog.Value["behaviorEntries"]!.AsArray()
            .OfType<JsonObject>().Where(r => (string?)r["releaseKey"] == release && (string?)r["entry"] == entry)
            .OrderBy(r => (string?)r["family"] == "P6-FALLBACK" ? 1 : 0)
            .FirstOrDefault(r => Select(typeof(BehaviorCapabilities).Assembly, release, (string)r["family"]!) == BehaviorPolicy.SafeV4)
            ?? Catalog.Value["behaviorEntries"]!.AsArray().OfType<JsonObject>()
                .FirstOrDefault(r => (string?)r["releaseKey"] == release && (string?)r["entry"] == entry);

        public static BehaviorPolicy EntryPolicy(Assembly product, string release, string entry, BehaviorPolicy fallback)
        {
            var record = Entry(release, entry);
            return record == null ? fallback : Select(product, release, (string)record["family"]!);
        }

        public static BehaviorPolicy Select(Assembly product, string release, string family) => Released(release, family);
    }
}
