using System.Text.Json.Nodes;

namespace TiaMcp.ReleaseTool;

// Three capacity lanes share the machine; sensitive partitions retain serial order.
internal static class PlainShards
{
    internal static IReadOnlyList<PipelineResult> RunLanes(string output, int parallelism,
        IReadOnlyList<(string Name, Func<PipelineResult> Run)> ordinary,
        IReadOnlyList<(string Name, Func<PipelineResult> Run)> sensitiveZero,
        IReadOnlyList<(string Name, Func<PipelineResult> Run)> sensitiveOne)
    {
        var lanes = new[] { (Jobs: ordinary, Limit: parallelism), (Jobs: sensitiveZero, Limit: 1), (Jobs: sensitiveOne, Limit: 1) };
        return Task.WhenAll(lanes.Select((lane, index) => Task.Run(() => lane.Jobs.Count == 0
            ? Array.Empty<PipelineResult>()
            : ParallelPipeline.Run(lane.Jobs, Path.Combine(output, "lane-" + index), lane.Limit, continueOnFailure: true))))
            .GetAwaiter().GetResult().SelectMany(rows => rows).ToArray();
    }

    internal static CommandResult Run(string root, Options options, JsonObject catalog, SuiteProcess? process = null)
    {
        var output = Path.GetFullPath(options.Get("ResultsDirectory", Path.Combine(root, "bin-build/merge-check/foundation")));
        int parallelism = Math.Max(1, Math.Min(FastSuites.Positive(options, "MaxParallelism", Environment.ProcessorCount), Environment.ProcessorCount - 4));
        var failures = new List<string>();
        var messages = new List<string>();
        foreach (var name in options.All("Suite").Distinct(StringComparer.Ordinal))
        {
            var suite = catalog[name]!.AsObject();
            var project = suite["project"]!.GetValue<string>();
            var unit = new CheckBuild(project, "default", []);
            var directory = Path.Combine(output, name);
            var history = Path.Combine(root, "bin-build/check-timings");
            bool reserve = TimeSensitiveTests.ReserveShard(project);
            var plan = SuiteCosts.Prepare(name, directory, history, null, FastSuites.Positive(options, "Shards", 8),
                reserve, reserve ? TimeSensitiveTests.ReservedShardCount(project) : 1);
            int sensitiveCount = plan.SensitivePartitions?.Length ?? 0;
            int ordinaryCount = plan.Costs.Length - sensitiveCount;
            var jobs = Enumerable.Range(0, plan.Costs.Length).Select(index =>
                (Name: name + "-shard-" + index, Run: (Func<PipelineResult>)(() =>
                {
                    var result = FastSuites.RunShard(root, name, suite, Path.Combine(directory, "shard-" + index),
                        options.Get("Dotnet", "dotnet"), options.Get("PublicApiRoot"), unit, index, plan.Costs.Length,
                        plan: plan.Path, reserveSensitive: reserve, process: process, sensitiveShards: sensitiveCount);
                    return new(name + "-shard-" + index, result.ExitCode, result.StandardOutput, result.StandardError);
                }))).ToArray();
            var rows = RunLanes(directory, parallelism, jobs.Take(ordinaryCount).ToArray(),
                jobs.Skip(ordinaryCount).Where((_, i) => plan.SensitivePartitions![i] == 0).ToArray(),
                jobs.Skip(ordinaryCount).Where((_, i) => plan.SensitivePartitions![i] != 0).ToArray());
            failures.AddRange(rows.Where(row => row.ExitCode != 0).Select(row => row.Name + ": " + row.StandardError));
            SuiteCosts.Record(name, directory, history);
            try
            {
                var merged = SuiteSharding.Merge(name, suite, Enumerable.Range(0, jobs.Length)
                    .Select(index => Path.Combine(directory, "shard-" + index, name + ".trx")).ToArray(), output);
                messages.Add(merged.StandardOutput);
                if (merged.ExitCode != 0) failures.Add(merged.StandardError);
            }
            catch (ReleaseException ex) { failures.Add(ex.Message); }
        }
        return new(failures.Count == 0 ? 0 : 1, string.Join("\n", messages), string.Join("\n", failures));
    }
}
