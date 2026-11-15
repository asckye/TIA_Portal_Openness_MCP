using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;

namespace TiaMcp.ReleaseTool;
internal static partial class MergeCheck
{
    internal static string Timeline(IEnumerable<Timing> timings) => "name\tstart_seconds\tend_seconds\telapsed_seconds\texit_code\n"
        + string.Concat(timings.OrderBy(row => row.Start).ThenBy(row => row.Name, StringComparer.Ordinal).Select(row =>
            FormattableString.Invariant($"{row.Name}\t{row.Start:F3}\t{row.End:F3}\t{row.Seconds:F3}\t{row.Result.ExitCode}\n")));
    internal static string Summary(IEnumerable<Timing> timings, double seconds) => string.Join('\n', timings.OrderByDescending(row => row.Seconds)
        .ThenBy(row => row.Name, StringComparer.Ordinal).Select(row => $"{row.Name}: {row.Seconds.ToString("F3", CultureInfo.InvariantCulture)} s"))
        + $"\nTotal: {seconds.ToString("F3", CultureInfo.InvariantCulture)} s\n"
        + string.Join('\n', timings.Where(row => row.Result.ExitCode != 0).Select(row => "FAIL " + row.Name + ": exit " + row.Result.ExitCode + "; " + row.Result.StandardOutput + row.Result.StandardError)) + "\n"
        + string.Concat(timings.Where(row => row.Name == "test-trends").Select(row => row.Result.StandardOutput));

    internal static CommandResult MergeTrends(string root, string output, Func<string[], CommandResult> git,
        IEnumerable<Timing> timings)
    {
        var diagnostics = new StringBuilder();
        string notice = "";
        string revision = "unknown";
        string utc = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        void TryIO(string operation, Action action)
        {
            try { action(); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { /* swallow(logging-failure): Optional trend storage must not fail the merge gate. */
                diagnostics.AppendLine($"Skipped {operation}: {error.GetType().Name}.");
            }
        }
        try
        {
            string directory = Path.Combine(output, "trend");
            var rows = new List<string>();
            TryIO("trend samples", () =>
            {
                if (Directory.Exists(directory))
                    foreach (string path in Directory.EnumerateFiles(directory, "trend-*.jsonl").Order(StringComparer.Ordinal))
                        TryIO("trend sample file", () => rows.AddRange(File.ReadAllLines(path)));
            });
            rows.AddRange(timings.Where(row => row.Result.ExitCode == 0).Select(row => new JsonObject
            {
                ["metric"] = "timing:" + row.Name, ["value"] = row.Seconds, ["unit"] = "s"
            }.ToJsonString()));
            var head = git(["rev-parse", "HEAD"]);
            ProcessRunner.RequireSuccess(head, "Read test trend revision");
            revision = head.StandardOutput.Trim();
            var common = git(["rev-parse", "--path-format=absolute", "--git-common-dir"]);
            ProcessRunner.RequireSuccess(common, "Locate test trend history");
            string main = Directory.GetParent(Path.GetFullPath(Path.Combine(root, common.StandardOutput.Trim())))!.FullName;
            string history = Path.Combine(main, "bin-build", "trend.jsonl");
            string identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(history)));
            using var append = new Mutex(false, "TiaMcp.TestTrends." + identity);
            try { append.WaitOne(); }
            catch (AbandonedMutexException) { /* The previous writer exited; this process now owns the mutex. */ }
            try
            {
                string[] earlier = [];
                TryIO("trend history read", () => earlier = File.ReadAllLines(history));
                notice = TrendNotice(rows, earlier, out var samples);
                TryIO("merged trend samples", () => File.WriteAllLines(Path.Combine(output, "trend.jsonl"),
                    samples.Select(row => row.ToJsonString()), new UTF8Encoding(false)));
                foreach (var sample in samples)
                {
                    sample["utc"] = utc;
                    sample["head"] = revision;
                }
                TryIO("trend history write", () =>
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(history)!);
                    File.AppendAllLines(history, samples.Select(row => row.ToJsonString()), new UTF8Encoding(false));
                });
            }
            finally { append.ReleaseMutex(); }
        }
        catch (ReleaseException error)
        { /* swallow(logging-failure): Trend metadata is optional and cannot change the gate result. */
            diagnostics.AppendLine("Skipped trend metadata: " + error.Message.Split('\n')[0]);
        }
        TryIO("trend notice write", () => File.WriteAllText(Path.Combine(output, "trend-notice.txt"),
            $"Trend notice from commit {revision} at {utc}\n" + diagnostics + notice, new UTF8Encoding(false)));
        return new(0, diagnostics + notice, "");
    }

    internal static string TrendNotice(IEnumerable<string> rows, IEnumerable<string> history, out JsonObject[] samples)
    {
        var notice = new StringBuilder();
        JsonObject[] Read(IEnumerable<string> source, string name)
        {
            var valid = new List<JsonObject>();
            int line = 0;
            foreach (string text in source)
            {
                line++;
                try
                {
                    if (JsonNode.Parse(text) is JsonObject row
                        && row["metric"] is JsonValue metric && metric.TryGetValue<string>(out var label)
                        && !string.IsNullOrWhiteSpace(label)
                        && row["unit"] is JsonValue unit && unit.TryGetValue<string>(out var unitName)
                        && !string.IsNullOrWhiteSpace(unitName)
                        && row["value"] is JsonValue value && value.TryGetValue<double>(out double number)
                        && double.IsFinite(number))
                    {
                        valid.Add(row);
                        continue;
                    }
                }
                catch (Exception error) when (error is JsonException or ArgumentException)
                { /* swallow(parse-fallback): Ignore a malformed optional sample and retain the remaining rows. */ }
                notice.AppendLine($"Skipped malformed {name} row {line}.");
            }
            return valid.ToArray();
        }
        samples = Read(rows, "trend sample");
        var earlier = Read(history, "trend history")
            .GroupBy(row => ((string)row["metric"]!, (string)row["unit"]!))
            .ToDictionary(group => group.Key, group => group.TakeLast(20).Select(row => (double)row["value"]!)
                .Order().ToArray());
        bool heading = false;
        foreach (var sample in samples)
        {
            string metric = (string)sample["metric"]!;
            string unit = (string)sample["unit"]!;
            double value = (double)sample["value"]!;
            if (!earlier.TryGetValue((metric, unit), out var values) || values.Length < 5) continue;
            int middle = values.Length / 2;
            double median = values.Length % 2 == 0 ? values[middle - 1] / 2 + values[middle] / 2 : values[middle];
            double ratio = value / median;
            if (!(ratio > 2)) continue;
            if (!heading) { notice.AppendLine("Slower than usual"); heading = true; }
            notice.AppendLine(FormattableString.Invariant($"{metric}: {value:G6} {unit}, median {median:G6} {unit}, ratio {ratio:G6}x"));
        }
        return notice.ToString();
    }
}
