using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;

namespace TiaMcp.ReleaseTool;

internal sealed class Scorecard(string root, string primary, string api)
{
    internal string Root { get; } = root;
    internal string Primary { get; } = primary;
    internal string Api { get; } = api;
    internal Func<string[], string> ReadGit { get; set; } = arguments => Git(root, arguments);
    internal ScorecardReport Report { get; } = new();
    internal List<MergeCheckRun> Timings { get; set; } = [];
    internal string RunDirectory { get; set; } = Path.Combine(root, "bin-build/merge-check");
    internal static string Text(JsonNode? node) => node?.GetValue<string>() ?? "";
    internal static JsonNode Read(string path) => JsonNode.Parse(File.ReadAllText(path)) ?? throw new IOException("Empty JSON: " + path);

    internal static string Git(string root, params string[] arguments)
    {
        var result = ReviewChangeSet.Git("git", arguments, root);
        ProcessRunner.RequireSuccess(result, "scorecard git " + string.Join(' ', arguments));
        return result.StandardOutput.Trim();
    }

    internal static string PrimaryCheckout(string root, Func<string[], CommandResult>? git = null)
    {
        try
        {
            string common;
            if (git == null) common = Git(root, "rev-parse", "--path-format=absolute", "--git-common-dir");
            else
            {
                var result = git(["rev-parse", "--path-format=absolute", "--git-common-dir"]);
                ProcessRunner.RequireSuccess(result, "Resolve primary checkout");
                common = result.StandardOutput.Trim();
            }
            if (common.Length == 0) return root;
            return Directory.GetParent(Path.GetFullPath(common, root))!.FullName;
        }
        catch (Exception) { return root; }
    }

    internal static string? Prepared(string primary, string relative, bool delivery = false)
    {
        string directory = Path.Combine(primary, "bin-build/prepared");
        if (!Directory.Exists(directory)) return null;
        return Directory.EnumerateDirectories(directory).SelectMany(path => delivery
                ? Directory.EnumerateDirectories(path, "TIA_MCP_Delivery_*").Select(folder => Path.Combine(folder, relative))
                : new[] { Path.Combine(path, relative) })
            .Where(File.Exists).OrderByDescending(File.GetLastWriteTimeUtc).ThenBy(path => path, StringComparer.Ordinal).FirstOrDefault();
    }

    internal static string? Previous(string primary, string output)
    {
        string directory = Path.Combine(primary, "bin-build/scorecard");
        if (!Directory.Exists(directory)) return null;
        string current = Path.Combine(Path.GetFullPath(output), "scorecard.json");
        return Directory.EnumerateFiles(directory, "scorecard.json", SearchOption.AllDirectories)
            .Where(path => !Path.GetFullPath(path).Equals(current, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
    }

    internal static string Sample(IEnumerable<string> paths, string seed)
    {
        var selected = paths.Where(path => path.StartsWith("src/", StringComparison.Ordinal) && path.EndsWith(".cs", StringComparison.Ordinal))
            .Distinct().OrderBy(path => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(seed + path))), StringComparer.Ordinal)
            .ThenBy(path => path, StringComparer.Ordinal).Take(10);
        return "Readability reviewed sample\nSeed: " + seed + "\n\n" + string.Join('\n', selected) +
            "\n\nScore 0–5: clear names; comments explain why, not what; one responsibility per method; " +
            "consistency with surrounding code. Independent review required.\n";
    }

    internal static string WriteSample(Scorecard context, Options options)
    {
        var props = XDocument.Load(Path.Combine(context.Root, "Version.props"));
        string release = props.Descendants("TiaMcpRelease").Single().Value;
        string suffix = props.Descendants("TiaMcpPrerelease").SingleOrDefault()?.Value ?? "";
        string seed = options.Get("Seed", release + (suffix.Length == 0 ? "" : "-" + suffix));
        string basis = options.Get("Base") ?? context.ReadGit(["describe", "--tags", "--abbrev=0", "--match", "v*"]);
        string[] paths = context.ReadGit(["diff", "--name-only", "--diff-filter=d", "-z", basis, "HEAD", "--", "src"]).Split('\0');
        return "Base: " + basis + "\n" + Sample(paths, seed);
    }

