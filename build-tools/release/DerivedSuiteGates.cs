using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace TiaMcp.ReleaseTool;

// Merge extracts the same-run subset; nightly compares it with the independently executed original.
internal static class DerivedSuiteGates
{
    private static readonly XNamespace Ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
    internal sealed record Case(string Class, string Method, string Display, XElement Result, XElement Definition);

    internal static string Normalize(string cls, string display) => cls == "FoundationChecks"
        ? Regex.Replace(display, @"(?<=FoundationChecks\.Check\(row: [A-Za-z0-9_]+ )#[0-9]+(?=: )", "#") : display;

    // These sixteen SDK-only XSD assertions are extra rows produced inside FoundationChecks, rather than an API class.
    // The SDK-less original never produced them. All other rows, including its skipped DeclarationXmlFormatTests, remain.
    internal static bool FoundationSdkOnly(Case row) => row.Class == "FoundationChecks" && row.Method == "Check" &&
        Regex.IsMatch(Normalize(row.Class, row.Display), @"\AFoundationChecks\.Check\(row: OfflineXmlBuilderTests #: (14sp1|15\.1|16|17|18|19|20|21) (UDT|GlobalDB) official interface XSD: \)\z");

    internal static Case[] Select(string name, IEnumerable<Case> rows, JsonObject suite) =>
        rows.Where(row => Matches(row, suite) && (name != "foundation" || !FoundationSdkOnly(row))).ToArray();

    internal static Case[] Read(string path)
    {
        var root = XDocument.Load(path).Root ?? throw new ReleaseException("TRX has no root: " + path);
        var definitions = root.Element(Ns + "TestDefinitions")!.Elements(Ns + "UnitTest")
            .ToDictionary(test => (string)test.Attribute("id")!, StringComparer.Ordinal);
        return root.Element(Ns + "Results")!.Elements(Ns + "UnitTestResult").Select(row =>
        {
            var definition = definitions[(string)row.Attribute("testId")!];
            var method = definition.Element(Ns + "TestMethod")!;
            return new Case((string)method.Attribute("className")!, (string)method.Attribute("name")!,
                (string)row.Attribute("testName")!, row, definition);
        }).ToArray();
    }

    private sealed record Literal(string Text, bool Positive);

    private static Literal[][] Filter(JsonObject suite)
    {
        var arguments = suite["arguments"]!.AsArray().Select(arg => arg!.GetValue<string>()).ToArray();
        string? filter = null;
        for (int index = 0; index < arguments.Length; index++)
        {
            if (arguments[index].StartsWith("-p:", StringComparison.Ordinal)) continue;
            if (arguments[index] != "--filter" || filter != null || index + 1 == arguments.Length)
                throw new ReleaseException("Unsupported derivation argument: " + arguments[index]);
            filter = arguments[++index];
        }
        if (filter == null) return [[]];
        return filter.Split('|').Select(term => term.Split('&').Select(predicate =>
        {
            var match = Regex.Match(predicate, @"\AFullyQualifiedName(!?~)([A-Za-z0-9_.]+)\z");
            if (!match.Success) throw new ReleaseException("Unsupported derived suite filter: " + predicate);
            return new Literal(match.Groups[2].Value, match.Groups[1].Value == "~");
        }).ToArray()).ToArray();
    }

    // A sufficient implication proof over all possible fully-qualified names. Unknown/ambiguous expressions fall back
    // to an independent process; observed superset rows cannot prove that its filter did not hide another class.
    internal static bool FilterContains(JsonObject superset, JsonObject subset)
    {
        var source = Filter(superset); var target = Filter(subset);
        return target.All(term => source.Any(candidate => candidate.All(required => term.Any(known =>
            known.Positive == required.Positive && (known.Positive
                ? known.Text.Contains(required.Text, StringComparison.OrdinalIgnoreCase)
                : required.Text.Contains(known.Text, StringComparison.OrdinalIgnoreCase))))));
    }

    // All registered subset filters use this grammar. Refuse unknown syntax rather than silently broadening coverage.
    internal static bool Matches(Case row, JsonObject suite)
    {
        string fqn = row.Class + "." + row.Method;
        return Filter(suite).Any(term => term.All(predicate => fqn.Contains(predicate.Text, StringComparison.OrdinalIgnoreCase) == predicate.Positive));
    }

    internal static Dictionary<string, int> Identities(IEnumerable<Case> rows) => rows
        .GroupBy(row => new JsonArray(row.Class, row.Method, Normalize(row.Class, row.Display)).ToJsonString(), StringComparer.Ordinal)
        .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

