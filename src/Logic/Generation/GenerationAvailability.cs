using System;
using System.Globalization;
using System.Linq;
using System.Resources;
using System.Text.Json;

namespace TiaMcp.Logic.Generation
{
    public static class GenerationAvailability
    {
        private static readonly Lazy<JsonElement> Catalog = new Lazy<JsonElement>(() => CanonicalJson.Parse(
            new ResourceManager("TiaMcp.Logic.ModelContextProtocol.ToolProfiles", typeof(GenerationAvailability).Assembly)
                .GetString("Catalog", CultureInfo.InvariantCulture)!));

        public static GenerationPlanStepsItemAvailability Get(string release, string tool)
        {
            var catalog = Catalog.Value;
            if (!catalog.GetProperty("releases").TryGetProperty(release, out var roster))
                throw CanonicalJson.Failure("/target/release", "availability", "Unknown release.");
            var present = roster.EnumerateArray().Any(row => row.GetProperty("name").GetString() == tool);
            var entries = catalog.GetProperty("behaviorEntries").EnumerateArray()
                .Where(row => row.GetProperty("releaseKey").GetString() == release && row.GetProperty("entry").GetString() == tool).ToArray();
            var policies = catalog.GetProperty("behaviorPolicies").EnumerateArray().Where(row =>
                row.GetProperty("releaseKey").GetString() == release && entries.Any(entry =>
                    entry.GetProperty("family").GetString() == row.GetProperty("family").GetString())).ToArray();
            // A roster entry is not native acceptance. All applicable families must be accepted.
            return new GenerationPlanStepsItemAvailability
            {
                Tool = present ? "present" : "absent",
                BehaviorPolicy = policies.Length == 0 ? "not-applicable" : policies.Any(p => p.GetProperty("state").GetString() == "current") ? "current" : "safe-v4",
                Native = policies.Length > 0 && policies.All(p => p.GetProperty("l5").GetString() == "accepted") ? "accepted" : "NOT RUN"
            };
        }
    }
}
