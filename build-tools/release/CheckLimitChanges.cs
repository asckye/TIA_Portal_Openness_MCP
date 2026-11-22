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

    private sealed record LimitPart(string Name, int Position, List<(int Index, double Value)> Numbers);
    private sealed record Limit(string Member, string Parameters, string Key, LimitPart[] Parts, bool Minimum, int Line);
    private static readonly (string Path, string Member, bool Minimum)[] Locations =
    [
        ("build-tools/release/MergeCheck.cs", "CheckResult", true),
        ("build-tools/release/PlcReleaseChecks.cs", "BuildReleaseCountRules", true),
        ("build-tools/release/PrepareChecks.cs", "RequirePreparedFoundationApproval", true),
        ("build-tools/release/SuiteHang.cs", "Timeout", false),
        ("build-tools/release/PublishPrepared.cs", "RemainingMilliseconds", false)
    ];

    private static IEnumerable<Limit> Limits(string source, CheckChange change)
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
            var location = Locations.FirstOrDefault(row => (row.Path == change.OldPath || row.Path == change.Path) && row.Member == member);
            bool timeout = node is AttributeArgumentSyntax argument && argument.NameEquals?.Name.Identifier.Text == "Timeout" &&
                argument.Ancestors().OfType<AttributeSyntax>().Any(attribute => attribute.Name.ToString().Split('.').Last() is
                    "Fact" or "Theory" or "FactAttribute" or "TheoryAttribute");
            if (location.Path == null && !timeout) continue;
            var methodOwner = node.FirstAncestorOrSelf<MethodDeclarationSyntax>();
            string owner = timeout ? methodOwner?.Identifier.Text ?? "" : member;
            string parameters = methodOwner == null ? "" : CheckLoosening.Tokens(methodOwner.ParameterList);
            string identity = CheckLoosening.ContainingType(node) + "/" + owner + (timeout ? ":timeout" : "");
            // Existing approvals identify the original declaration, including its parameters.
            string key = string.Join(".", node.Ancestors().OfType<TypeDeclarationSyntax>().Reverse().Select(type => type.Identifier.Text))
                + ":" + owner + (timeout ? ":timeout" : parameters);
            yield return new(identity, parameters, key, NumericParts(node),
                location.Minimum, node.GetLocation().GetLineSpan().StartLinePosition.Line + 1);
        }
    }

    private static LimitPart[] NumericParts(SyntaxNode node)
    {
        var parameters = (node as MethodDeclarationSyntax)?.ParameterList.Parameters ?? default;
        LimitPart[] parts = [new("", -1, []), .. parameters.Select((parameter, index) => new LimitPart(parameter.Identifier.Text, index, []))];
        int ordinal = 0;
        foreach (var token in node.DescendantTokens())
        {
            if (!Number(token, out double value)) continue;
            // An optional parameter can add a literal ahead of every body limit. Keep the original ordinal for approvals.
            var parameter = token.Parent?.FirstAncestorOrSelf<ParameterSyntax>();
            int position = parameter == null ? -1 : parameters.IndexOf(parameter);
            parts[position + 1].Numbers.Add((ordinal++, value));
        }
        return parts;
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
        var before = Limits(change.Before!, change).ToArray();
        var pairs = new CheckMemberPairs<Limit>(before, Limits(change.After!, change));
        pairs.MatchBy(row => (row.Member, row.Parameters));
        pairs.MatchBy(row => row.Member, unique: true);
        foreach (var previous in before) Compare(change.Path, previous, pairs.Replacement(previous), findings);
    }

    private static void Compare(string path, Limit previous, Limit? replacement, List<ReviewFinding> findings)
    {
        var parts = new CheckMemberPairs<LimitPart>(previous.Parts, replacement?.Parts ?? []);
        parts.MatchBy(part => part.Name);
        parts.MatchBy(part => part.Position);
        foreach (var part in previous.Parts)
        {
            var current = parts.Replacement(part)?.Numbers;
            for (int i = 0; i < part.Numbers.Count; i++)
            {
                var (index, value) = part.Numbers[i];
                string key = CheckLoosening.Hash(previous.Key) + ":" + index;
                if (current == null || i >= current.Count)
                    findings.Add(new(path, "gate-removed", key, "Numeric check limit removed", previous.Line));
                else if (previous.Minimum ? current[i].Value < value : current[i].Value > value)
                    AddNumber(path, previous.Minimum ? "limit-lowered" : "limit-raised", key, value, current[i].Value,
                        findings, previous.Line, previous.Key);
            }
        }
    }
}
