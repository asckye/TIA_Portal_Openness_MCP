using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Resources;
using System.Text.Json.Nodes;

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

        public static BehaviorPolicy Released(string release, string family)
        {
            var record = Catalog.Value["behaviorPolicies"]!.AsArray().Single(r => (string?)r!["releaseKey"] == release && (string?)r["family"] == family)!;
            return (string?)record["state"] == "safe-v4" ? BehaviorPolicy.SafeV4 : BehaviorPolicy.Current;
        }

        private static JsonObject? Entry(string release, string entry) => Catalog.Value["behaviorEntries"]!.AsArray()
            .OfType<JsonObject>().SingleOrDefault(r => (string?)r["releaseKey"] == release && (string?)r["entry"] == entry);

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
            if (matches.Length > 1) throw new InvalidOperationException("Duplicate behavior candidate for " + entry);
            if (matches.Length == 0) return current;
            var candidate = matches[0];
            return (select?.Invoke(candidate.Policy!.Family) ?? Select(current.DeclaringType!.Assembly, release, candidate.Policy!.Family))
                == BehaviorPolicy.SafeV4 ? candidate.Method : current;
        }
    }
}
