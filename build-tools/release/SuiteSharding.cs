using System.Text.Json.Nodes;
using System.Xml.Linq;
using System.Text.Json;
using System.Text;

namespace TiaMcp.ReleaseTool;

internal static class SuiteSharding
{
    private static readonly XNamespace Ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";

    internal static Dictionary<string, int> Identities(IEnumerable<string> paths, bool outcomes = false)
    {
        var identities = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var path in paths)
        {
            var root = XDocument.Load(path).Root!;
            var methods = (root.Element(Ns + "TestDefinitions")?.Elements(Ns + "UnitTest") ?? []).ToDictionary(
                test => (string)test.Attribute("id")!, test => test.Element(Ns + "TestMethod")!);
            foreach (var row in root.Element(Ns + "Results")?.Elements(Ns + "UnitTestResult") ?? [])
            {
                var method = methods[(string)row.Attribute("testId")!];
                var identity = new JsonArray((string?)method.Attribute("className"), (string?)method.Attribute("name"), (string?)row.Attribute("testName"),
                    outcomes ? (string?)row.Attribute("outcome") : null).ToJsonString();
                identities[identity] = identities.GetValueOrDefault(identity) + 1;
            }
        }
        return identities;
    }

    internal static void Compare(IEnumerable<string> baseline, IEnumerable<string> current, bool outcomes = false)
    {
        var expected = Identities(baseline, outcomes); var actual = Identities(current, outcomes);
        var differences = expected.Keys.Union(actual.Keys).Where(key => expected.GetValueOrDefault(key) != actual.GetValueOrDefault(key)).ToArray();
        if (differences.Length != 0)
            throw new ReleaseException($"Test identity multiset differs ({differences.Length} identities): " + string.Join("; ", differences.Take(6)));
    }

    internal static CommandResult Merge(string name, JsonObject suite, IReadOnlyList<string> shards, string output, string? baseline = null)
    {
        if (shards.Count == 0 || shards.Any(path => !File.Exists(path))) throw new ReleaseException(name + ": missing shard TRX");
        var errors = new List<string>();
        foreach (var shard in shards) errors.AddRange(DotnetSuites.ReadTrx(shard).Errors.Select(error => Path.GetFileName(shard) + ": " + error));
        var docs = shards.Select(XDocument.Load).ToArray();
        var root = new XElement(docs[0].Root!);
        var definitions = new XElement(Ns + "TestDefinitions");
        var results = new XElement(Ns + "Results");
        var ids = new Dictionary<string, XElement>();
        foreach (var doc in docs)
        {
            foreach (var definition in doc.Root!.Element(Ns + "TestDefinitions")?.Elements() ?? [])
            {
                var id = (string)definition.Attribute("id")!;
                // Execution IDs are per run, while test IDs are stable across processes.
                if (!ids.ContainsKey(id)) { ids[id] = definition; definitions.Add(new XElement(definition)); }
            }
            results.Add((doc.Root.Element(Ns + "Results")?.Elements() ?? []).Select(row => new XElement(row)));
        }
        if (root.Element(Ns + "TestDefinitions") is { } oldDefinitions) oldDefinitions.ReplaceWith(definitions); else root.Add(definitions);
        if (root.Element(Ns + "Results") is { } oldResults) oldResults.ReplaceWith(results); else root.Add(results);
        var counters = root.Element(Ns + "ResultSummary")!.Element(Ns + "Counters")!;
        foreach (var attribute in counters.Attributes().ToArray())
            attribute.Value = docs.Sum(doc => (int?)doc.Root!.Element(Ns + "ResultSummary")!.Element(Ns + "Counters")!.Attribute(attribute.Name) ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture);
        root.Element(Ns + "ResultSummary")!.SetAttributeValue("outcome", docs.All(doc => (string?)doc.Root!.Element(Ns + "ResultSummary")!.Attribute("outcome") is "Completed" or "Passed") ? "Completed" : "Failed");
        Directory.CreateDirectory(output);
        var path = Path.Combine(output, name + ".trx");
        new XDocument(root).Save(path);
        var evaluated = DotnetSuites.EvaluateTrx(path, suite["minimumPassed"]!.GetValue<int>(), suite["maximumSkipped"]!.GetValue<int>());
        errors.AddRange(evaluated.Errors);
        bool? identitiesEqual = null, outcomesEqual = null;
        if (baseline != null)
        {
            try { Compare([baseline], [path]); identitiesEqual = true; }
            catch (Exception ex) when (ex is ReleaseException or IOException or System.Xml.XmlException) { identitiesEqual = false; errors.Add(ex.Message); }
            try { Compare([baseline], [path], outcomes: true); outcomesEqual = true; }
            catch (Exception ex) when (ex is ReleaseException or IOException or System.Xml.XmlException) { outcomesEqual = false; errors.Add(ex.Message); }
        }
        var report = evaluated.Counts;
        report["suite"] = name; report["minimumPassed"] = suite["minimumPassed"]!.DeepClone(); report["maximumSkipped"] = suite["maximumSkipped"]!.DeepClone();
        report["shards"] = new JsonArray(shards.Select(p => (JsonNode?)JsonValue.Create(p)).ToArray());
        report["identitiesEqual"] = identitiesEqual; report["outcomesEqual"] = outcomesEqual;
        report["equivalence"] = baseline == null ? "not-measured" : identitiesEqual == true && outcomesEqual == true ? "equal" : "failed";
        report["errors"] = new JsonArray(errors.Select(e => (JsonNode?)JsonValue.Create(e)).ToArray());
        File.WriteAllText(Path.Combine(output, name + ".json"), report.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) + "\n");
        return new(errors.Count == 0 ? 0 : 1, $"{name}: {report["passed"]} passed; {report["failed"]} failed; {report["skipped"]} skipped\n", string.Join("\n", errors));
    }
}

