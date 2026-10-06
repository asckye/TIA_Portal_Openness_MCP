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
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
    public sealed class BehaviorCandidateAttribute : Attribute
    {
        public string Entry { get; }
        public string Family { get; }
        public Type Contract { get; }
        public BehaviorCandidateAttribute(string entry, string family, Type contract) { Entry = entry; Family = family; Contract = contract; }
    }

    public static class BehaviorCapabilities
    {
        public const string TestProperty = "TiaMcpTestPolicy";
        public const string DeviceCandidate = "P6-DEVICE:safe-v4";
        public const string ImportCandidate = "P6-IMPORT:safe-v4";

        public static string[] TestFamilies(string? setting)
        {
            if (setting == null) return Array.Empty<string>();
            var tokens = setting.Split(',');
            var known = new[] { "DEVICE", "IMPORT", "EXPORT", "SESSION", "CLOSE", "SOURCE", "COMPILE", "FALLBACK" }
                .Select(f => "P6-" + f + ":safe-v4").ToArray();
            if (tokens.Any(t => !known.Contains(t, StringComparer.Ordinal)) || tokens.Distinct(StringComparer.Ordinal).Count() != tokens.Length)
                throw new InvalidOperationException("Invalid test policy build metadata.");
            return tokens;
        }
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
            // Candidate results already declare SafeV4. Shared admission/failure paths
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

        public static JsonObject? CandidateExample(string release, string entry)
        {
            var record = Entry(release, entry);
            return record != null && Select(typeof(BehaviorCapabilities).Assembly, release, (string)record["family"]!) == BehaviorPolicy.SafeV4
                ? (JsonObject)record["example"]!.DeepClone() : null;
        }

        // Only assembly metadata emitted by an explicit build property can override the ledger.
        public static BehaviorPolicy Select(Assembly product, string release, string family)
        {
            var policy = Released(release, family);
            var setting = product.GetCustomAttributes<AssemblyMetadataAttribute>().SingleOrDefault(a => a.Key == TestProperty)?.Value
                ?? typeof(BehaviorCapabilities).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().SingleOrDefault(a => a.Key == TestProperty)?.Value;
            return TestFamilies(setting).Contains(family + ":safe-v4", StringComparer.Ordinal) ? BehaviorPolicy.SafeV4 : policy;
        }

        public static MethodInfo SelectMethod(string entry, MethodInfo current, IEnumerable<MethodInfo> candidates,
            string release, Func<string, BehaviorPolicy>? select = null)
        {
            var matches = candidates.Select(m => new { Method = m, Policy = m.GetCustomAttribute<BehaviorCandidateAttribute>() })
                .Where(c => c.Policy != null && c.Policy.Entry == entry).ToArray();
            if (matches.Select(c => c.Policy!.Family).Distinct(StringComparer.Ordinal).Count() != matches.Length
                || matches.Length > 1 && (matches.Length != 2 || !matches.Any(c => c.Policy!.Family == "P6-FALLBACK")))
                throw new InvalidOperationException("Duplicate behavior candidate for " + entry);
            if (matches.Length == 0) return current;
            // The entry's owning family takes precedence when both isolated test policies are selected.
            var candidate = matches.OrderBy(c => c.Policy!.Family == "P6-FALLBACK" ? 1 : 0).FirstOrDefault(c =>
                (select?.Invoke(c.Policy!.Family) ?? Select(current.DeclaringType!.Assembly, release, c.Policy!.Family)) == BehaviorPolicy.SafeV4);
            return candidate?.Method ?? current;
        }
    }
}