    internal static int Run(string root, Options options)
    {
        string[] allowed = ["Base", "Output", "Previous", "PublicApiRoot", "Seed", "ReviewedSample"];
        if (options.Arguments().Any(argument => !allowed.Any(name => argument.StartsWith("-" + name + "=",
                StringComparison.OrdinalIgnoreCase)))) throw new ReleaseException("Unknown scorecard option", 64);
        double? reviewed = null;
        if (options.Get("ReviewedSample") is { } value)
        {
            if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) ||
                !double.IsFinite(number) || number < 0 || number > 5) throw new ReleaseException("-ReviewedSample must be 0–5", 64);
            reviewed = number;
        }
        var watch = Stopwatch.StartNew();
        string primary = PrimaryCheckout(root);
        var context = new Scorecard(root, primary, Path.Combine(root, options.Get("PublicApiRoot", Path.Combine(primary, "sdk"))));
        string output = options.Get("Output", Path.Combine(primary, "bin-build/scorecard", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-unknown"));
        try
        {
            try { context.Report.Commit = Git(root, "rev-parse", "--short", "HEAD"); }
            catch (Exception ex) { context.Report.Commit = "unknown"; context.Report.Notes.Add("Git identity not measured: " + ex.Message); }
            if (options.Get("Output") == null) output = output.Replace("-unknown", "-" + context.Report.Commit, StringComparison.Ordinal);
            string timingPath = Path.Combine(primary, "bin-build/scorecard/merge-check-runs.jsonl");
            context.Report.Capture("Verification efficiency", "", timingPath, () =>
            {
                context.Timings = MergeCheckRuns.Read(timingPath);
                context.RunDirectory = context.Timings.OrderByDescending(row => row.Utc)
                    .FirstOrDefault(row => Directory.Exists(row.Output))?.Output ?? context.RunDirectory;
            });
            ScorecardCoverage.Collect(context);
            ScorecardRuns.Collect(context);
            ScorecardEvidence.WriteSafety(context);
            ScorecardEvidence.Tests(context);
            ScorecardEvidence.Security(context);
            ScorecardEvidence.Documentation(context);
            ScorecardEvidence.Performance(context);
            ScorecardEvidence.Efficiency(context);
            ScorecardSource.Collect(context, reviewed);
            ScorecardSource.Reviews(context);
            string sample;
            try { sample = WriteSample(context, options); }
            catch (Exception ex) { sample = "Sample not measured: " + ex.Message + "\n"; context.Report.Notes.Add(sample.Trim()); }
            var previous = options.Get("Previous");
            try
            {
                previous ??= Previous(primary, output);
                if (previous != null)
                    context.Report.Compare(JsonSerializer.Deserialize<ScorecardReport>(File.ReadAllText(previous), ScorecardReport.Json));
            }
            catch (Exception ex) { context.Report.Notes.Add("Previous scorecard not measured: " + previous + ": " + ex.Message); }
            Directory.CreateDirectory(output);
            File.WriteAllText(Path.Combine(output, "sample.txt"), sample);
            context.Report.Seconds = watch.Elapsed.TotalSeconds;
            File.WriteAllText(Path.Combine(output, "scorecard.json"), JsonSerializer.Serialize(context.Report, ScorecardReport.Json) + "\n");
            File.WriteAllText(Path.Combine(output, "scorecard.md"), context.Report.Render());
            Console.WriteLine($"scorecard: {ScorecardReport.Number(context.Report.Seconds)} seconds; {Path.GetFullPath(output)}; bands proposed");
        }
        catch (Exception ex) { Console.WriteLine("scorecard: report unavailable: " + ex.Message); }
        return 0;
    }
}
