using System.Diagnostics;

namespace TiaMcp.ReleaseTool;

internal sealed record PipelineResult(string Name, int ExitCode, string StandardOutput, string StandardError);

internal static class ParallelPipeline
{
    internal static IReadOnlyList<PipelineResult> Run(
        IReadOnlyList<(string Name, Func<PipelineResult> Run)> pipelines,
        string logDirectory,
        int maxParallelism = int.MaxValue)
    {
        if (pipelines.Count == 0) throw new ReleaseException("At least one version pipeline is required.");
        if (pipelines.Select(pipeline => pipeline.Name).Distinct(StringComparer.Ordinal).Count() != pipelines.Count)
            throw new ReleaseException("Version pipeline names must be unique.");
        if (maxParallelism < 1) throw new ReleaseException("Maximum parallelism must be at least 1.");

        Directory.CreateDirectory(logDirectory);
        var results = new PipelineResult?[pipelines.Count];
        var elapsed = new double[pipelines.Count];
        var next = -1;
        var stopStarting = 0;
        var workers = Enumerable.Range(0, Math.Min(maxParallelism, pipelines.Count)).Select(_ => Task.Run(() =>
        {
            while (Volatile.Read(ref stopStarting) == 0)
            {
                var index = Interlocked.Increment(ref next);
                if (index >= pipelines.Count || Volatile.Read(ref stopStarting) != 0) return;
                var pipeline = pipelines[index];
                var timer = Stopwatch.StartNew();
                PipelineResult result;
                try { result = pipeline.Run(); }
                catch (Exception ex) { result = new PipelineResult(pipeline.Name, 1, "", ex.ToString()); }
                timer.Stop();
                elapsed[index] = timer.Elapsed.TotalSeconds;
                results[index] = result;
                if (result.ExitCode != 0) Interlocked.Exchange(ref stopStarting, 1);
            }
        })).ToArray();
        Task.WhenAll(workers).GetAwaiter().GetResult();
        var completed = results.Where(result => result is not null).Select(result => result!).ToArray();
        foreach (var (maybeResult, index) in results.Select((result, index) => (result, index)).Where(row => row.result is not null))
        {
            var result = maybeResult!;
            var log = Path.Combine(logDirectory, result.Name + ".log");
            File.WriteAllText(log, result.StandardOutput + result.StandardError + $"Elapsed: {elapsed[index]:F3} seconds{Environment.NewLine}", new System.Text.UTF8Encoding(false));
            Console.WriteLine($"{result.Name}: {(result.ExitCode == 0 ? "passed" : "failed")}; elapsed={elapsed[index]:F3}s; log: {log}");
        }
        foreach (var skipped in pipelines.Where((_, index) => results[index] is null))
        {
            var log = Path.Combine(logDirectory, skipped.Name + ".log");
            File.WriteAllText(log, "Not started because an earlier pipeline failed." + Environment.NewLine, new System.Text.UTF8Encoding(false));
            Console.WriteLine($"{skipped.Name}: not started after an earlier failure; log: {log}");
        }

        var failures = completed.Where(result => result.ExitCode != 0).Select(result => result.Name).ToArray();
        if (failures.Length != 0)
            throw new ReleaseException("Pipelines failed: " + string.Join(", ", failures) + "; all started and skipped jobs have logs");
        return completed;
    }
}
