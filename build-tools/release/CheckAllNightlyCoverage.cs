using System.Text.Json;
using System.Text.Json.Nodes;

namespace TiaMcp.ReleaseTool;

internal sealed record NightlyJobPass(string Name, string IdentityHash, ReleaseArtifact[] Files);
internal sealed record NightlyJobIndex(string Schema, string RunId, string Machine, NightlyJobPass[] Jobs);

internal sealed class CheckAllNightlyCoverage
{
    internal const string Schema = "tia-mcp/nightly-job-identities/1";
    private readonly string output, machine;
    private readonly Dictionary<string, NightlyJobPass> recorded = new(StringComparer.Ordinal);
    private readonly Dictionary<string, NightlyJobPass> previous = new(StringComparer.Ordinal);
    internal string? NightlyId { get; private set; }
    internal string Output => output;
    internal bool RecordPasses { get; }
    internal string? NightlyOutput { get; private set; }
    internal DateTime PassedAtUtc { get; private set; }

    internal CheckAllNightlyCoverage(string output, string machine, string? latestPassed, bool recordPasses = true)
    {
        this.output = Path.TrimEndingDirectorySeparator(Path.GetFullPath(output)); this.machine = machine;
        RecordPasses = recordPasses;
        if (latestPassed == null || !File.Exists(latestPassed)) return;
        try
        {
            var pointer = JsonNode.Parse(File.ReadAllText(latestPassed)) as JsonObject ?? throw new IOException("Invalid latest-passed nightly pointer");
            var history = JsonSerializer.Deserialize<NightlyRun[]>(File.ReadAllText(Path.Combine(Path.GetDirectoryName(latestPassed)!, "history.json")));
            if (history == null || history.Any(run => run == null)) return;
            var latest = history?.Where(run => run.Status == "passed").MaxBy(run => run.CompletedUtc);
            if (latest == null || latest.RunId != (string?)pointer["runId"] || latest.ReportPath != (string?)pointer["reportPath"] ||
                latest.ReportSha256 != (string?)pointer["reportSha256"] || latest.EvidenceSha256 != (string?)pointer["evidenceSha256"] ||
                latest.IdentitiesSha256 != (string?)pointer["identitiesSha256"] || !latest.UnderThirtyMinutes ||
                !double.IsFinite(latest.WallSeconds) || latest.WallSeconds < 0 || latest.WallSeconds >= 1800) return;
            var reportPath = Path.GetFullPath((string)pointer["reportPath"]!);
            var directory = Path.GetDirectoryName(reportPath)!;
            var evidencePath = Path.Combine(directory, ReleaseEvidence.FileName);
            var indexPath = Path.Combine(directory, "nightly-identities.json");
            if ((string?)pointer["status"] != "passed" || pointer["underThirtyMinutes"]?.GetValue<bool>() != true ||
                CheckAllResultCache.FileHash(reportPath) != (string?)pointer["reportSha256"] ||
                CheckAllResultCache.FileHash(evidencePath) != (string?)pointer["evidenceSha256"] ||
                CheckAllResultCache.FileHash(indexPath) != (string?)pointer["identitiesSha256"]) return;
            var report = JsonNode.Parse(File.ReadAllText(reportPath)) as JsonObject ?? throw new IOException("Invalid nightly report");
            var evidence = JsonNode.Parse(File.ReadAllText(evidencePath)) as JsonObject ?? throw new IOException("Invalid nightly evidence");
            var index = JsonSerializer.Deserialize<NightlyJobIndex>(File.ReadAllText(indexPath));
            if (!PassedNightly(report, evidence) || index == null || index.Jobs == null || index.Jobs.Any(job => job == null) || index.Schema != Schema || index.Machine != machine ||
                index.RunId != (string?)pointer["runId"] || index.RunId != (string?)evidence["runId"] ||
                index.Jobs.Select(job => job.Name).Distinct(StringComparer.Ordinal).Count() != index.Jobs.Length) return;
            var checks = report["checks"]!.AsArray().OfType<JsonObject>().ToDictionary(row => (string)row["Name"]!, StringComparer.Ordinal);
            foreach (var job in index.Jobs)
                if (!checks.TryGetValue(job.Name, out var row) || (string?)row["Status"] != "passed" || (int?)row["ExitCode"] != 0 ||
                    (string?)row["IdentityHash"] != job.IdentityHash || string.IsNullOrEmpty(job.IdentityHash) || job.IdentityHash.Length != 64 || job.Files == null || job.Files.Length == 0 ||
                    job.Files.Any(file => file == null || string.IsNullOrEmpty(file.Path) || string.IsNullOrEmpty(file.Sha256)) ||
                    !job.Files.Any(file => file.Path == "logs/" + job.Name + ".log") ||
                    job.Files.Select(file => file.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != job.Files.Length) return;
            foreach (var job in index.Jobs) previous.Add(job.Name, job);
            NightlyId = index.RunId; NightlyOutput = directory;
            PassedAtUtc = DateTime.Parse((string)evidence["completedUtc"]!, null, System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime();
        }
        catch (Exception ex) when (InputError(ex)) { previous.Clear(); NightlyId = null; NightlyOutput = null; }
    }

    internal static bool PassedNightly(JsonNode report, JsonNode evidence)
    {
        var mode = evidence["mode"];
        var planned = evidence["planned"]?.AsArray().Select(node => (string)node!).ToArray() ?? [];
        var jobs = evidence["jobs"]?.AsArray().ToArray() ?? [];
        var checks = report["checks"]?.AsArray().ToArray() ?? [];
        if (jobs.Any(job => job is not JsonObject) || checks.Any(check => check is not JsonObject)) return false;
        return (string?)report["profile"] == "nightly" && (int?)report["failed"] == 0 && report["underThirtyMinutes"]?.GetValue<bool>() == true &&
            report["quick"]?.GetValue<bool>() != true && report["buildOnly"]?.GetValue<bool>() != true &&
            (string?)mode?["profile"] == "nightly" && mode?["quick"]?.GetValue<bool>() != true && mode?["affected"]?.GetValue<bool>() != true &&
            (mode?["clean"]?.GetValue<bool>() == true || mode?["cold"]?.GetValue<bool>() == true) && mode?["buildCacheDisabled"]?.GetValue<bool>() == true &&
            mode?["resultCacheEnabled"]?.GetValue<bool>() == false && evidence["source"]?["cleanAtStart"]?.GetValue<bool>() == true &&
            (int?)evidence["result"]?["exitCode"] == 0 && evidence["result"]?["underThirtyMinutes"]?.GetValue<bool>() == true &&
            (int?)evidence["result"]?["failed"] == 0 && (int?)evidence["result"]?["reused"] == 0 && evidence["beginError"] == null &&
            planned.Length > 0 && planned.Distinct(StringComparer.Ordinal).Count() == planned.Length && jobs.Length == planned.Length + 1 &&
            jobs.Select(job => (string)job!["name"]!).Distinct(StringComparer.Ordinal).Count() == jobs.Length &&
            planned.All(name => jobs.Any(job => (string?)job!["name"] == name)) &&
            jobs.All(job => (string?)job!["status"] == "passed" && (int?)job["exitCode"] == 0 && job["reused"]?.GetValue<bool>() == false) &&
            checks.Length == jobs.Length && checks.Select(check => (string)check!["Name"]!).Distinct(StringComparer.Ordinal).Count() == checks.Length &&
            checks.All(check => (string?)check!["Status"] == "passed" && (int?)check["ExitCode"] == 0 &&
                jobs.Any(job => (string?)job!["name"] == (string?)check["Name"]));
    }

    internal CheckAllNightlyBinding Bind(CheckJob job, Func<string> identity, bool reuse, string[] outputs) => new(this, job.Name, identity, reuse, outputs);
    internal NightlyJobPass? Previous(string name, string identity) => previous.TryGetValue(name, out var job) && job.IdentityHash == identity ? job : null;

    internal void Record(string name, string identity, string[] outputs)
    {
        var files = new List<string> { "logs/" + name + ".log" };
        foreach (var relative in outputs)
        {
            var path = Safe(output, relative);
            if (File.Exists(path)) files.Add(relative);
            else if (Directory.Exists(path)) files.AddRange(Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
                .Select(file => Path.GetRelativePath(output, file).Replace('\\', '/')));
            else throw new IOException("Missing nightly output: " + relative);
        }
        var row = new NightlyJobPass(name, identity, files.Distinct(StringComparer.Ordinal).Select(file => new ReleaseArtifact(file,
            CheckAllResultCache.FileHash(Safe(output, file)))).ToArray());
        lock (recorded) recorded[name] = row;
    }

    internal void Save() => File.WriteAllText(Path.Combine(output, "nightly-identities.json"), JsonSerializer.Serialize(
        new NightlyJobIndex(Schema, Path.GetFileName(output), machine, recorded.Values.OrderBy(row => row.Name, StringComparer.Ordinal).ToArray()),
        new JsonSerializerOptions { WriteIndented = true }) + "\n");

    internal static string Safe(string root, string path)
    {
        if (Path.IsPathRooted(path) || path.Replace('\\', '/').Split('/').Any(part => part is ".." or ".")) throw new IOException("Unsafe nightly artifact: " + path);
        var full = Path.GetFullPath(Path.Combine(root, path)); ReleaseCommands.RequireCheckAllOwnedPath(root, full);
        return full;
    }
    internal static bool InputError(Exception ex) => ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or
        ArgumentException or ReleaseException or FormatException or KeyNotFoundException or System.Xml.XmlException;
}

internal sealed class CheckAllNightlyBinding(CheckAllNightlyCoverage owner, string name, Func<string> identity, bool reuse, string[] outputs)
{
    private string? before;
    internal CheckTiming? TryReuse()
    {
        var installed = new List<string>();
        try
        {
            before = identity();
            if (!reuse || owner.Previous(name, before) is not { } pass || owner.NightlyOutput == null) return null;
            foreach (var file in pass.Files)
                if (CheckAllResultCache.FileHash(CheckAllNightlyCoverage.Safe(owner.NightlyOutput, file.Path)) != file.Sha256) return null;
            var staging = Path.Combine(owner.Output, "nightly-replay", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            var copies = new List<(string Staged, string Target)>();
            foreach (var file in pass.Files.Where(file => !file.Path.StartsWith("logs/", StringComparison.Ordinal)))
            {
                var target = CheckAllNightlyCoverage.Safe(owner.Output, file.Path);
                if (File.Exists(target)) return null;
                string staged = Path.Combine(staging, copies.Count + ".stage");
                File.Copy(CheckAllNightlyCoverage.Safe(owner.NightlyOutput, file.Path), staged);
                if (CheckAllResultCache.FileHash(staged) != file.Sha256) return null;
                copies.Add((staged, target));
            }
            if (identity() != before) return null;
            // Install only verified receipts into fresh destinations. A failed
            // replay must leave no stale receipts for the original check to read.
            foreach (var (staged, target) in copies)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Move(staged, target); installed.Add(target);
            }
            return new(name, "passed (reused)", 0, 0, null) { IdentityHash = before, ReusedRunId = owner.NightlyId,
                ReusedAtUtc = owner.PassedAtUtc, EvidencePath = Path.Combine(owner.NightlyOutput, "nightly-identities.json") };
        }
        catch (Exception ex) when (CheckAllNightlyCoverage.InputError(ex))
        {
            foreach (var path in installed) { ReleaseCommands.RequireCheckAllOwnedPath(owner.Output, path); File.Delete(path); }
            before = null; return null;
        }
    }
    internal CheckTiming Record(CheckTiming row)
    {
        if (!owner.RecordPasses || before == null || !CheckTiming.Passed(row)) return row;
        try
        {
            if (identity() != before) return row;
            owner.Record(name, before, outputs);
            return row with { IdentityHash = before };
        }
        catch (Exception ex) when (CheckAllNightlyCoverage.InputError(ex)) { return row; }
    }
}
