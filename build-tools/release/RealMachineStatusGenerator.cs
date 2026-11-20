using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using TiaMcp.Adapters.Contracts;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcp.ReleaseTool;

internal static class RealMachineStatusGenerator
{
    internal const string Manifest = "manifest/real-machine-status.json";
    internal const string Roster = "manifest/version-tools.json";
    internal const string Page = "docs/reference/real-machine-status.md";
    internal const string Begin = "<!-- generated:real-machine-status:begin -->";
    internal const string End = "<!-- generated:real-machine-status:end -->";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Strict)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true, NewLine = "\n",
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    internal sealed record Result(string Tool, string Status, string Severity, string? Code = null)
    {
        internal bool Ran => Status is "failed" or "known" or "passed" or "refused-as-designed";
        internal int Rank => !Ran ? 0 : (Status, Severity) switch
        {
            ("failed" or "known", "HIGH") => 7,
            ("failed" or "known", "MEDIUM") => 6,
            ("failed" or "known", "LOW") => 5,
            (_, "LOW") => 4,
            ("refused-as-designed", _) => 3,
            _ => 2
        };
        internal string Symbol => Rank switch { >= 6 => "❌", >= 4 => "⚠", 3 => "⛔", 2 => "✅", _ => "·" };
    }

    // One row per tool in each candidate/release. An empty run still dates the candidate.
    internal sealed record Run(string Release, string Commit, string Package, string Date, Result[] Records);
    internal sealed record Cell(Run Run, Result Result);
    internal sealed record Summary(string Release, int Registered, int Siemens, int Recorded, int Passed, int Failed, Run? Latest);

    internal sealed class Table(Dictionary<string, HashSet<string>> roster, Run[] runs)
    {
        internal readonly Dictionary<string, HashSet<string>> Roster = roster;
        internal readonly Run[] Runs = runs;
        // A late retry of an old binary must not make it a newer candidate.
        private readonly Dictionary<string, string> first = runs.GroupBy(r => r.Commit)
            .ToDictionary(g => g.Key, g => g.Select(r => r.Date).Min(StringComparer.Ordinal)!);

        private IOrderedEnumerable<Run> Candidates(string release) => Runs.Where(r => r.Release == release)
            .OrderByDescending(r => first[r.Commit], StringComparer.Ordinal).ThenBy(r => r.Commit, StringComparer.Ordinal);

        internal Dictionary<string, Cell> Cells(string release) => Candidates(release)
            .SelectMany(run => run.Records.Select(row => new Cell(run, row))).GroupBy(c => c.Result.Tool)
            .ToDictionary(g => g.Key, g => g.FirstOrDefault(c => c.Result.Ran) ?? g.First());

        internal Summary Summarize(string release)
        {
            var rows = Cells(release).Values.Select(c => c.Result).ToArray();
            var tools = Roster[release];
            return new(release, tools.Count, tools.Count(t => ToolExecution.Table.TryGetValue(t, out var owner) && owner is "foundation" or "worker"),
                rows.Count(r => r.Ran), rows.Count(r => r.Symbol == "✅"), rows.Count(r => r.Status is "failed" or "known"),
                Candidates(release).FirstOrDefault());
        }
    }

    internal static string Label(string release) => release == "14sp1" ? "V14 SP1" : "V" + release;
    private static bool Matches(string value, string pattern) => Regex.IsMatch(value, @"\A(?:" + pattern + @")\z", RegexOptions.CultureInvariant);
    private static string? Field(JsonElement obj, string key) =>
        obj.TryGetProperty(key, out var field) && field.ValueKind == JsonValueKind.String ? field.GetString() : null;

    private static Dictionary<string, HashSet<string>> Rosters(string root)
    {
        var releases = CatalogJson.Read(root, Roster)["releases"]!;
        return CatalogJson.Keys.ToDictionary(k => k, k => CatalogJson.Set(CatalogJson.Strings(releases[k]!["tools"])));
    }

    private static string Release(string? text)
    {
        var key = text?.Trim().TrimStart('V', 'v').Replace(" ", "", StringComparison.Ordinal).ToLowerInvariant();
        return CatalogJson.Keys.Contains(key) ? key! : throw new ReleaseException("Missing or invalid real-machine release.");
    }

    private static string Date(string? text)
    {
        if (text == null || !DateTimeOffset.TryParseExact(text, ["yyyy-MM-dd", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK"],
                CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date))
            throw new ReleaseException("Invalid real-machine run date.");
        return date.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);
    }

    private static void ValidateCandidate(string commit, string package)
    {
        CatalogJson.Require(Matches(commit, "[0-9a-f]{8}") && Matches(package, @"[\w.\-]+"),
            "Invalid real-machine candidate identity.");
    }

    private static Result ReadResult(JsonElement row, string tool, bool unreachable)
    {
        var status = unreachable ? "blocked" : Field(row, "outcome");
        if (status == "pending") status = "not-run";
        var severity = Field(row, "severity")?.ToUpperInvariant();
        if (severity is not ("LOW" or "MEDIUM" or "HIGH")) severity = status is "failed" or "known" ? "MEDIUM" : "None";
        string? code = null;
        if (row.TryGetProperty("error", out var error))
            code = error.ValueKind switch { JsonValueKind.Object => Field(error, "code"), JsonValueKind.String => error.GetString(), _ => null };
        if (code != null && !Matches(code, "[A-Z][A-Z0-9_]*")) code = null;
        CatalogJson.Require(status is "passed" or "failed" or "known" or "refused-as-designed" or "not-run" or "skipped" or "blocked",
            "Invalid real-machine outcome.");
        return new(tool, status!, severity, code);
    }

    // Prefer a different refusal code on ties so a mixed refusal cannot be called universally unsupported.
    private static Result Worst(IEnumerable<Result> rows) => rows.OrderByDescending(r => r.Rank)
        .ThenBy(r => r.Code == "UNSUPPORTED_CAPABILITY").ThenBy(r => r.Code, StringComparer.Ordinal)
        .ThenBy(r => r.Status, StringComparer.Ordinal).ThenBy(r => r.Severity, StringComparer.Ordinal).First();

    private static bool Excluded(string folder) => folder.Equals("beta4-v21", StringComparison.OrdinalIgnoreCase)
        || folder.Equals("beta2", StringComparison.OrdinalIgnoreCase) || folder.StartsWith("beta2-", StringComparison.OrdinalIgnoreCase);

    private static bool Registered(HashSet<string> roster, string release, string tool, SortedSet<string> dropped)
    {
        if (roster.Contains(tool)) return true;
        dropped.Add(release + "/" + (Matches(tool, "[A-Za-z][A-Za-z0-9_]*") ? tool : "(invalid identifier)"));
        return false;
    }

    private static void ReportDropped(SortedSet<string> dropped, TextWriter diagnostics)
    {
        foreach (var item in dropped) diagnostics.WriteLine("Dropped unregistered tool: " + item);
    }

    internal static Table Import(string root, string input)
    {
        CatalogJson.Require(!Path.GetFullPath(input).Split(Path.DirectorySeparatorChar).Any(Excluded), "Excluded real-machine input directory.");
        var roster = Rosters(root);
        var batches = new List<Run>();
        var dropped = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var directory in Directory.EnumerateDirectories(input))
        {
            if (Excluded(Path.GetFileName(directory))) continue;
            foreach (var path in Directory.EnumerateFiles(directory, "results-*.jsonl"))
            {
                var suffix = Path.GetFileNameWithoutExtension(path)["results-".Length..];
                using var metadata = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(directory, $"run-{suffix}.json")));
                var run = metadata.RootElement;
                var commit = Field(run, "commit") ?? "";
                var package = Field(run, "package") ?? "";
                ValidateCandidate(commit, package);
                var date = Date(Field(run, "date"));
                var byRelease = new Dictionary<string, List<Result>>();
                var fallback = Field(run, "release");
                if (fallback != null) byRelease[Release(fallback)] = [];
                foreach (var line in File.ReadLines(path))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    using var document = JsonDocument.Parse(line);
                    var row = document.RootElement;
                    if (row.TryGetProperty("helper", out _)) continue;
                    var release = Release(Field(row, "release") ?? fallback);
                    var tool = Field(row, "tool") ?? "";
                    if (!Registered(roster[release], release, tool, dropped)) continue;
                    if (!byRelease.TryGetValue(release, out var records)) byRelease[release] = records = [];
                    records.Add(ReadResult(row, tool, suffix.EndsWith(".unreachable", StringComparison.OrdinalIgnoreCase)));
                }
                batches.AddRange(byRelease.Select(pair => new Run(pair.Key, commit, package, date, pair.Value.ToArray())));
            }
        }
        ReportDropped(dropped, Console.Error);
        CatalogJson.Require(batches.Count > 0, "No paired real-machine runs found.");
        var runs = batches.GroupBy(r => (r.Release, r.Commit)).Select(group =>
        {
            var first = group.OrderBy(r => r.Date, StringComparer.Ordinal).First();
            CatalogJson.Require(group.All(r => r.Package == first.Package), "Conflicting packages for one candidate.");
            var records = group.SelectMany(r => r.Records).GroupBy(r => r.Tool).Select(Worst);
            return first with { Records = records.OrderBy(r => r.Tool, StringComparer.Ordinal).ToArray() };
        }).OrderBy(r => r.Release, StringComparer.Ordinal).ThenBy(r => r.Date, StringComparer.Ordinal).ThenBy(r => r.Commit, StringComparer.Ordinal);
        return new(roster, runs.ToArray());
    }

    internal static Table Read(string root, TextWriter? diagnostics = null, bool checkManifest = false)
    {
        var roster = Rosters(root);
        var bytes = File.ReadAllBytes(Path.Combine(root, Manifest));
        var runs = JsonSerializer.Deserialize<Run[]>(bytes, Json)
            ?? throw new ReleaseException("Missing real-machine records.");
        // -Check compares the original bytes before applying today's registration roster.
        if (checkManifest)
            CatalogJson.Require(bytes[^1] == '\n' && bytes.AsSpan(0, bytes.Length - 1).SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(runs, Json)),
                "Noncanonical real-machine manifest: " + Manifest);
        var dropped = new SortedSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < runs.Length; i++)
        {
            var run = runs[i];
            ValidateCandidate(run.Commit, run.Package);
            CatalogJson.Require(Release(run.Release) == run.Release && Date(run.Date) == run.Date, "Invalid release or date in real-machine manifest.");
            foreach (var row in run.Records)
                CatalogJson.Require(Matches(row.Tool, "[A-Za-z][A-Za-z0-9_]*")
                    && row.Status is "passed" or "failed" or "known" or "refused-as-designed" or "not-run" or "skipped" or "blocked"
                    && row.Severity is "None" or "LOW" or "MEDIUM" or "HIGH" && (row.Code == null || Matches(row.Code, "[A-Z][A-Z0-9_]*"))
                    && !(row.Status is "failed" or "known" && row.Severity == "None"),
                    "Invalid real-machine manifest record.");
            runs[i] = run with { Records = run.Records.Where(r => Registered(roster[run.Release], run.Release, r.Tool, dropped)).ToArray() };
        }
        ReportDropped(dropped, diagnostics ?? Console.Error);
        return new(roster, runs);
    }

    internal static Dictionary<string, string> Generate(string root, string? input, bool checkManifest = false)
    {
        var table = input == null ? Read(root, checkManifest: checkManifest) : Import(root, input);
        var summaries = CatalogJson.Keys.Select(table.Summarize).ToArray();
        var outputs = new Dictionary<string, string>();
        if (input != null) outputs[Manifest] = JsonSerializer.Serialize(table.Runs, Json) + "\n";
        foreach (var name in new[] { "README.md", "README.zh-CN.md" })
        {
            var zh = name == "README.zh-CN.md";
            var recorded = summaries.Sum(s => s.Recorded);
            var registered = summaries.Sum(s => s.Registered);
            var title = zh ? $"### 真机记录覆盖 {recorded}/{registered} 个已注册的工具与版本组合"
                : $"### Real-machine records cover {recorded} of {registered} registered tool and release pairs";
            var link = zh ? "查看每个工具的真机记录" : "View the records for each tool";
            var carried = zh ? "部分记录来自较早的候选版本，在完整表中以 ᵒ 标出。"
                : "Some cells use records from an older candidate, marked ᵒ in the full table.";
            var block = $"{title}\n\n{SummaryTable(summaries, zh)}\n\n{carried}\n\n[{link}]({Page})";
            outputs[name] = ToolCountGenerator.Replace(CatalogJson.Source(root, name), block, name, Begin, End);
        }
        outputs[Page] = Detail(table, summaries);
        return outputs;
    }

    private static string SummaryTable(Summary[] summaries, bool zh)
    {
        var lines = new List<string> { zh
            ? "| TIA 版本 | 注册工具 | 调用西门子 API | 有实机记录 | 全部成功 | 有失败 | 最近候选提交 | 运行日期（UTC） |"
            : "| TIA release | Registered | Calling the Siemens API | With records | All succeeded | With failures | Last candidate | Run date (UTC) |",
            "|---|---:|---:|---:|---:|---:|---|---|" };
        foreach (var s in summaries)
            lines.Add($"| {Label(s.Release)} | {s.Registered} | {s.Siemens} | {s.Recorded} | {s.Passed} | {s.Failed} | "
                + $"{s.Latest?.Commit ?? "—"} | {s.Latest?.Date[..10] ?? "—"} |");
        return string.Join('\n', lines);
    }

    private static string Detail(Table table, Summary[] summaries)
    {
        var keys = CatalogJson.Keys;
        var cells = keys.ToDictionary(k => k, table.Cells);
        var lines = new List<string> { "# Real-machine tool status", "",
            "| Symbol | Meaning |", "|---|---|", "| ✅ | Passed |", "| ⛔ | Refused by design |", "| ⚠ | LOW finding |",
            "| ❌ | HIGH or MEDIUM failure |", "| · | Not run, skipped or blocked |", "| — | Not registered |", "| ᵒ | Older candidate |", "",
            "Each cell uses the worst result on the newest candidate with an executed call. The combined column shows the worst recorded release.",
            "Candidate order uses the first run of each commit. A superscript ᵒ marks evidence carried forward from an earlier commit.", "",
            "## Release summary", "", SummaryTable(summaries, false), "",
            "The API count includes only explicit foundation and worker entries in ToolExecution. Tools without an entry are excluded from that count.",
            "With records counts tools with an executed call.",
            "All succeeded counts clean passes. With failures counts failed and known issues, including LOW failures.", "",
            "## Tools", "", "| Tool | Type | " + string.Join(" | ", keys.Select(Label)) + " | Combined |",
            "|---|---|" + string.Concat(keys.Select(_ => "---|")) + "---|" };
        var refused = new List<string>();
        foreach (var tool in table.Roster.Values.SelectMany(x => x).Distinct().Order(StringComparer.Ordinal))
        {
            var values = new List<string>();
            var results = new List<Result>();
            var registered = keys.Where(k => table.Roster[k].Contains(tool)).ToArray();
            foreach (var summary in summaries)
            {
                if (cells[summary.Release].TryGetValue(tool, out var cell))
                {
                    results.Add(cell.Result);
                    values.Add(cell.Result.Symbol + (cell.Run.Commit == summary.Latest?.Commit ? "" : "ᵒ"));
                }
                else values.Add(table.Roster[summary.Release].Contains(tool) ? "·" : "—");
            }
            var type = ToolTaxonomy.OperationOf(tool, null).Operation;
            lines.Add($"| {tool} | {type} | {string.Join(" | ", values)} | {(results.Count == 0 ? "·" : Worst(results).Symbol)} |");
            if (results.Count == registered.Length && results.All(r => r.Status == "refused-as-designed" && r.Code == "UNSUPPORTED_CAPABILITY"))
            {
                var available = PortedFamilies.Contains(tool)
                    ? string.Join(", ", registered.Where(k => PortedFamilies.Available(k, tool)).Select(Label)) : "No ported family";
                refused.Add($"| {tool} | {string.Join(", ", registered.Select(Label))} | {(available.Length == 0 ? "None" : available)} |");
            }
        }
        lines.AddRange(["", "## Always refused in the recorded runs", "",
            "Every registered release must have a refusal with UNSUPPORTED_CAPABILITY. Family availability describes the declared port for that release.", ""]);
        if (refused.Count == 0) lines.Add("No tool meets these conditions.");
        else { lines.AddRange(["| Tool | Releases with refusals | Available in PortedFamilies |", "|---|---|---|"]); lines.AddRange(refused); }
        return string.Join('\n', lines) + "\n";
    }
}