internal sealed record ShardPlan(string Path, double[] Costs);
internal static class SuiteCosts
{
    internal static ShardPlan Prepare(string name, string directory, string historyDirectory, string? baseline, int fallback)
    {
        Directory.CreateDirectory(historyDirectory); Directory.CreateDirectory(directory);
        var history = Path.Combine(historyDirectory, name + ".json");
        var costs = File.Exists(history) ? JsonSerializer.Deserialize<Dictionary<string, double>>(File.ReadAllText(history))! : new Dictionary<string, double>(StringComparer.Ordinal);
        if (costs.Count == 0 && baseline != null && File.Exists(baseline))
        {
            var root = XDocument.Load(baseline).Root!; var ns = root.Name.Namespace;
            var methods = (root.Element(ns + "TestDefinitions")?.Elements(ns + "UnitTest") ?? []).ToDictionary(t => (string)t.Attribute("id")!, t => t.Element(ns + "TestMethod")!);
            foreach (var row in root.Element(ns + "Results")?.Elements(ns + "UnitTestResult") ?? [])
            {
                var method = methods[(string)row.Attribute("testId")!]; var display = (string)row.Attribute("testName")!;
                var key = display.Contains("(row: ", StringComparison.Ordinal) ? "M:" + (string)method.Attribute("className")! + "." + (string)method.Attribute("name")! : "T:" + display;
                costs[key] = costs.GetValueOrDefault(key) + TimeSpan.Parse((string)row.Attribute("duration")!, System.Globalization.CultureInfo.InvariantCulture).TotalSeconds;
            }
            foreach (var key in costs.Keys.Where(key => key.StartsWith("M:", StringComparison.Ordinal)).ToArray()) costs[key] = Math.Max(1, costs[key]);
            File.WriteAllText(history, JsonSerializer.Serialize(costs) + "\n");
        }
        int count = fallback == 1 ? 1 : costs.Count == 0 ? fallback : Math.Clamp((int)Math.Ceiling(costs.Values.Sum() / 55), 1, 32);
        var (assignments, totals) = Balance(costs, count);
        var path = Path.Combine(directory, "shard-plan.tsv");
        File.WriteAllLines(path, assignments.Select(pair => Convert.ToBase64String(Encoding.UTF8.GetBytes(pair.Key)) + "\t" + pair.Value));
        File.WriteAllText(Path.Combine(directory, "shard-plan.json"), JsonSerializer.Serialize(new { source = history, targetSeconds = 55, startupAllowanceSeconds = 5, costs = totals, assignments }, new JsonSerializerOptions { WriteIndented = true }) + "\n");
        return new(path, totals);
    }
    internal static (Dictionary<string, int>, double[]) Balance(IReadOnlyDictionary<string, double> costs, int count)
    {
        if (count < 1 || costs.Values.Any(v => !double.IsFinite(v) || v < 0)) throw new ReleaseException("Invalid shard costs");
        var totals = new double[count]; var assignments = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var pair in costs.OrderByDescending(p => p.Value).ThenBy(p => p.Key, StringComparer.Ordinal))
        {
            int index = Array.IndexOf(totals, totals.Min()); assignments.Add(pair.Key, index); totals[index] += pair.Value;
        }
        return (assignments, totals);
    }
    internal static void Record(string name, string directory, string historyDirectory)
    {
        var path = Path.Combine(historyDirectory, name + ".json");
        var costs = File.Exists(path) ? JsonSerializer.Deserialize<Dictionary<string, double>>(File.ReadAllText(path))! : new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(directory, "case-timings.tsv", SearchOption.AllDirectories).Where(p => !p.Contains(Path.DirectorySeparatorChar + "sequential" + Path.DirectorySeparatorChar, StringComparison.Ordinal)))
            foreach (var line in File.ReadLines(file))
            {
                var fields = line.Split('\t'); var key = Encoding.UTF8.GetString(Convert.FromBase64String(fields[0]));
                costs[key] = double.Parse(fields[1], System.Globalization.CultureInfo.InvariantCulture);
            }
        File.WriteAllText(path, JsonSerializer.Serialize(costs) + "\n");
    }
}
