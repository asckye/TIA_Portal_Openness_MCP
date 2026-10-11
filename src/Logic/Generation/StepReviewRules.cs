using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace TiaMcp.Logic.Generation
{
    public sealed class StepReviewVerdict
    {
        public JsonArray Differences { get; } = new JsonArray();
        public bool Matches => Differences.Count == 0;
        internal void Different(string field, JsonNode? expected, JsonNode? observed)
            => Differences.Add(new JsonObject { ["field"] = field, ["expected"] = expected?.DeepClone(), ["observed"] = observed?.DeepClone() });
    }

    // Rules consume existing current-policy results. SafeV4 candidate rules are deliberately absent.
    public static class StepReviewRules
    {
        public const string CreateProject = "project-create/1";
        public const string Record = "generation-record/1";
        public const string Import = "external-source-import/1", Generate = "external-source-generate/1", Delete = "external-source-delete/1";
        private static readonly IReadOnlyDictionary<string, string> Tools = new Dictionary<string, string> {
            [Import] = "ImportPlcExternalSource", [Generate] = "GenerateBlocksFromExternalSource", [Delete] = "DeletePlcExternalSource", [Record] = "PublishGenerationRecord" };
        public static bool Supports(GenerationPlanStepsItem step) => step.Review != null && step.Review.Mode == "tool-preview"
            && Tools.TryGetValue(step.Review.Rule, out var tool) && tool == step.Tool;
        public static StepReviewVerdict Transition(JsonObject before, JsonObject after, string predicted, JsonObject tree)
        {
            var verdict = new StepReviewVerdict();
            foreach (string field in new[] { "processId", "processStartUtc" })
                if (before[field] == null || !JsonNode.DeepEquals(before[field], after[field])) verdict.Different(field, before[field], after[field]);
            bool samePath = false;
            try { samePath = string.Equals(GenerationProjectPath.Normalize(predicted), GenerationProjectPath.Normalize((string?)after["projectPath"] ?? ""), StringComparison.OrdinalIgnoreCase); }
            catch (ArgumentException ex) { verdict.Differences.Add(new JsonObject { ["field"] = "projectPath.normalization", ["error"] = ex.Message }); }
            if (!samePath) verdict.Different("projectPath", JsonValue.Create(predicted), after["projectPath"]);
            if (!long.TryParse(before["generation"]?.ToString(), out var previous) || !long.TryParse(after["generation"]?.ToString(), out var current) || current <= previous)
                verdict.Different("generation", before["generation"], after["generation"]);
            if (!EmptyProject(tree, predicted)) verdict.Different("emptyProject", JsonValue.Create(true), tree);
            return verdict;
        }
        private static bool EmptyProject(JsonObject tree, string project)
        {
            // Native Foundation/ported trees contain only the project header and the empty built-in group.
            string? text = (string?)tree["tree"] ?? (string?)tree["value"];
            if (text != null)
            {
                var lines = text.Split('\n').Select(l => l.Trim().TrimEnd('\r')).Where(l => l.Length > 0).ToArray();
                if (lines.Length >= 2 && lines[0] == "```" && lines[lines.Length - 1] == "```") lines = lines.Skip(1).Take(lines.Length - 2).ToArray();
                string name = project.Replace('\\', '/').Split('/').Last();
                name = name.Substring(0, name.LastIndexOf('.'));
                return lines.Length >= 1 && lines[0] == name && lines.Skip(1).All(l => l == "Ungrouped devices");
            }
            return tree["devices"] is JsonArray devices && devices.Count == 0
                && (tree["deviceGroups"] == null || tree["deviceGroups"] is JsonArray groups && groups.Count == 0)
                && (tree["ungroupedDevices"] == null || tree["ungroupedDevices"] is JsonArray ungrouped && ungrouped.Count == 0);
        }
        public static JsonObject PreviewArguments(GenerationPlanStepsItem step)
        {
            Require(step);
            var arguments = JsonSerializer.SerializeToNode(step.Arguments)!.AsObject();
            arguments.Remove("expectedPlanHash"); arguments["dryRun"] = true;
            return arguments;
        }
        public static JsonObject FinalArguments(GenerationPlanStepsItem step, JsonObject projection)
        {
            Require(step);
            string hash = String(projection["planHash"]) ?? "";
            if (!Hash(hash)) throw new ArgumentException("Preview must supply its own SHA-256 tool hash.");
            var arguments = JsonSerializer.SerializeToNode(step.Arguments)!.AsObject();
            arguments["expectedPlanHash"] = hash;
            return arguments;
        }
        private static void Require(GenerationPlanStepsItem step)
        {
            if (!Supports(step) || !step.Review!.LateArguments.SequenceEqual(new[] { "expectedPlanHash" }) || step.Arguments.ContainsKey("expectedPlanHash"))
                throw new ArgumentException("Only the registered preview rule's expectedPlanHash slot may be late-filled.");
        }
        public static JsonObject Project(JsonObject data) => data.DeepClone().AsObject();
        public static StepReviewVerdict Compare(GenerationPlan plan, GenerationPlanStepsItem step, JsonObject projection, RunFacts facts, int processId)
        {
            Require(step);
            var review = step.Review!;
            var verdict = new StepReviewVerdict();
            if (review.Rule == Record)
            {
                foreach (var pair in review.Expect) Equal(pair.Key, JsonNode.Parse(pair.Value.GetRawText()));
                if (!Hash(String(projection["planHash"]))) verdict.Different("planHash", JsonValue.Create("sha256"), projection["planHash"]);
                if (!SameFile(String(projection["projectFile"]), plan.Target.ProjectIdentity)) verdict.Different("projectFile", JsonValue.Create(plan.Target.ProjectIdentity), projection["projectFile"]);
                Equal("release", JsonValue.Create(plan.Target.Release));
                if (projection["record"] is not JsonObject record || record["objects"] is not JsonArray records)
                    verdict.Different("record", JsonValue.Create("ownership projection required"), projection["record"]);
                else
                {
                    var declared = plan.Objects.Select(o => o.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    var newObjects = records.OfType<JsonObject>().Where(o => (string?)o["createdBy"]?["runId"] == (string?)projection["runId"]).ToArray();
                    var wanted = facts.CompletedKeys.Where(declared.Contains).Concat(plan.Adopt).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    if (!wanted.SetEquals(newObjects.Select(o => (string?)o["key"] ?? "")) || newObjects.Any(o =>
                        !declared.Contains((string?)o["key"] ?? "") || (string?)o["state"] is not ("owned" or "adopted") || o["witness"] == null ||
                        (string?)o["expectedFingerprint"] != plan.Objects.Single(p => p.Key == (string?)o["key"]).ExpectedFingerprint))
                        verdict.Different("record.objects", Strings(wanted), records);
                }
                return verdict;
            }
            Equal("status", JsonValue.Create("planned")); Equal("release", JsonValue.Create(plan.Target.Release));
            if (review.Rule != Delete) Equal("operation", JsonValue.Create(step.Tool));
            Equal("executed", JsonValue.Create(false)); Equal("attempted", JsonValue.Create(false)); Equal("requiresSessionReset", JsonValue.Create(false));
            if (!SameFile(String(projection["projectFile"]), plan.Target.ProjectIdentity)) verdict.Different("projectFile", JsonValue.Create(plan.Target.ProjectIdentity), projection["projectFile"]);
            Equal("processId", JsonValue.Create(processId));
            string software = step.Arguments["softwarePath"].GetString()!;
            bool hasOwnedKey = review.Expect.TryGetValue("ownedKey", out var ownedKey);
            bool ownedSource = review.Rule == Delete && hasOwnedKey
                && review.Witness.TryGetValue("targetIdentity", out var ownedConstraint) && ownedConstraint == "capture-owned-source";
            var ownership = ownedSource ? plan.Objects.SingleOrDefault(o => o.Key == ownedKey.GetString()) : null;
            if (ownedSource && (ownership == null || !step.Key.StartsWith(ownership.Key + "/", StringComparison.Ordinal) || ownership.State is not ("owned" or "adopted")
                || ownership.Witness?.Kind != "external-source" || ownership.Witness.Tool != "ListPlcExternalSources"
                || ownership.Artifact == null || !ownership.Witness.Values.TryGetValue("contentHash", out var content) || content.GetString() != ownership.Artifact.Sha256
                || !ownership.Witness.Values.TryGetValue("present", out var present) || present.ValueKind != JsonValueKind.True
                || !review.Witness.TryGetValue("ownership", out var proof) || proof != CanonicalJson.Hash(JsonSerializer.SerializeToElement(ownership))
                || !ownership.Key.EndsWith("/externalSource/" + Uri.EscapeDataString(String(projection["sourceName"]) ?? ""), StringComparison.Ordinal)
                || !plan.Observed.TryGetValue(software, out var ownedInventory) || !ownedInventory.SourceNames.Contains(String(projection["sourceName"]) ?? "", StringComparer.Ordinal)))
                verdict.Different("source-ownership", JsonValue.Create("verified owned source artifact and observed name"), projection["sourceName"]);
            foreach (var pair in review.Expect)
            {
                if (ownedSource && pair.Key == "ownedKey") continue;
                var expected = JsonNode.Parse(pair.Value.GetRawText());
                if (pair.Key == "sourcesBefore" || pair.Key == "objectKeysBefore")
                {
                    if (expected?.ToString() != "observed+run") verdict.Different(pair.Key + ".constraint", JsonValue.Create("observed+run"), expected);
                    var predicted = pair.Key == "sourcesBefore" ? facts.Sources(software) : facts.Objects(software);
                    Set(pair.Key, predicted, pair.Key == "sourcesBefore" ? Names(projection["sourcesBefore"]) : Keys(projection["objectsBefore"]));
                    continue;
                }
                if (expected is JsonObject reference && reference["fact"] is JsonValue)
                {
                    expected = facts.Resolve((string)reference["fact"]!);
                    if (expected == null) { verdict.Different(pair.Key + ".fact", reference, null); continue; }
                }
                string field = pair.Key == "sourceName" && review.Rule == Import ? "requestedSourceName" : pair.Key;
                if (field == "filePath")
                { if (!SameFile(String(projection[field]), expected?.ToString())) verdict.Different(field, expected, projection[field]); }
                else Equal(field, expected);
            }
            // Mandatory constraints cannot be removed by constructing an incomplete review block.
            Equal("softwarePath", JsonValue.Create(software)); Equal("groupPath", JsonValue.Create(""));
            if (!review.Witness.TryGetValue("planHash", out var hashConstraint) || hashConstraint != "sha256" || !Hash(String(projection["planHash"])))
                verdict.Different("planHash", JsonValue.Create("sha256"), projection["planHash"]);
            foreach (var pair in review.Witness)
            {
                if (pair.Key == "planHash" || ownedSource && pair.Key == "ownership") continue;
                if (ownedSource && pair.Key == "targetIdentity" && pair.Value == "capture-owned-source")
                { if (string.IsNullOrWhiteSpace(String(projection["targetIdentity"]))) verdict.Different("targetIdentity", JsonValue.Create("nonempty owned-source identity"), projection["targetIdentity"]); continue; }
                if (pair.Value == "ignore" && new[] { "objectsBefore.modifiedDate", "objectsBefore.isConsistent", "objectsBefore.typeName", "objectsBefore.programmingLanguage" }.Contains(pair.Key)) continue;
                if (pair.Key == "sourceIdentity" && pair.Value == "capture-as-fact")
                {
                    string? identity = String(projection[pair.Key]), source = String(projection["sourceName"]);
                    string? prior = facts.SourceIdentity(software, source ?? "");
                    if (string.IsNullOrWhiteSpace(identity) || prior != null && prior != identity) verdict.Different(pair.Key, JsonValue.Create(prior ?? "nonempty"), projection[pair.Key]);
                }
                else if (pair.Key == "targetIdentity" && pair.Value.StartsWith("fact:", StringComparison.Ordinal))
                {
                    var expected = facts.Resolve(pair.Value.Substring(5));
                    if (expected == null || string.IsNullOrWhiteSpace(expected.ToString())) verdict.Different(pair.Key + ".fact", JsonValue.Create(pair.Value), expected);
                    else Equal(pair.Key, expected);
                }
                else verdict.Different(pair.Key + ".constraint", null, JsonValue.Create(pair.Value));
            }
            foreach (var pair in review.Forbid)
            {
                var observed = pair.Key == "objectKeysBefore" ? Keys(projection["objectsBefore"]) : pair.Key == "sourcesBefore" ? Names(projection["sourcesBefore"]) : Array.Empty<string>();
                if (observed == null || pair.Value.Intersect(observed, StringComparer.OrdinalIgnoreCase).Any())
                    verdict.Different("forbid." + pair.Key, Strings(pair.Value), Strings(observed ?? Array.Empty<string>()));
            }
            string sourceName = String(projection["sourceName"]) ?? "";
            if (review.Rule != Import && !ownedSource && !facts.Imported(software, sourceName)) verdict.Different("source-custody", JsonValue.Create("imported-in-this-run"), JsonValue.Create(sourceName));
            if (review.Rule == Generate && (!review.Witness.ContainsKey("sourceIdentity") || !review.Forbid.ContainsKey("objectKeysBefore") || !review.Effect.ContainsKey("generated")))
                verdict.Different("review", JsonValue.Create("identity, forbidden keys and declared generated keys required"), null);
            if (review.Rule == Delete && !ownedSource && (!review.Witness.ContainsKey("targetIdentity") || facts.SourceIdentity(software, sourceName) != String(projection["targetIdentity"])))
                verdict.Different("targetIdentity", JsonValue.Create(facts.SourceIdentity(software, sourceName)), projection["targetIdentity"]);
            return verdict;

            void Equal(string field, JsonNode? expected) { if (!JsonNode.DeepEquals(expected, projection[field])) verdict.Different(field, expected, projection[field]); }
            void Set(string field, IEnumerable<string> expected, string[]? observed)
            { if (observed == null || !new HashSet<string>(expected, StringComparer.OrdinalIgnoreCase).SetEquals(observed)) verdict.Different(field, Strings(expected), Strings(observed ?? Array.Empty<string>())); }
        }
        public static StepReviewVerdict VerifyEffect(GenerationPlan plan, GenerationPlanStepsItem step, JsonObject data, RunFacts facts)
        {
            Require(step); var verdict = new StepReviewVerdict(); var review = step.Review!;
            if (review.Rule == Record)
            {
                if (!True(data["executed"]) || !SameFile(String(data["recordPath"]), step.Arguments["recordPath"].GetString()) || String(data["recordHash"]) == null)
                    verdict.Different("record", JsonValue.Create("atomic publication with verified recordHash"), data);
                return verdict;
            }
            if (!True(data["executed"])) verdict.Different("executed", JsonValue.Create(true), data["executed"]);
            string software = step.Arguments["softwarePath"].GetString()!;
            string source = step.Tool == "ImportPlcExternalSource" ? step.Expect.Contains! : step.Arguments["externalSourceName"].GetString()!;
            if (String(data["sourceName"]) != source) verdict.Different("sourceName", JsonValue.Create(source), data["sourceName"]);
            if (review.Rule == Import)
            {
                Set("sourcesAdded", new[] { source }, Names(JsonNode.Parse(review.Effect["sourcesAdded"].GetRawText())));
                Set("sourceNamesAfter", facts.Sources(software).Concat(new[] { source }), Names(data["sourceNamesAfter"]));
            }
            else if (review.Rule == Generate)
            {
                var expected = review.Effect["generated"].EnumerateArray().Select(x => x.GetString()!).ToArray();
                var changed = Keys(data["observedChanges"]);
                Set(plan.Target.Release == "14sp1" ? "observedChanges" : "generatedObjects", expected, plan.Target.Release == "14sp1" ? changed : Keys(data["generatedObjects"]));
                if (changed == null || changed.Except(expected, StringComparer.OrdinalIgnoreCase).Any()) verdict.Different("observedChanges", Strings(expected), Strings(changed ?? Array.Empty<string>()));
                Set("objectsAfter", facts.Objects(software).Concat(expected), Keys(data["objectsAfter"]));
            }
            else
            {
                Set("sourcesRemoved", new[] { source }, review.Effect.TryGetValue("sourcesRemoved", out var removed) ? Names(JsonNode.Parse(removed.GetRawText())) : null);
                if (!True(data["deleted"])) verdict.Different("deleted", JsonValue.Create(true), data["deleted"]);
                Set("sourcesAfter", facts.Sources(software).Where(s => !string.Equals(s, source, StringComparison.OrdinalIgnoreCase)), Names(data["sourcesAfter"]));
            }
            return verdict;
            void Set(string field, IEnumerable<string> expected, string[]? observed)
            { if (observed == null || !new HashSet<string>(expected, StringComparer.OrdinalIgnoreCase).SetEquals(observed)) verdict.Different(field, Strings(expected), Strings(observed ?? Array.Empty<string>())); }
        }
        private static string? String(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
        private static bool True(JsonNode? node) => node is JsonValue value && value.TryGetValue<bool>(out var result) && result;
        private static bool Hash(string? value) => value != null && Regex.IsMatch(value, "\\A[0-9a-f]{64}\\z", RegexOptions.CultureInvariant);
        private static bool SameFile(string? first, string? second) => first != null && second != null && string.Equals(first.Replace('\\', '/'), second.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase);
        private static JsonArray Strings(IEnumerable<string> strings) => new JsonArray(strings.OrderBy(s => s, StringComparer.Ordinal).Select(s => (JsonNode?)JsonValue.Create(s)).ToArray());
        private static string[]? Names(JsonNode? node)
        {
            if (node is not JsonArray array || array.Any(n => n is not JsonValue || !n.AsValue().TryGetValue<string>(out _))) return null;
            var names = array.Select(n => (string)n!).ToArray();
            return names.Distinct(StringComparer.OrdinalIgnoreCase).Count() == names.Length ? names : null;
        }
        private static string[]? Keys(JsonNode? node)
        {
            if (node is not JsonArray array || array.Any(n => n is not JsonObject || String(n["kind"]) is not ("block" or "type") || string.IsNullOrEmpty(String(n["path"])))) return null;
            var keys = array.Select(n => (string)n!["kind"]! + ":" + (string)n["path"]!).ToArray();
            return keys.Distinct(StringComparer.OrdinalIgnoreCase).Count() == keys.Length ? keys : null;
        }
    }
}
