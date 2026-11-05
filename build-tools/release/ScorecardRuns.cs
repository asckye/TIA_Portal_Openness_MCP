using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace TiaMcp.ReleaseTool;

internal sealed record ScorecardRun(string Directory, string Release, string Commit, string Package, DateTime Date);
internal sealed record ScorecardRunData(Dictionary<string, string> Tools, int Failed, int Typed, int Native,
    int Unknown, int ServerLog, int Recovery, bool Envelopes, string? ErrorProblem = null);
internal sealed record ScorecardStale(int Rows, int Tools, DateTime First, DateTime Last);

internal static class ScorecardRuns
{
    private static readonly string[] Dimensions =
        ["Real-machine coverage", "Delivery quality", "Defect escape and regression", "Error quality"];

    internal static string Tool(JsonNode row)
    {
        string name = Scorecard.Text(row["tool"]);
        return name == "CallTool" ? row["arguments"] is JsonObject arguments ? Scorecard.Text(arguments["name"]) : ""
            : name.StartsWith("CallTool:", StringComparison.Ordinal) ? name[9..] : name;
    }

    internal static ScorecardRunData Envelopes(IEnumerable<string> lines, ISet<string> roster)
    {
        var history = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        int failed = 0, typed = 0, native = 0, unknown = 0, serverLog = 0, recovery = 0;
        string? errorProblem = null;
        foreach (string line in lines.Where(line => !string.IsNullOrWhiteSpace(line)))
        {
            var row = JsonNode.Parse(line) ?? throw new IOException("Empty result row");
            string name = Tool(row);
            bool isRecovery = name.Contains("(recovery)", StringComparison.OrdinalIgnoreCase);
            if (isRecovery) recovery++;
            string state = Scorecard.Text(row["outcome"]);
            if (roster.Contains(name))
            {
                if (!history.TryGetValue(name, out var states)) history[name] = states = [];
                if (state is "passed" or "failed" or "refused-as-designed") states.Add(state);
            }
            if (state != "failed" || !roster.Contains(name) && !isRecovery) continue;
            failed++;
            var error = row["error"] ?? row["response"]?["error"];
            var evidence = ErrorEnvelope(row["envelope"], error);
            if (!evidence.Complete) errorProblem = "Some serialized envelopes are incomplete; counts describe retained fields only";
            string code = error?["code"]?.ToString() ?? "";
            if (!string.IsNullOrWhiteSpace(code) && code != "INTERNAL_ERROR") typed++;
            if (code == "OUTCOME_UNKNOWN") unknown++;
            if (evidence.Native) native++;
            if (evidence.ServerLog) serverLog++;
        }
        var tools = roster.ToDictionary(name => name, name => Classify(history.GetValueOrDefault(name) ?? []), StringComparer.Ordinal);
        return new(tools, failed, typed, native, unknown, serverLog, recovery, true, errorProblem);
    }

