using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace TiaMcp.ReleaseTool;

internal static class CheckLimitChanges
{
    internal static readonly HashSet<string> Baselines = [
        "build-tools/release/baselines/analyzer-warnings-baseline.json",
        "build-tools/release/baselines/comment-hygiene-baseline.json", "build-tools/release/baselines/mcp-text-baseline.json",
        "build-tools/release/baselines/response-envelope-baseline.json", "build-tools/release/baselines/swallowed-exceptions-baseline.json"
    ];

    internal static bool FilterNarrows(string before, string after) =>
        after.Length > 0 && after != before &&
        !(before.Length > 0 && (after.Split('|').Contains(before, StringComparer.Ordinal) ||
            before.Split('&').Contains(after, StringComparer.Ordinal)));

    internal static void Scan(CheckChange change, List<ReviewFinding> findings)
    {
        if (change.Before == null || change.After == null) return;
        if (change.Path.EndsWith(".json", StringComparison.Ordinal) && (change.Path == "tests/test-suites.json" ||
            Baselines.Contains(change.Path)))
        {
            var before = JsonNode.Parse(change.Before);
            var after = JsonNode.Parse(change.After);
            if (change.Path == "tests/test-suites.json") SuiteLimits(change.Path, before!.AsObject(), after!.AsObject(), findings);
            Json(change, before, after, "", findings);
            return;
        }
        if (change.Path.EndsWith(".cs", StringComparison.Ordinal)) CSharp(change, findings);
        if (change.Path.StartsWith("scripts/checks/", StringComparison.Ordinal) && change.Path.EndsWith(".py", StringComparison.Ordinal))
            PythonCheckChanges.Limits(change, findings);
    }

    private static void SuiteLimits(string path, JsonObject before, JsonObject after, List<ReviewFinding> findings)
    {
        foreach (var (name, value) in before)
        {
            if (value is not JsonObject oldSuite || after[name] is not JsonObject newSuite) continue;
            var oldPlatforms = oldSuite["platforms"] as JsonObject;
            var newPlatforms = newSuite["platforms"] as JsonObject;
            var platforms = new[] { "" }.Concat(oldPlatforms?.Select(pair => pair.Key) ?? [])
                .Concat(newPlatforms?.Select(pair => pair.Key) ?? []).Distinct();
            foreach (string platform in platforms)
            foreach (string field in new[] { "minimumPassed", "maximumSkipped" })
            {
                JsonNode? Effective(JsonObject suite, JsonObject? overrides) =>
                    platform.Length != 0 && overrides?[platform]?[field] is { } selected ? selected : suite[field];
                var oldValue = Effective(oldSuite, oldPlatforms);
                var newValue = Effective(newSuite, newPlatforms);
                if (oldValue == null || newValue == null) continue;
                double oldNumber = oldValue.GetValue<double>(), newNumber = newValue.GetValue<double>();
                if (field == "minimumPassed" ? newNumber < oldNumber : newNumber > oldNumber)
                    AddNumber(path, field == "minimumPassed" ? "minimum-passed-lowered" : "maximum-skipped-raised",
                        "/" + name + (platform.Length == 0 ? "" : "/platforms/" + platform) + "/" + field, oldNumber, newNumber, findings);
            }
        }
    }

