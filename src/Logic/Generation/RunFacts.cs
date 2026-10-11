using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace TiaMcp.Logic.Generation
{
    // Inventory predictions change only after a verified successful effect.
    public sealed class RunFacts
    {
        private readonly Dictionary<string, JsonNode?> values = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        private readonly Dictionary<string, HashSet<string>> sources = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        private readonly Dictionary<string, HashSet<string>> objects = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> identities = new Dictionary<string, string>(StringComparer.Ordinal);
        public RunFacts(GenerationPlan plan)
        {
            foreach (var pair in plan.Observed)
            {
                sources[pair.Key] = new HashSet<string>(pair.Value.SourceNames, StringComparer.OrdinalIgnoreCase);
                objects[pair.Key] = new HashSet<string>(pair.Value.ObjectKeys, StringComparer.OrdinalIgnoreCase);
            }
        }
        public JsonNode? Resolve(string key) => values.TryGetValue(key, out var value) ? value?.DeepClone() : null;
        public string[] Sources(string software) => sources.TryGetValue(software, out var set) ? set.OrderBy(s => s, StringComparer.Ordinal).ToArray() : Array.Empty<string>();
        public string[] Objects(string software) => objects.TryGetValue(software, out var set) ? set.OrderBy(s => s, StringComparer.Ordinal).ToArray() : Array.Empty<string>();
        public string? SourceIdentity(string software, string source) => identities.TryGetValue(software + "/" + source, out var value) ? value : null;
        public bool Imported(string software, string source) => values.Any(v => v.Key.EndsWith(".imported", StringComparison.Ordinal) && v.Value?.ToString() == software + "/" + source);
        public JsonObject CapturePreview(GenerationPlanStepsItem step, JsonObject projection)
        {
            var added = new JsonObject();
            if (step.Review?.Rule == StepReviewRules.Generate)
            {
                string software = (string)projection["softwarePath"]!, source = (string)projection["sourceName"]!, identity = (string)projection["sourceIdentity"]!;
                identities[software + "/" + source] = identity;
                Put("sourceIdentity", JsonValue.Create(identity));
            }
            return added;
            void Put(string field, JsonNode? value) { values[step.Id + "." + field] = value?.DeepClone(); added[field] = value?.DeepClone(); }
        }
        public JsonObject Accept(GenerationPlanStepsItem step, JsonObject result)
        {
            var added = new JsonObject();
            string software = step.Arguments.TryGetValue("softwarePath", out var path) ? path.GetString()! : "";
            if (!sources.ContainsKey(software)) sources[software] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!objects.ContainsKey(software)) objects[software] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (step.Review?.Rule == StepReviewRules.Import)
            {
                string source = (string)result["sourceName"]!;
                sources[software].Add(source); Put("sourceName", JsonValue.Create(source)); Put("imported", JsonValue.Create(software + "/" + source));
            }
            else if (step.Review?.Rule == StepReviewRules.Generate)
            {
                var generated = step.Review.Effect["generated"].EnumerateArray().Select(k => k.GetString()!).ToArray();
                objects[software].UnionWith(generated); Put("generatedKeys", new JsonArray(generated.Select(k => (JsonNode?)JsonValue.Create(k)).ToArray()));
            }
            else if (step.Review?.Rule == StepReviewRules.Delete)
            {
                sources[software].Remove((string)result["sourceName"]!); Put("deleted", JsonValue.Create(true));
            }
            else if (step.Tool == "MovePlcBlockToGroup")
            {
                string name = step.Arguments["blockName"].GetString()!, group = step.Arguments["groupPath"].GetString()!;
                objects[software].Remove("block:" + name); objects[software].Add("block:" + (group.Length == 0 ? name : group + "/" + name));
                Put("objectPath", JsonValue.Create(group.Length == 0 ? name : group + "/" + name));
            }
            return added;
            void Put(string field, JsonNode? value) { values[step.Id + "." + field] = value?.DeepClone(); added[field] = value?.DeepClone(); }
        }
    }
}
