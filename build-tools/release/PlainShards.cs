using System.Text.Json.Nodes;

namespace TiaMcp.ReleaseTool;

// Sensitive partitions retain serial order. GUI input runs before the layout lanes.
internal static class PlainShards
{
    internal static IReadOnlyList<PipelineResult> RunLanes(string output, int parallelism,
        IReadOnlyList<(string Name, Func<PipelineResult> Run)> ordinary,
        IReadOnlyList<(string Name, Func<PipelineResult> Run)> sensitiveZero,
        IReadOnlyList<(string Name, Func<PipelineResult> Run)> sensitiveOne, bool sensitiveFirst = false)
    {
        var lanes = new[] { (Jobs: ordinary, Limit: parallelism), (Jobs: sensitiveZero, Limit: 1), (Jobs: sensitiveOne, Limit: 1) };
        IReadOnlyList<PipelineResult> RunLane(int index) => lanes[index].Jobs.Count == 0
            ? Array.Empty<PipelineResult>()
            : ParallelPipeline.Run(lanes[index].Jobs, Path.Combine(output, "lane-" + index), lanes[index].Limit, continueOnFailure: true);
        if (sensitiveFirst)
        {
            var sensitive = RunLane(1).Concat(RunLane(2)).ToArray();
            return RunLane(0).Concat(sensitive).ToArray();
        }
        return Task.WhenAll(Enumerable.Range(0, lanes.Length).Select(index => Task.Run(() => RunLane(index))))
            .GetAwaiter().GetResult().SelectMany(rows => rows).ToArray();
    }

    internal static CommandResult Run(string root, Options options, JsonObject catalog, SuiteProcess? process = null)
    {
        var output = Path.GetFullPath(options.Get("ResultsDirectory", Path.Combine(root, "bin-build/merge-check/foundation")));
        int parallelism = FastSuites.Positive(options, "MaxParallelism", Math.Max(1, Environment.ProcessorCount - 4));
        var failures = new List<string>();
        var messages = new List<string>();
        foreach (var name in options.All("Suite").Distinct(StringComparer.Ordinal))
        {
            var suite = catalog[name]!.AsObject();
            var unit = SuiteBuild.Suite(suite);
            var directory = Path.Combine(output, name);
            var history = Path.GetFullPath(options.Get("TimingHistory", Path.Combine(root, "bin-build/check-timings")));
            var api = PublicApiRoot(name, suite, options);
            var shards = suite["minimumPassed"]!.GetValue<int>() >= 100 ? FastSuites.Positive(options, "Shards", 8) : 1;
            bool studioGui = name == "studio-gui";
            bool reserve = TimeSensitiveTests.ReserveShard(unit.Project) && (!studioGui || shards > 1);
            var plan = SuiteCosts.Prepare(name, directory, history, null, shards,
                reserve, reserve ? TimeSensitiveTests.ReservedShardCount(unit.Project) : 1,
                fixedShards: studioGui ? shards : null);
            int sensitiveCount = plan.SensitivePartitions?.Length ?? 0;
            int ordinaryCount = plan.Costs.Length - sensitiveCount;
            var jobs = Enumerable.Range(0, plan.Costs.Length).Select(index =>
                (Name: name + "-shard-" + index, Run: (Func<PipelineResult>)(() =>
                {
                    var result = FastSuites.RunShard(root, name, suite, Path.Combine(directory, "shard-" + index),
                        options.Get("Dotnet", "dotnet"), api, unit, index, plan.Costs.Length,
                        plan: plan.Path, reserveSensitive: reserve, process: process, sensitiveShards: sensitiveCount);
                    return new(name + "-shard-" + index, result.ExitCode, result.StandardOutput, result.StandardError);
                }))).ToArray();
            // Finish GUI input on the shared desktop before starting its parallel layout processes.
            var rows = RunLanes(directory, studioGui ? Math.Max(1, parallelism - 1) : parallelism, jobs.Take(ordinaryCount).ToArray(),
                jobs.Skip(ordinaryCount).Where((_, i) => plan.SensitivePartitions![i] == 0).ToArray(),
                jobs.Skip(ordinaryCount).Where((_, i) => plan.SensitivePartitions![i] != 0).ToArray(), sensitiveFirst: studioGui);
            failures.AddRange(rows.Where(row => row.ExitCode != 0).Select(row => row.Name + ": " + row.StandardError));
            SuiteCosts.Record(name, directory, history, jobs.Length);
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

    internal static string? PublicApiRoot(string name, JsonObject suite, Options options) =>
        name == "foundation-api" || suite["publicApiRoot"]?.GetValue<bool>() == true
            ? options.Get("PublicApiRoot") ?? Environment.GetEnvironmentVariable("TIA_MCP_TEST_PUBLIC_API_ROOT") : null;
}
