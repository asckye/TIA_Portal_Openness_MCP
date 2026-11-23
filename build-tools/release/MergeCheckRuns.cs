using System.Text.Json;

namespace TiaMcp.ReleaseTool;

internal sealed record MergeCheckRun(DateTime Utc, string Commit, string Branch, bool All, bool Wide, bool DocsOnly,
    double Seconds, [property: System.Text.Json.Serialization.JsonRequired] int Exit, string[] Failed,
    Dictionary<string, double> Jobs, string Output, string? Tree = null);

internal static class MergeCheckRuns
{
    internal const string RelativePath = "bin-build/scorecard/merge-check-runs.jsonl";
    internal sealed record Revision(string Commit, string Tree);

    internal static Revision? CleanRevision(Func<string[], CommandResult> git)
    {
        var status = git(["status", "--porcelain=v1", "--untracked-files=all"]);
        if (status.ExitCode != 0 || !string.IsNullOrWhiteSpace(status.StandardOutput)) return null;
        var head = git(["rev-parse", "HEAD", "HEAD^{tree}"]);
        var hashes = head.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        return head.ExitCode == 0 && hashes.Length == 2 ? new(hashes[0], hashes[1]) : null;
    }

    internal static MergeCheckRun? RequirePassing(string root, Options options, Func<string[], CommandResult> git)
    {
        if (options.Has("SkipMergeCheck"))
        {
            if (options.Has("BuildOnly")) return null;
            throw new ReleaseException("-SkipMergeCheck requires -BuildOnly.");
        }

        var head = CleanRevision(git);
        var path = Path.Combine(Scorecard.PrimaryCheckout(root, git), RelativePath);
        const string remedy = "Run dotnet run --project build-tools/release -- merge-check -All on the clean candidate commit.";
        try
        {
            if (head != null && File.Exists(path))
            {
                foreach (var line in File.ReadAllLines(path).Reverse())
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    var run = JsonSerializer.Deserialize<MergeCheckRun>(line, ScorecardReport.Json);
                    if (run == null || !run.All || run.Commit != head.Commit || run.Tree != head.Tree) continue;
                    if (run is { Wide: true, DocsOnly: false, Exit: 0, Failed.Length: 0, Jobs.Count: > 0 }) return run;
                    break;
                }
            }
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
        {
            throw new ReleaseException($"Cannot read {path}: {error.Message}. {remedy}");
        }
        throw new ReleaseException($"No passing full gate for {head?.Commit ?? "the current checkout"} in {path}. {remedy}");
    }

    internal static List<MergeCheckRun> Read(string path) => File.ReadLines(path).Where(line => !string.IsNullOrWhiteSpace(line))
        .Select(line => JsonSerializer.Deserialize<MergeCheckRun>(line, ScorecardReport.Json) ?? throw new IOException("Empty timing record"))
        .ToList();

    internal static void Append(string root, Options options, MergeCheck.Plan plan, IEnumerable<MergeCheck.Timing> timings,
        double seconds, string output, Func<string[], CommandResult> git, Revision? started = null)
    {
        try
        {
            var rows = timings.ToArray();
            string ReadGit(params string[] arguments)
            {
                var result = git(arguments);
                ProcessRunner.RequireSuccess(result, "Read merge-check identity");
                return result.StandardOutput.Trim();
            }
            var finished = started == null ? null : CleanRevision(git);
            var record = new MergeCheckRun(DateTime.UtcNow, finished?.Commit ?? ReadGit("rev-parse", "HEAD"),
                ReadGit("branch", "--show-current"), options.Has("All"), plan.Wide, plan.DocsOnly, seconds,
                rows.Any(row => row.Result.ExitCode != 0) ? 1 : 0, rows.Where(row => row.Result.ExitCode != 0).Select(row => row.Name).ToArray(),
                rows.GroupBy(row => row.Name).ToDictionary(group => group.Key, group => group.Sum(row => row.Seconds)),
                Path.GetFullPath(output), finished == started ? finished?.Tree : null);
            Append(Path.Combine(Scorecard.PrimaryCheckout(root, git), RelativePath), record);
        }
        catch (Exception ex) { Console.WriteLine("scorecard: could not append merge-check timing: " + ex.Message); }
    }

    internal static void Append(string path, MergeCheckRun record)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var json = new JsonSerializerOptions(ScorecardReport.Json) { WriteIndented = false };
            using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
            using var writer = new StreamWriter(stream);
            writer.WriteLine(JsonSerializer.Serialize(record, json));
        }
        catch (Exception ex) { Console.WriteLine("scorecard: could not append merge-check timing: " + ex.Message); }
    }
}
