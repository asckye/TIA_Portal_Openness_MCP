namespace TiaMcp.ReleaseTool;

internal sealed record PipelineResult(string Name, int ExitCode, string StandardOutput, string StandardError);

internal static class ParallelPipeline
{
    internal static IReadOnlyList<PipelineResult> Run(
        IReadOnlyList<(string Name, Func<PipelineResult> Run)> pipelines,
        string logDirectory)
    {
        if (pipelines.Count == 0) throw new ReleaseException("At least one version pipeline is required.");
        if (pipelines.Select(pipeline => pipeline.Name).Distinct(StringComparer.Ordinal).Count() != pipelines.Count)
            throw new ReleaseException("Version pipeline names must be unique.");

        Directory.CreateDirectory(logDirectory);
        var tasks = pipelines.Select(pipeline => Task.Run(() =>
        {
            try { return pipeline.Run(); }
            catch (Exception ex) { return new PipelineResult(pipeline.Name, 1, "", ex.ToString()); }
        })).ToArray();

        var results = Task.WhenAll(tasks).GetAwaiter().GetResult();
        foreach (var result in results)
        {
            var log = Path.Combine(logDirectory, result.Name + ".log");
            File.WriteAllText(log, result.StandardOutput + result.StandardError, new System.Text.UTF8Encoding(false));
            Console.WriteLine($"{result.Name}: {(result.ExitCode == 0 ? "passed" : "failed")}; log: {log}");
        }

        var failures = results.Where(result => result.ExitCode != 0).Select(result => result.Name).ToArray();
        if (failures.Length != 0)
            throw new ReleaseException("Version pipelines failed: " + string.Join(", ", failures) + "; both logs retained");
        return results;
    }
}
