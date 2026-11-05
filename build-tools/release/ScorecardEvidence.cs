using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml;

namespace TiaMcp.ReleaseTool;

internal static class ScorecardEvidence
{
    internal static void WriteSafety(Scorecard context)
    {
        string catalog = Path.Combine(context.Root, "manifest/tools-list.json");
        foreach (string key in CatalogJson.Keys)
        {
            string baseline = Path.Combine(context.Root, "manifest/contracts/v4/baseline", key + ".json");
            context.Report.Capture("Write safety", key, baseline + "; " + catalog, () =>
            {
                var operations = Scorecard.Read(catalog)["tools"]!.AsArray()
                    .ToDictionary(tool => Scorecard.Text(tool!["name"]), tool => Scorecard.Text(tool!["operation"]));
                var tools = Scorecard.Read(baseline)["tools"]!.AsArray().Where(tool =>
                    operations[Scorecard.Text(tool!["name"])] is "WRITE" or "ONLINE-WRITE").ToArray();
                if (tools.Length == 0) throw new IOException("No write tools in contract roster");
                int dry = tools.Count(tool => tool!["inputSchema"]?["properties"]?["dryRun"]?["default"]?.GetValue<bool>() == true);
                int identity = tools.Count(tool => tool!["inputSchema"]?["properties"] is JsonObject properties &&
                    (properties.ContainsKey("expectedProjectFile") || properties.ContainsKey("expectedProject")));
                context.Report.Add("Write safety", "dry-run-share", key, (double)dry / tools.Length, baseline,
                    $"{dry}/{tools.Length} write tools default dryRun=true", "share");
                context.Report.Add("Write safety", "project-identity-share", key, (double)identity / tools.Length, baseline,
                    $"{identity}/{tools.Length} write tools", "share");
            }, "write-contracts");
            string relative = key is "20" or "21" ? $"logs/approval-v{key}-bundle/result.json" : "logs/foundation-approval/result.json";
            string source = Path.Combine(context.Primary, "bin-build/prepared/*", relative);
            context.Report.Capture("Write safety", key, source, () =>
            {
                source = Scorecard.Prepared(context.Primary, relative) ?? throw new FileNotFoundException("No prepared approval record found");
                var gate = Scorecard.Read(source);
                if (Scorecard.Text(gate["status"]) is not ("passed" or "failed")) throw new IOException("Approval checks not completed");
                int passed, expected;
                if (key is "20" or "21")
                {
                    passed = gate["checksPassed"]!.GetValue<int>(); expected = gate["checksExpected"]!.GetValue<int>();
                }
                else
                {
                    var releases = gate["releases"]!.AsObject();
                    passed = releases[key]!["checksPassed"]!.GetValue<int>();
                    expected = gate["checksExpected"]!.GetValue<int>() / releases.Count;
                }
                if (expected <= 0) throw new IOException("No expected approval checks");
                context.Report.Add("Write safety", "approval-checks", key, (double)passed / expected, source,
                    $"{passed}/{expected}; category gate, not per-tool coverage", "share");
            }, "approval-checks");
            context.Report.Missing("Write safety", "rollback", key, baseline, "No rollback contract field (D4)");
        }
    }