    private static void Json(CheckChange change, JsonNode? before, JsonNode? after, string pointer, List<ReviewFinding> findings)
    {
        if (before is JsonObject oldObject && after is JsonObject newObject)
        {
            foreach (string key in oldObject.Select(pair => pair.Key).Union(newObject.Select(pair => pair.Key)))
            {
                string path = pointer + "/" + key.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);
                if (!oldObject.TryGetPropertyValue(key, out var value))
                {
                    if (pointer == "/counts" && newObject[key] is JsonValue added && added.TryGetValue<double>(out double number) && number > 0)
                        AddNumber(change.Path, "baseline-grown", path, 0, number, findings);
                    continue;
                }
                if (!newObject.TryGetPropertyValue(key, out var replacement))
                {
                    if (change.Path == "tests/test-suites.json" && pointer.Length == 0)
                        findings.Add(new(change.Path, "suite-removed", path + ":" + CheckLoosening.Hash(value!.ToJsonString()), "Registered suite removed"));
                    else if (key == "initializedRules" && value is JsonArray { Count: > 0 })
                        findings.Add(new(change.Path, "initialization-history-removed", path, "Analyzer initialization history removed"));
                    else if (key is "minimumPassed" or "maximumSkipped" && !pointer.Contains("/platforms/", StringComparison.Ordinal))
                        findings.Add(new(change.Path, "gate-removed", path + ":" + value, "Registered gate removed"));
                    continue;
                }
                Json(change, value, replacement, path, findings);
            }
            return;
        }
        if (before is JsonArray oldArguments && after is JsonArray newArguments && change.Path == "tests/test-suites.json" && pointer
                .EndsWith("/arguments", StringComparison.Ordinal))
        {
            string Filter(JsonArray args)
            {
                for (int i = 0; i < args.Count; i++)
                {
                    string value = args[i]?.GetValue<string>() ?? "";
                    if (value is "--filter" or "--TestCaseFilter") return i + 1 < args.Count ? args[i + 1]!.GetValue<string>() : "";
                    if (value.StartsWith("--filter=", StringComparison.Ordinal)) return value[9..];
                }
                return "";
            }
            string oldFilter = Filter(oldArguments), newFilter = Filter(newArguments);
            if (FilterNarrows(oldFilter, newFilter))
                findings
                    .Add(new(change.Path, "test-filter-changed", pointer + ":" + CheckLoosening.Hash(oldFilter + "->" + newFilter), "Suite filter narrowed: "
                            + newFilter));
            return;
        }
        if (pointer == "/initializedRules" && before is JsonArray oldRules && after is JsonArray newRules)
        {
            var retained = newRules.Select(rule => rule!.GetValue<string>()).ToHashSet();
            foreach (string rule in oldRules.Select(rule => rule!.GetValue<string>()).Where(rule => !retained.Contains(rule)))
                findings.Add(new(change.Path, "initialization-history-removed", pointer + ":" + rule, "Analyzer initialization history removed"));
            return;
        }
        if (before is JsonArray oldArray && after is JsonArray newArray && change.Path.Contains("baseline", StringComparison.Ordinal))
        {
            string Identity(JsonNode? node) => node is JsonObject item && item["fingerprint"] is { } fingerprint
                ? fingerprint.GetValue<string>() : node?.ToJsonString() ?? "null";
            var oldCounts = oldArray.GroupBy(Identity).ToDictionary(group => group.Key, group => group.Count());
            foreach (var group in newArray.GroupBy(Identity))
                for (int index = oldCounts.GetValueOrDefault(group.Key); index < group.Count(); index++)
                    findings.Add(new(change.Path, "baseline-grown", pointer + ":" + group.Key + ":" + (index + 1), "Ratchet baseline entry added"));
            return;
        }
        if (before is not JsonValue oldValue || after is not JsonValue newValue ||
            !oldValue.TryGetValue<double>(out double oldNumber) || !newValue.TryGetValue<double>(out double newNumber) || oldNumber == newNumber) return;
        string field = pointer.Split('/').Last();
        string? kind = field switch
        {
            "minimumPassed" when change.Path != "tests/test-suites.json" && newNumber < oldNumber => "minimum-passed-lowered",
            "maximumSkipped" when change.Path != "tests/test-suites.json" && newNumber > oldNumber => "maximum-skipped-raised",
            _ when pointer.StartsWith("/counts/", StringComparison.Ordinal) && newNumber > oldNumber => "baseline-grown",
            _ when change.Path == "tests/test-suites.json" && field is "timeoutSeconds" or "timeoutMilliseconds" && newNumber > oldNumber => "limit-raised",
            _ => null
        };
        if (kind != null) AddNumber(change.Path, kind, pointer, oldNumber, newNumber, findings);
    }

    private static void AddNumber(string path, string kind, string key, double before, double after, List<ReviewFinding> findings,
        int line = 0, string? context = null) =>
        findings.Add(new(path, kind, key + ":" + before.ToString("R", CultureInfo.InvariantCulture) + "->" + after.ToString("R", CultureInfo.InvariantCulture),
            $"Check limit changed: {before.ToString(CultureInfo.InvariantCulture)} -> {after.ToString(CultureInfo.InvariantCulture)}" +
            (context == null ? "" : " (" + context + ")"), line));

    private sealed record Limit(string Key, double[] Values, bool Minimum, int Line);
    private static readonly (string Path, string Member, bool Minimum)[] Locations =
    [
        ("build-tools/release/MergeCheck.cs", "CheckResult", true),
        ("build-tools/release/PlcReleaseChecks.cs", "BuildReleaseCountRules", true),
        ("build-tools/release/PrepareChecks.cs", "RequirePreparedFoundationApproval", true),
        ("build-tools/release/SuiteHang.cs", "Timeout", false),
        ("build-tools/release/PublishPrepared.cs", "RemainingMilliseconds", false)
    ];

    private static IEnumerable<Limit> Limits(string source, string path)
    {
        var syntax = CheckLoosening.Parse(source);
        foreach (var node in syntax.DescendantNodes())
        {
            string member = node switch
            {
                MethodDeclarationSyntax method => method.Identifier.Text,
                PropertyDeclarationSyntax property => property.Identifier.Text,
                VariableDeclaratorSyntax variable when variable.Parent?.Parent is FieldDeclarationSyntax => variable.Identifier.Text,
                _ => ""
            };
            var location = Locations.FirstOrDefault(row => row.Path == path && row.Member == member);
            bool timeout = node is AttributeArgumentSyntax argument && argument.NameEquals?.Name.Identifier.Text == "Timeout" &&
                argument.Ancestors().OfType<AttributeSyntax>().Any(attribute => attribute.Name.ToString().Split('.').Last() is
                    "Fact" or "Theory" or "FactAttribute" or "TheoryAttribute");
            if (location.Path == null && !timeout) continue;
            var numbers = node.DescendantTokens().Where(token => Number(token, out _)).ToArray();
            string owner = timeout ? node.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault()?.Identifier.Text ?? "" : member;
            string key = string.Join(".", node.Ancestors().OfType<TypeDeclarationSyntax>().Reverse().Select(type => type.Identifier.Text))
                + ":" + owner + (node is MethodDeclarationSyntax declaration ? CheckLoosening.Tokens(declaration.ParameterList) : "")
                + (timeout ? ":timeout" : "");
            yield return new(key, numbers.Select(token => { Number(token, out double value); return value; }).ToArray(),
                location.Minimum, node.GetLocation().GetLineSpan().StartLinePosition.Line + 1);
        }
    }

    private static bool Number(SyntaxToken token, out double value)
    {
        value = 0;
        if (token.IsKind(SyntaxKind.NumericLiteralToken)) { value = Convert.ToDouble(token.Value, CultureInfo.InvariantCulture); return true; }
        if (!token.IsKind(SyntaxKind.StringLiteralToken)) return false;
        var match = Regex.Match(token.ValueText, @"\A(?<number>\d+(?:\.\d+)?)(?<unit>ms|s|m|h)?\z");
        if (!match.Success) return false;
        value = double.Parse(match.Groups["number"].Value, CultureInfo.InvariantCulture) * (match.Groups["unit"].Value switch
            { "ms" => 0.001, "m" => 60, "h" => 3600, _ => 1 });
        return true;
    }

    private static void CSharp(CheckChange change, List<ReviewFinding> findings)
    {
        var after = Limits(change.After!, change.Path).GroupBy(row => row.Key).ToDictionary(group => group.Key, group => group.First());
        foreach (var previous in Limits(change.Before!, change.Path).DistinctBy(row => row.Key))
        {
            if (!after.TryGetValue(previous.Key, out var current)) current = previous with { Values = [] };
            for (int index = current.Values.Length; index < previous.Values.Length; index++)
                findings.Add(new(change.Path, "gate-removed", CheckLoosening.Hash(previous.Key) + ":" + index,
                    "Numeric check limit removed", previous.Line));
            for (int index = 0; index < Math.Min(previous.Values.Length, current.Values.Length); index++)
                if (previous.Minimum ? current.Values[index] < previous.Values[index] : current.Values[index] > previous.Values[index])
                    AddNumber(change.Path, previous.Minimum ? "limit-lowered" : "limit-raised", CheckLoosening.Hash(previous.Key) + ":" + index,
                        previous.Values[index], current.Values[index], findings, previous.Line, previous.Key);
        }
    }
}
