using System.Text.Json;

namespace TiaMcp.ReleaseTool;

internal sealed record MergeCheckRun(DateTime Utc, string Commit, string Branch, bool All, bool Wide, bool DocsOnly,
    double Seconds, int Exit, string[] Failed, Dictionary<string, double> Jobs, string Output);

internal static class MergeCheckRuns
{
    internal static List<MergeCheckRun> Read(string path) => File.ReadLines(path).Where(line => !string.IsNullOrWhiteSpace(line))
        .Select(line => JsonSerializer.Deserialize<MergeCheckRun>(line, ScorecardReport.Json) ?? throw new IOException("Empty timing record"))
        .ToList();

    internal static void Append(string root, Options options, MergeCheck.Plan plan, IEnumerable<MergeCheck.Timing> timings,
        double seconds, string output, Func<string[], CommandResult> git)
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
            var record = new MergeCheckRun(DateTime.UtcNow, ReadGit("rev-parse", "HEAD"),
                ReadGit("branch", "--show-current"), options.Has("All"), plan.Wide, plan.DocsOnly, seconds,
                rows.Any(row => row.Result.ExitCode != 0) ? 1 : 0, rows.Where(row => row.Result.ExitCode != 0).Select(row => row.Name).ToArray(),
                rows.GroupBy(row => row.Name).ToDictionary(group => group.Key, group => group.Sum(row => row.Seconds)), Path.GetFullPath(output));
            Append(Path.Combine(Scorecard.PrimaryCheckout(root, git), "bin-build/scorecard/merge-check-runs.jsonl"), record);
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