    private static (bool Native, bool ServerLog, bool Complete) ErrorEnvelope(JsonNode? envelope, JsonNode? fallback)
    {
        if (envelope is not JsonValue)
        {
            var error = envelope?["error"] ?? fallback;
            return (error?["details"] is JsonObject details && details.ContainsKey("nativeMessage"),
                error?.ToJsonString().Contains("server log", StringComparison.OrdinalIgnoreCase) == true, true);
        }
        string text = envelope.GetValue<string>();
        var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(text), isFinalBlock: false, state: default);
        bool inError = false, inDetails = false, native = false, serverLog = false, complete = false;
        string property = "";
        try
        {
            // A recorded envelope can end mid-string. Read only complete tokens in its retained JSON prefix.
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.PropertyName)
                {
                    property = reader.GetString()!;
                    if (inDetails && reader.CurrentDepth == 3 && property == "nativeMessage") native = true;
                }
                if (reader.TokenType == JsonTokenType.StartObject)
                {
                    if (reader.CurrentDepth == 1 && property == "error") inError = true;
                    if (inError && reader.CurrentDepth == 2 && property == "details") inDetails = true;
                }
                if (reader.TokenType == JsonTokenType.String && inError)
                {
                    string value = reader.GetString()!;
                    if (value.Contains("server log", StringComparison.OrdinalIgnoreCase)) serverLog = true;
                }
                if (reader.TokenType != JsonTokenType.EndObject) continue;
                if (reader.CurrentDepth == 2) inDetails = false;
                if (reader.CurrentDepth == 1) inError = false;
                if (reader.CurrentDepth == 0) complete = true;
            }
        }
        catch (JsonException) { complete = false; }
        return (native, serverLog, complete);
    }

    internal static string Classify(IReadOnlyList<string> states)
    {
        bool pass = states.Contains("passed"), fail = states.Contains("failed");
        if (pass && fail) return states[^1] == "passed" ? "pass" : "uncertain";
        if (fail) return "defect";
        if (pass) return "pass";
        return states.Count == 0 ? "not-run" : "refused-by-design";
    }

    internal static Dictionary<string, string> Tables(IEnumerable<string> documents, ISet<string> roster)
    {
        var result = roster.ToDictionary(name => name, _ => "not-run", StringComparer.Ordinal);
        var defects = new HashSet<string>(StringComparer.Ordinal);
        foreach (string document in documents)
        {
            int skipLevel = 0;
            foreach (string line in document.Split('\n'))
            {
                var heading = Regex.Match(line.Trim(), @"^(#+)\s+(.+)");
                if (heading.Success)
                {
                    int level = heading.Groups[1].Length;
                    if (skipLevel != 0 && level <= skipLevel) skipLevel = 0;
                    if (Regex.IsMatch(heading.Groups[2].Value,
                        @"^(catalog|example|bugs?|session reset|side effect|general|cross-cutting|not run|deferred|untested|counts?|status)",
                        RegexOptions.IgnoreCase) && skipLevel == 0) skipLevel = level;
                    continue;
                }
                if (skipLevel != 0 || !line.TrimStart().StartsWith('|')) continue;
                var cells = line.Trim().Trim('|').Split('|');
                if (cells.Length < 2) continue;
                string status = cells[1].Trim().Trim('`', '*').Trim();
                string? state = TableState(status);
                if (state == null) continue;
                string names = Regex.Replace(cells[0], @"\([^)]*\)", "").Replace("`", "", StringComparison.Ordinal)
                    .Replace("*", "", StringComparison.Ordinal);
                foreach (Match match in Regex.Matches(names, @"[A-Za-z_][A-Za-z_0-9]*"))
                {
                    string name = match.Value;
                    if (!roster.Contains(name)) continue;
                    if (state == "defect") defects.Add(name);
                    if (state != "not-run" || result[name] == "not-run") result[name] = state;
                }
            }
        }
        foreach (string name in defects) result[name] = "defect";
        return result;
    }

    private static string? TableState(string status)
    {
        foreach (var pair in new[] { ("PASS", "pass"), ("BUG", "defect"), ("EXPECTED-REFUSAL", "refused-by-design"),
            ("EXPECTED REFUSAL", "refused-by-design"), ("INCONCLUSIVE", "uncertain"), ("NOT RUN", "not-run"),
            ("DEFERRED", "not-run"), ("SKIP", "not-run") })
            if (status.StartsWith(pair.Item1, StringComparison.OrdinalIgnoreCase)) return pair.Item2;
        return null;
    }

    internal static List<ScorecardRun> Discover(string directory, List<string> notes)
    {
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException(directory);
        var runs = new List<ScorecardRun>();
        foreach (string folder in Directory.GetDirectories(directory).Order(StringComparer.Ordinal))
        {
            string path = Path.Combine(folder, "run.json");
            try
            {
                var data = Scorecard.Read(path);
                string release = Scorecard.Text(data["release"]), commit = Scorecard.Text(data["commit"]);
                string package = Scorecard.Text(data["package"]);
                if (!CatalogJson.Keys.Contains(release) || commit.Length == 0 || package.Length == 0)
                    throw new IOException("run.json requires release, commit and package");
                var date = DateTime.Parse(Scorecard.Text(data["date"]), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal);
                runs.Add(new(folder, release, commit, package, date));
            }
            catch (Exception ex) { notes.Add("Ignored run folder " + folder + ": " + ex.Message); }
        }
        return runs.OrderByDescending(run => run.Date).ThenBy(run => run.Directory, StringComparer.Ordinal).ToList();
    }

    internal static ScorecardRunData Read(ScorecardRun run, ISet<string> roster)
    {
        string jsonl = Path.Combine(run.Directory, "results.jsonl");
        if (File.Exists(jsonl)) return Envelopes(File.ReadLines(jsonl), roster);
        string[] tables = [.. Directory.GetFiles(run.Directory, "slice-*.md").Order(StringComparer.Ordinal),
            Path.Combine(run.Directory, "lead-probe-results.md"), Path.Combine(run.Directory, "lead-tools.md")];
        tables = tables.Where(File.Exists).ToArray();
        if (tables.Length == 0) throw new IOException("No results.jsonl or result tables: " + run.Directory);
        return new(Tables(tables.Select(File.ReadAllText), roster), 0, 0, 0, 0, 0, 0, false);
    }

    internal static Dictionary<string, ScorecardStale> Stale(string directory)
    {
        var records = new Dictionary<string, List<(string Tool, DateTime Date)>>();
        if (!Directory.Exists(directory)) return [];
        foreach (string path in Directory.EnumerateFiles(directory, "*.json", SearchOption.AllDirectories))
        {
            var data = Scorecard.Read(path);
            var rows = data as JsonArray ?? (data as JsonObject)?["rows"] as JsonArray;
            if (rows == null) continue;
            var header = data as JsonObject;
            string release = Scorecard.Text(header?["release"] ?? header?["summary"]?["release"]);
            if (release.Length == 0)
                release = Regex.Match(Path.GetRelativePath(directory, path), @"(?<!\d)(14sp1|15\.1|16|17|18|19|20|21)(?!\d)").Value;
            if (release.Length == 0 && Regex.IsMatch(Path.GetFileName(path), @"^v14[-.]", RegexOptions.IgnoreCase)) release = "14sp1";
            if (!CatalogJson.Keys.Contains(release)) continue;
            if (!records.TryGetValue(release, out var list)) records[release] = list = [];
            foreach (var row in rows)
            {
                if (row is not JsonObject fields || !fields.ContainsKey("seconds") || string.IsNullOrWhiteSpace(Tool(row))) continue;
                string dateText = Scorecard.Text(row["utc"] ?? header?["date"] ?? header?["utc"]);
                var date = DateTime.TryParse(dateText, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var parsed)
                    ? parsed : File.GetLastWriteTimeUtc(path);
                list.Add((Tool(row), date));
            }
        }
        return records.Where(pair => pair.Value.Count > 0).ToDictionary(pair => pair.Key, pair => new ScorecardStale(
            pair.Value.Count, pair.Value.Select(row => row.Tool).Distinct().Count(),
            pair.Value.Min(row => row.Date), pair.Value.Max(row => row.Date)));
    }

    internal static void Collect(Scorecard context)
    {
        string directory = Path.Combine(context.Primary, "bin-build/vm-test");
        List<ScorecardRun> runs = [];
        Dictionary<string, ScorecardStale> stale = [];
        string? unavailable = null;
        try { runs = Discover(directory, context.Report.Notes); }
        catch (Exception ex) { unavailable = ex.Message; }
        string oldPath = Path.Combine(context.Primary, "bin-build/vm-acceptance/4.0");
        try { stale = Stale(oldPath); }
        catch (Exception ex) { context.Report.Notes.Add("Older acceptance records not measured: " + oldPath + ": " + ex.Message); }
        foreach (string key in CatalogJson.Keys)
        {
            var releases = runs.Where(run => run.Release == key).ToArray();
            if (releases.Length == 0)
            {
                foreach (string dimension in Dimensions)
                {
                    if (stale.TryGetValue(key, out var old))
                        context.Report.Rows.Add(new(dimension, "run-evidence", key, "stale", old.Rows, "rows", null, "unscored", oldPath,
                            $"4.0 dev builds: {old.Rows} rows, {old.Tools} tools touched, {old.First:yyyy-MM-dd}–{old.Last:yyyy-MM-dd}; " +
                            "result rows with tool and seconds; dates fall back to source file UTC; journals excluded"));
                    else context.Report.Missing(dimension, "run-evidence", key, directory,
                        unavailable ?? (runs.Count == 0 ? "No valid run.json records" : "No run for this release"),
                        runs.Count == 0 ? "not measured" : "no evidence");
                }
                continue;
            }
            ScorecardRunData data;
            if (stale.TryGetValue(key, out var historic)) context.Report.Notes.Add(
                $"V{key} older 4.0 dev builds (stale, unscored): {historic.Rows} rows, {historic.Tools} tools, " +
                $"{historic.First:yyyy-MM-dd}–{historic.Last:yyyy-MM-dd}; {oldPath}");
            var run = releases[0];
            string rosterPath = Path.Combine(context.Root, "manifest/contracts/v4/baseline", key + ".json");
            HashSet<string> roster;
            try
            {
                roster = Scorecard.Read(rosterPath)["tools"]!.AsArray()
                    .Select(tool => Scorecard.Text(tool!["name"])).ToHashSet(StringComparer.Ordinal);

            }
            catch (Exception ex)
            {
                foreach (string dimension in Dimensions) context.Report.Missing(dimension, "run-evidence", key,
                    run.Directory + "; " + rosterPath, ex.Message);
                continue;
            }
            var envelopeRun = releases.FirstOrDefault(candidate => File.Exists(Path.Combine(candidate.Directory, "results.jsonl")));
            if (envelopeRun == null) context.Report.Missing("Error quality", "envelopes", key,
                run.Directory, "No run for this release has results.jsonl");
            else context.Report.Capture("Error quality", key, envelopeRun.Directory, () =>
                ErrorQuality(context, envelopeRun, Envelopes(File.ReadLines(Path.Combine(envelopeRun.Directory, "results.jsonl")), roster)),
                "envelopes");
            try { data = Read(run, roster); }
            catch (Exception ex)
            {
                foreach (string dimension in Dimensions.Where(dimension => dimension != "Error quality"))
                    context.Report.Missing(dimension, "run-evidence", key, run.Directory, ex.Message);
                continue;
            }
            Results(context, run, data, roster.Count);
            context.Report.Capture("Defect escape and regression", key, run.Directory, () =>
            {
                if (releases.Length < 2) context.Report.Missing("Defect escape and regression", "regressions", key,
                    run.Directory, "Two runs required");
                else
                {
                    var previous = Read(releases[1], roster);
                    int regressions = data.Tools.Count(pair => pair.Value == "defect" && previous.Tools[pair.Key] == "pass");
                    int fixedCount = data.Tools.Count(pair => pair.Value == "pass" && previous.Tools[pair.Key] is "defect" or "uncertain");
                    context.Report.Add("Defect escape and regression", "regressions", key, regressions, run.Directory,
                        "Previous: " + releases[1].Directory);
                    context.Report.Add("Defect escape and regression", "fixed", key, fixedCount, run.Directory);
                }
                context.Report.Missing("Defect escape and regression", "escape-rate", key, directory, "No pre-release defect records");
                context.Report.Missing("Defect escape and regression", "reappearing-bug-ids", key, directory, "Bug IDs exist only in REPORT.md");
            }, "regressions");
        }
    }

    private static void Results(Scorecard context, ScorecardRun run, ScorecardRunData data, int rosterCount)
    {
        string key = run.Release, source = run.Directory;
        foreach (string state in new[] { "pass", "defect", "refused-by-design", "uncertain", "not-run" })
            context.Report.Add("Real-machine coverage", state, key, data.Tools.Count(pair => pair.Value == state), source);
        int executed = data.Tools.Count(pair => pair.Value != "not-run");
        context.Report.Add("Real-machine coverage", "tools-run-share", key, (double)executed / rosterCount, source,
            $"{executed}/{rosterCount}; {run.Package}; commit {run.Commit}", "share");
        context.Report.Add("Real-machine coverage", "run-age-days", key, (DateTime.UtcNow - run.Date).TotalDays, source, unit: "days");
        context.Report.Capture("Real-machine coverage", key, source, () =>
            context.Report.Add("Real-machine coverage", "commits-since-run", key,
                int.Parse(Scorecard.Git(context.Root, "rev-list", "--count", run.Commit + "..HEAD"), CultureInfo.InvariantCulture), source),
            "commits-since-run");
        string examples = Path.Combine(source, "verified-examples-v" + key + ".json");
        if (File.Exists(examples)) context.Report.Capture("Real-machine coverage", key, examples, () =>
        {
            var json = Scorecard.Read(examples);
            int count = json is JsonArray array ? array.Count : json["tools"] is JsonObject tools
                ? tools.Count : json["examples"]!.AsArray().Count;
            context.Report.Add("Real-machine coverage", "verified-examples", key, count, examples);
        }, "verified-examples");
        int pass = data.Tools.Count(pair => pair.Value == "pass"), defect = data.Tools.Count(pair => pair.Value == "defect");
        int denominator = pass + defect + data.Tools.Count(pair => pair.Value == "uncertain");
        if (denominator == 0) context.Report.Missing("Delivery quality", "delivery-pass-share", key, source, "No pass/defect/uncertain tools");
        else context.Report.Add("Delivery quality", "delivery-pass-share", key, (double)pass / denominator, source,
            $"{pass}/{denominator}", "share");
        context.Report.Add("Delivery quality", "delivery-defects", key, defect, source);
    }

    private static void ErrorQuality(Scorecard context, ScorecardRun run, ScorecardRunData data)
    {
        string key = run.Release, source = Path.Combine(run.Directory, "results.jsonl");
        if (data.ErrorProblem != null) context.Report.Missing("Error quality", "complete-envelopes", key, source, data.ErrorProblem);
        context.Report.Add("Error quality", "failed-rows", key, data.Failed, source);
        context.Report.Add("Error quality", "recovery-rows", key, data.Recovery, source);
        foreach (var (measure, count) in new[] { ("typed-error-share", data.Typed), ("native-message-share", data.Native),
            ("outcome-unknown-share", data.Unknown), ("server-log-share", data.ServerLog) })
        {
            if (data.Failed == 0) context.Report.Missing("Error quality", measure, key, source, "No failed rows to assess");
            else context.Report.Add("Error quality", measure, key, (double)count / data.Failed, source,
                $"{count}/{data.Failed}" + (measure == "native-message-share" ? "; nativeMessage field present (may be null)" : ""), "share");
        }
    }
}