    internal static Dictionary<string, int[]> ReleaseResults(string path)
    {
        var result = CatalogJson.Keys.ToDictionary(key => key, _ => new int[3]);
        using var reader = XmlReader.Create(path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit });
        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "UnitTestResult") continue;
            var match = Regex.Match(reader.GetAttribute("testName") ?? "", "release: \"(14sp1|15\\.1|16|17|18|19|20|21)\"");
            if (!match.Success) continue;
            int index = reader.GetAttribute("outcome") switch { "Passed" => 0, "Failed" => 1, _ => 2 };
            result[match.Groups[1].Value][index]++;
        }
        return result;
    }

    internal static void Tests(Scorecard context)
    {
        string catalogPath = Path.Combine(context.Root, "tests/test-suites.json");
        context.Report.Capture("Tests", "", catalogPath + "; " + context.RunDirectory, () =>
        {
            var catalog = DotnetSuites.LoadCatalog(catalogPath, context.Root);
            string Trx(string name) => Path.Combine(context.RunDirectory, "suites", name, name + ".trx");
            int covered = catalog.Count(pair => File.Exists(Trx(pair.Key)) ||
                DerivedSuiteGates.Supersets.TryGetValue(pair.Key, out var parent) && File.Exists(Trx(parent)));
            if (covered == 0) context.Report.Missing("Tests", "suite-coverage", "", context.RunDirectory, "No registered suite TRX files");
            else context.Report.Add("Tests", "suite-coverage", "", (double)covered / catalog.Count, context.RunDirectory,
                $"{covered}/{catalog.Count} registered suites represented by TRX; supersets count once in totals", "share");
            if (covered < catalog.Count) context.Report.Missing("Tests", "missing-suite-trx", "", context.RunDirectory,
                $"{catalog.Count - covered} registered suites lack TRX");
            int passed = 0, failed = 0, skipped = 0, total = 0;
            var margins = new List<int>();
            var byRelease = CatalogJson.Keys.ToDictionary(key => key, _ => new int[3]);
            foreach (var (name, suite) in catalog)
            {
                if (DerivedSuiteGates.Supersets.TryGetValue(name, out var parent) && File.Exists(Trx(parent))) continue;
                string path = Trx(name);
                if (!File.Exists(path)) continue;
                var (counts, errors) = DotnetSuites.ReadTrx(path);
                int Count(string key) => counts[key]!.GetValue<int>();
                passed += Count("passed"); failed += Count("failed"); skipped += Count("skipped"); total += Count("total");
                margins.Add(Count("passed") - suite!["minimumPassed"]!.GetValue<int>());
                context.Report.Add("Tests", "suite-margin:" + name, "", margins[^1], path,
                    string.Join("; ", errors), "tests");
                var timings = context.Timings.LastOrDefault(row => row.Output == context.RunDirectory)?.Jobs;
                if (timings != null && (timings.TryGetValue("suite-" + name, out double seconds) || timings.TryGetValue(name, out seconds)))
                    context.Report.Add("Tests", "suite-seconds:" + name, "", seconds, path, unit: "seconds");
                else context.Report.Missing("Tests", "suite-seconds:" + name, "", path, "No timing record for this suite");
                foreach (var (key, values) in ReleaseResults(path))
                    for (int index = 0; index < 3; index++) byRelease[key][index] += values[index];
            }
            if (margins.Count > 0)
            {
                context.Report.Add("Tests", "passed-tests", "", passed, context.RunDirectory);
                context.Report.Add("Tests", "failed-tests", "", failed, context.RunDirectory);
                context.Report.Add("Tests", "skipped-share", "", total == 0 ? 0 : (double)skipped / total, context.RunDirectory, unit: "share");
                context.Report.Add("Tests", "minimum-margin", "", margins.Min(), context.RunDirectory);
            }
            foreach (var (key, counts) in byRelease)
            {
                if (counts.Sum() == 0) context.Report.Missing("Tests", "release-results", key, context.RunDirectory, "No release-tagged results");
                else context.Report.Add("Tests", "release-results", key, counts.Sum(), context.RunDirectory,
                    $"passed {counts[0]}, failed {counts[1]}, skipped {counts[2]}; unscored");
            }
        }, "suite-results");
        foreach (string key in CatalogJson.Keys)
            if (!context.Report.Rows.Any(row => row.Dimension == "Tests" && row.Release == key))
                context.Report.Missing("Tests", "release-results", key, context.RunDirectory, "Suite source unavailable");
    }

    internal static bool SecurityRule(string rule)
    {
        if (!rule.StartsWith("CA", StringComparison.Ordinal) || !int.TryParse(rule[2..], out int code)) return false;
        return code is 2100 or >= 2300 and <= 2330 or >= 3001 and <= 3012 or 3075 or 3077 or >= 5350 and <= 5403;
    }

    internal static void Security(Scorecard context)
    {
        string baseline = Path.Combine(context.Root, AnalyzerWarnings.BaselinePath);
        context.Report.Capture("Security", "", baseline, () =>
        {
            var data = Scorecard.Read(baseline);
            if (data["initializedRules"] is not JsonArray) throw new IOException("initializedRules is absent; security baseline not initialized");
            int count = data["entries"]!.AsArray().Count(row => SecurityRule(Scorecard.Text(row!["rule"])));
            context.Report.Add("Security", "security-warnings", "", count, baseline, "Reviewed baseline entries");
        }, "security-warnings");
        string source = Path.Combine(context.Primary, "bin-build/prepared/*/logs/vulnerable-packages.json");
        context.Report.Capture("Security", "", source, () =>
        {
            source = Scorecard.Prepared(context.Primary, "logs/vulnerable-packages.json") ??
                throw new FileNotFoundException("No prepared vulnerability scan found");
            var data = Scorecard.Read(source);
            if (Scorecard.Text(data["status"]) is not ("passed" or "failed")) throw new IOException("Vulnerability scan not completed");
            var findings = data["findings"]!.AsArray();
            int Count(params string[] kinds) => findings.Count(row => kinds.Contains(Scorecard.Text(row!["kind"])));
            context.Report.Add("Security", "high-critical", "", Count("vulnerability-high", "vulnerability-critical"), source);
            context.Report.Add("Security", "low-moderate", "", Count("vulnerability-low", "vulnerability-moderate"), source);
            context.Report.Add("Security", "scan-age-days", "", (DateTime.UtcNow - File.GetLastWriteTimeUtc(source)).TotalDays, source, unit: "days");
        }, "vulnerability-scan");
    }

    internal static void Documentation(Scorecard context, Func<(int Count, List<string> Errors)>? repository = null,
        Func<int>? deadReferences = null)
    {
        context.Report.Capture("Documentation", "", context.Root, () =>
        {
            var (count, errors) = (repository ?? (() => RepositoryChecks.Check(context.Root, true)))();
            context.Report.Add("Documentation", "repository-errors", "", errors.Count, context.Root,
                $"{count} checks; " + string.Join("; ", errors));
            int dead = (deadReferences ?? (() => DeadToolReferences.Run(context.Root, false)))();
            if (dead is not (0 or 1)) throw new IOException("Dead reference check unavailable, exit " + dead);
            context.Report.Add("Documentation", "dead-references", "", dead, context.Root, dead == 0 ? "pass" : "fail");
            var paths = context.ReadGit(["ls-files", "-z", "--", ":(glob)docs/**/*.md"])
                .Split('\0', StringSplitOptions.RemoveEmptyEntries);
            int outdated = 0, undated = 0;
            foreach (string path in paths)
            {
                string source = File.ReadAllText(Path.Combine(context.Root, path));
                var match = Regex.Match(source, @"最后核对：\s*(\d{4}-\d{2}-\d{2})");
                if (!match.Success) { undated++; continue; }
                var date = DateTime.ParseExact(match.Groups[1].Value, "yyyy-MM-dd", CultureInfo.InvariantCulture);
                if ((DateTime.Today - date).TotalDays > 30) outdated++;
            }
            if (paths.Length == 0) throw new IOException("No tracked docs/**/*.md");
            context.Report.Add("Documentation", "outdated-docs", "", (double)(outdated + undated) / paths.Length,
                "git ls-files docs/**/*.md", $"{undated} undated, {outdated} older than 30 days, {paths.Length} total", "share");
        });
    }

    internal static double Median(IEnumerable<double> values)
    {
        var ordered = values.Order().ToArray();
        if (ordered.Length == 0) throw new IOException("No samples for median");
        return (ordered[(ordered.Length - 1) / 2] + ordered[ordered.Length / 2]) / 2;
    }

    internal static void Efficiency(Scorecard context)
    {
        string source = Path.Combine(context.Primary, "bin-build/scorecard/merge-check-runs.jsonl");
        var rows = context.Timings.OrderByDescending(row => row.Utc).Take(20).ToArray();
        foreach (bool wide in new[] { false, true })
        {
            var group = rows.Where(row => !row.DocsOnly && row.Wide == wide).ToArray();
            string name = wide ? "wide-seconds" : "narrow-seconds";
            if (group.Length < 5) context.Report.Missing("Verification efficiency", name, "", source, $"{group.Length} runs; at least 5 required");
            else context.Report.Add("Verification efficiency", name, "", Median(group.Select(row => row.Seconds)), source,
                $"{group.Length} of last 20 runs", "seconds");
        }
        if (rows.Length < 5) context.Report.Missing("Verification efficiency", "first-pass-rate", "", source, "Fewer than 5 runs");
        else
        {
            var names = rows.Select(row => row.Branch).ToHashSet(StringComparer.Ordinal);
            var branches = context.Timings.Where(row => names.Contains(row.Branch))
                .GroupBy(row => row.Branch).Select(group => group.MinBy(row => row.Utc)!).ToArray();
            context.Report.Add("Verification efficiency", "first-pass-rate", "", (double)branches.Count(row => row.Exit == 0) / branches.Length,
                source, "First recorded run of branches in last 20; " +
                    string.Join(", ", branches.Select(row => row.Branch + ": " + row.Exit)), "share");
        }
    }

    internal static void Performance(Scorecard context)
    {
        string source = Path.Combine(context.RunDirectory, "transport-plc/call-latency.json");
        foreach (string key in CatalogJson.Keys)
        foreach (string transport in new[] { "http", "stdio" })
        {
            context.Report.Capture("Performance trend", key, source, () =>
            {
                var data = Scorecard.Read(source);
                string label = $"transport/{key}/{transport}/full";
                var row = data["samples"]?[label];
                if (row == null) throw new IOException("Missing latency key: " + label);
                double median = row["medianMilliseconds"]!.GetValue<double>();
                context.Report.Add("Performance trend", transport, key, median, source, "Synthetic fixture worker; no TIA", "ms");
            }, transport);
        }
    }
}