    internal static string[] DifferenceClasses(IEnumerable<Case> independent, IEnumerable<Case> derived)
    {
        var expected = Identities(independent); var actual = Identities(derived);
        return expected.Keys.Union(actual.Keys, StringComparer.Ordinal).Where(key => expected.GetValueOrDefault(key) != actual.GetValueOrDefault(key))
            .Select(key => JsonNode.Parse(key)![0]!.GetValue<string>()).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    }

    internal static CommandResult CompareSameRun(string name, JsonObject suite, string independent, string source, string output, string sourceName)
    {
        var result = Evaluate(name, suite, source, output, sourceName);
        var errors = new List<string>();
        if (result.ExitCode != 0) errors.Add(result.StandardError);
        var original = DotnetSuites.EvaluateTrx(independent, (int)suite["minimumPassed"]!, (int)suite["maximumSkipped"]!);
        errors.AddRange(original.Errors);
        var classes = DifferenceClasses(Read(independent), Select(name, Read(source), suite));
        if (classes.Length != 0) errors.Add(name + ": same-run identity multiset differs: " + string.Join(", ", classes));
        var path = Path.Combine(output, name + ".json");
        var summary = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        summary["independentTrxSha256"] = ReleaseRecords.HashFile(independent);
        summary["differenceClasses"] = ReleaseEvidence.Strings(classes);
        summary["errors"] = ReleaseEvidence.Strings(errors);
        File.WriteAllText(path, summary.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
        return result with { ExitCode = errors.Count == 0 ? 0 : 1,
            StandardOutput = result.StandardOutput + $"Same-run identity proof: {name}; independent SHA-256 {summary["independentTrxSha256"]}; source SHA-256 {summary["sourceTrxSha256"]}; difference classes: {string.Join(", ", classes)}\n",
            StandardError = string.Join("\n", errors) };
    }

    internal static CommandResult Evaluate(string name, JsonObject suite, string source, string output, string sourceName)
    {
        Directory.CreateDirectory(output);
        var rows = Select(name, Read(source), suite);
        var errors = new List<string>();
        var document = XDocument.Load(source);
        var root = document.Root!;
        root.Element(Ns + "Results")!.ReplaceWith(new XElement(Ns + "Results", rows.Select(row => new XElement(row.Result))));
        root.Element(Ns + "TestDefinitions")!.ReplaceWith(new XElement(Ns + "TestDefinitions", rows.Select(row => row.Definition)
            .DistinctBy(row => (string)row.Attribute("id")!).Select(row => new XElement(row))));
        var ids = rows.Select(row => (string)row.Result.Attribute("executionId")!).ToHashSet(StringComparer.Ordinal);
        if (root.Element(Ns + "TestEntries") is { } entries)
            entries.Elements().Where(entry => !ids.Contains((string?)entry.Attribute("executionId") ?? "")).Remove();
        int passed = rows.Count(row => (string?)row.Result.Attribute("outcome") == "Passed");
        int failed = rows.Count(row => (string?)row.Result.Attribute("outcome") == "Failed");
        int skipped = rows.Count(row => (string?)row.Result.Attribute("outcome") == "NotExecuted");
        // Recompute the subset summary; a failure outside it belongs to the superset's own independent gate.
        root.Element(Ns + "ResultSummary")!.ReplaceWith(new XElement(Ns + "ResultSummary", new XAttribute("outcome", failed == 0 ? "Completed" : "Failed"),
            new XElement(Ns + "Counters", new XAttribute("total", rows.Length), new XAttribute("executed", passed + failed),
                new XAttribute("passed", passed), new XAttribute("failed", failed), new XAttribute("notExecuted", skipped))));
        var trx = Path.Combine(output, name + ".trx"); document.Save(trx);
        var evaluated = DotnetSuites.EvaluateTrx(trx, (int)suite["minimumPassed"]!, (int)suite["maximumSkipped"]!);
        errors.AddRange(evaluated.Errors);
        var summary = evaluated.Counts;
        summary["suite"] = name; summary["minimumPassed"] = suite["minimumPassed"]!.DeepClone();
        summary["maximumSkipped"] = suite["maximumSkipped"]!.DeepClone();
        summary["derivedFrom"] = sourceName; summary["sourceTrxSha256"] = ReleaseRecords.HashFile(source);
        summary["execution"] = "derived"; summary["releaseEligible"] = false;
        summary["errors"] = ReleaseEvidence.Strings(errors);
        File.WriteAllText(Path.Combine(output, name + ".json"), summary.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
        return new(errors.Count == 0 ? 0 : 1, $"{name}: {passed} passed; {failed} failed; {skipped} skipped (derived from {sourceName})\n", string.Join("\n", errors));
    }

}
