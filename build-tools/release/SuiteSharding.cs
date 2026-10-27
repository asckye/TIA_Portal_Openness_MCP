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
                var identity = new JsonArray((string?)method.Attribute("className"), (string?)method.Attribute("name"), (string?)row.Attribute("testId"), (string?)row.Attribute("testName"),
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
        var errors = new List<string>();
        foreach (var shard in shards) errors.AddRange(SuiteHang.Errors(Path.GetDirectoryName(shard)!, name));
        if (shards.Count == 0 || shards.Any(path => !File.Exists(path)))
        {
            errors.Add(name + ": missing shard TRX");
            Directory.CreateDirectory(output);
            File.WriteAllText(Path.Combine(output, name + ".json"), new JsonObject { ["suite"] = name,
                ["errors"] = new JsonArray(errors.Select(e => (JsonNode?)JsonValue.Create(e)).ToArray()) }.ToJsonString() + "\n");
            throw new ReleaseException(string.Join("\n", errors));
        }
        foreach (var shard in shards) errors.AddRange(DotnetSuites.ReadTrx(shard).Errors.Select(error => Path.GetFileName(shard) + ": " + error));
        var duplicateIdentities = Identities(shards).Where(pair => pair.Value > 1).ToArray();
        if (duplicateIdentities.Length != 0)
            errors.Add(name + ": duplicate test identities across shards: " + string.Join("; ", duplicateIdentities.Select(pair => pair.Key + " x" + pair.Value)));
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

// MeasuredCases counts the cases with timing history. A fresh CI checkout or new worktree has none, and a run that
// was stopped part way records only part of a suite; the caller compares it with the registered minimum before it
// lets the costs set job deadlines.
internal sealed record ShardPlan(string Path, double[] Costs, int MeasuredCases = int.MaxValue, int[]? SensitivePartitions = null);
internal static class SuiteCosts
{
    internal static ShardPlan Prepare(string name, string directory, string historyDirectory, string? baseline, int fallback, bool reserveSensitive = false, int sensitiveShards = 1)
    {
        Directory.CreateDirectory(historyDirectory); Directory.CreateDirectory(directory);
        var history = Path.Combine(historyDirectory, name + ".json");
        var classesPath = Path.Combine(historyDirectory, name + ".classes.json");
        var costs = File.Exists(history) ? JsonSerializer.Deserialize<Dictionary<string, double>>(File.ReadAllText(history))! : new Dictionary<string, double>(StringComparer.Ordinal);
        if (baseline != null && File.Exists(baseline)) ImportTrx([baseline], costs, classesPath);
        var classes = File.Exists(classesPath) ? JsonSerializer.Deserialize<Dictionary<string, ClassCost>>(File.ReadAllText(classesPath))! : new();
        var planned = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var (key, seconds) in costs)
        {
            var cls = ClassName(key);
            if (reserveSensitive && TimeSensitiveTests.ContainsCost(key)) continue;
            // Small classes stay together; expensive classes use measured case
            // costs. Late producers are never executed more than once.
            if (cls != null && !TimeSensitiveTests.Methods.ContainsKey(cls) && classes.TryGetValue(cls, out var measured) && measured.Seconds <= 55)
                planned["C:" + cls] = measured.Seconds;
            else planned[key] = seconds;
        }
        int count = fallback == 1 ? 1 : planned.Count == 0 ? fallback : Math.Clamp((int)Math.Ceiling(planned.Values.Sum() / 55), 1, 32);
        // Keep the original total partition budget when adding a dedicated
        // method lane. At least one ordinary partition remains for unknown cases.
        if (reserveSensitive) count = Math.Max(1, count - Math.Max(0, sensitiveShards - 1));
        var (assignments, totals) = Balance(planned, count);
        var sensitivePartitions = new List<int>();
        if (reserveSensitive)
        {
            if (sensitiveShards < 1) throw new ReleaseException("Invalid sensitive shard count");
            // Partition zero was the 500-second Foundation shard: all process
            // fixtures and legacy producers landed there. Balance within each
            // existing isolation group; never split a late-enumerated producer.
            for (int partition = 0; partition < sensitiveShards; partition++)
            {
                var measured = costs.Where(pair => TimeSensitiveTests.ContainsCost(pair.Key) &&
                    Math.Min(TimeSensitiveTests.SensitivePartition(pair.Key), sensitiveShards - 1) == partition)
                    .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
                double seconds = measured.Values.Sum();
                // An indivisible long producer plus a few cheap cases does not
                // become faster by adding another process just for those cases.
                int partitions = fallback == 1 || seconds - measured.Values.DefaultIfEmpty().Max() <= 55 ? 1
                    : Math.Max(1, Math.Min(measured.Count, (int)Math.Ceiling(seconds / 55)));
                var (sensitiveAssignments, sensitiveCosts) = Balance(measured, partitions);
                assignments.Add("S:partition:" + partition, totals.Length);
                foreach (var pair in sensitiveAssignments) assignments.Add("S:" + pair.Key, totals.Length + pair.Value);
                sensitivePartitions.AddRange(Enumerable.Repeat(partition, partitions));
                totals = [.. totals, .. sensitiveCosts];
            }
        }
        int reservedCount = sensitivePartitions.Count;
        var path = Path.Combine(directory, "shard-plan.tsv");
        File.WriteAllLines(path, assignments.Select(pair => Convert.ToBase64String(Encoding.UTF8.GetBytes(pair.Key)) + "\t" + pair.Value));
        File.WriteAllText(Path.Combine(directory, "shard-plan.json"), JsonSerializer.Serialize(new { source = history, targetSeconds = 55, startupAllowanceSeconds = 5, costs = totals, assignments, measuredClasses = classes,
            exclusiveShard = reserveSensitive ? (int?)(totals.Length - 1) : null,
            exclusiveShards = reserveSensitive ? Enumerable.Range(totals.Length - reservedCount, reservedCount).ToArray() : [], sensitivePartitions,
            timeSensitiveClasses = reserveSensitive ? TimeSensitiveTests.Classes : null,
            timeSensitiveMethods = reserveSensitive ? TimeSensitiveTests.Methods : null }, new JsonSerializerOptions { WriteIndented = true }) + "\n");
        return new(path, totals, costs.Count, sensitivePartitions.ToArray());
    }

    internal sealed record ClassCost(double Seconds, double TrxSeconds, int Cases, DateTime MeasuredUtc);

    private static string? ClassName(string key)
    {
        if (!(key.StartsWith("T:", StringComparison.Ordinal) || key.StartsWith("M:", StringComparison.Ordinal))) return null;
        var end = key.IndexOf('('); var method = end < 0 ? key[2..] : key[2..end];
        var dot = method.LastIndexOf('.'); return dot < 0 ? null : method[..dot];
    }

    internal static void ImportTrx(IEnumerable<string> paths, Dictionary<string, double> costs, string classesPath)
    {
        var groups = new Dictionary<string, (double Seconds, int Cases)>(StringComparer.Ordinal);
        foreach (var trx in paths)
        {
            var doc = XDocument.Load(trx);
            var definitions = doc.Descendants().Where(e => e.Name.LocalName == "UnitTest").ToDictionary(e => (string)e.Attribute("id")!, e => e.Descendants().Single(n => n.Name.LocalName == "TestMethod"));
            foreach (var result in doc.Descendants().Where(e => e.Name.LocalName == "UnitTestResult"))
            {
                var method = definitions[(string)result.Attribute("testId")!]; var cls = (string)method.Attribute("className")!;
                var display = (string)result.Attribute("testName")!;
                bool late = display.Contains("(row: ", StringComparison.Ordinal);
                string key = late ? "M:" + cls + "." + (string)method.Attribute("name")! : "T:" + display;
                double seconds = TimeSpan.TryParse((string?)result.Attribute("duration"), System.Globalization.CultureInfo.InvariantCulture, out var duration) ? duration.TotalSeconds : 0;
                // TRX records verification of produced rows. CostSink includes
                // their producer, so retain that measurement for late theories.
                if (!late) costs[key] = Math.Max(.001, seconds);
                else if (!costs.ContainsKey(key)) costs[key] = 1;
                var prior = groups.GetValueOrDefault(cls); groups[cls] = (prior.Seconds + seconds, prior.Cases + 1);
            }
        }
        var records = groups.ToDictionary(pair => pair.Key, pair => new ClassCost(
            Math.Max(pair.Value.Seconds, costs.Where(cost => ClassName(cost.Key) == pair.Key).Sum(cost => cost.Value)), pair.Value.Seconds, pair.Value.Cases, DateTime.UtcNow), StringComparer.Ordinal);
        Directory.CreateDirectory(Path.GetDirectoryName(classesPath)!);
        File.WriteAllText(classesPath, JsonSerializer.Serialize(records, new JsonSerializerOptions { WriteIndented = true }) + "\n");
        File.AppendAllText(Path.ChangeExtension(classesPath, ".history.jsonl"), JsonSerializer.Serialize(new { atUtc = DateTime.UtcNow, classes = records }) + "\n");
    }

    internal static void ImportReport(string reportDirectory, string historyDirectory)
    {
        foreach (var parent in new[] { "suites", "preflight-suites" })
        {
            var root = Path.Combine(reportDirectory, parent); if (!Directory.Exists(root)) continue;
            foreach (var directory in Directory.EnumerateDirectories(root))
                Record(Path.GetFileName(directory), directory, parent == "preflight-suites" ? Path.Combine(historyDirectory, "preflight") : historyDirectory);
        }
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
        Directory.CreateDirectory(historyDirectory);
        var path = Path.Combine(historyDirectory, name + ".json");
        var costs = File.Exists(path) ? JsonSerializer.Deserialize<Dictionary<string, double>>(File.ReadAllText(path))! : new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(directory, "case-timings.tsv", SearchOption.AllDirectories).Where(p => !p.Contains(Path.DirectorySeparatorChar + "sequential" + Path.DirectorySeparatorChar, StringComparison.Ordinal)))
            foreach (var line in File.ReadLines(file))
            {
                var fields = line.Split('\t'); var key = Encoding.UTF8.GetString(Convert.FromBase64String(fields[0]));
                costs[key] = double.Parse(fields[1], System.Globalization.CultureInfo.InvariantCulture);
            }
        ImportTrx(Directory.EnumerateFiles(directory, "*.trx", SearchOption.AllDirectories).Where(p => !p.Contains(Path.DirectorySeparatorChar + "sequential" + Path.DirectorySeparatorChar, StringComparison.Ordinal)), costs, Path.Combine(historyDirectory, name + ".classes.json"));
        File.WriteAllText(path, JsonSerializer.Serialize(costs) + "\n");
    }
}
