using TiaMcp.ReleaseTool;
using TiaMcp.BuildCommon;
using System.Text.Json;
using Xunit;

namespace TiaMcp.ReleaseTool.Tests;

public sealed class ReleaseValidationTests
{
    [Theory]
    [Trait("Category", "Pipeline")]
    [InlineData("none", "aggregate")]
    [InlineData("none", "v20-log")]
    [InlineData("none", "v21-log")]
    [InlineData("v20", "aggregate")]
    [InlineData("v20", "v20-log")]
    [InlineData("v20", "v21-log")]
    [InlineData("both", "aggregate")]
    [InlineData("both", "v20-log")]
    [InlineData("both", "v21-log")]
    public void ParallelPipelinePreservesLegacyAggregateAndPerVersionLogs(string mode, string assertion)
    {
        var directory = Path.Combine(Path.GetTempPath(), "release-pipeline-parity-" + Guid.NewGuid().ToString("N"));
        try
        {
            var shouldFail20 = mode is "v20" or "both";
            var shouldFail21 = mode == "both";
            ReleaseException? failure = null;
            try
            {
                _ = ParallelPipeline.Run(
                    [("release-v20", () => new PipelineResult("release-v20", shouldFail20 ? 7 : 0, "V20 passed", shouldFail20 ? "Synthetic V20 failure" : "")),
                     ("release-v21", () => new PipelineResult("release-v21", shouldFail21 ? 8 : 0, "V21 passed", shouldFail21 ? "Synthetic V21 failure" : ""))], directory);
            }
            catch (ReleaseException ex) { failure = ex; }

            if (assertion == "aggregate")
            {
                if (mode == "none") Assert.Null(failure);
                else
                {
                    Assert.NotNull(failure);
                    if (shouldFail20) Assert.Contains("release-v20", failure!.Message);
                    var v21Log = File.ReadAllText(Path.Combine(directory, "release-v21.log"));
                    if (shouldFail21 && !v21Log.Contains("Not started", StringComparison.Ordinal)) Assert.Contains("release-v21", failure!.Message);
                }
            }
            else
            {
                var major = assertion == "v20-log" ? "20" : "21";
                var expectedText = "V" + major + " passed";
                var log = File.ReadAllText(Path.Combine(directory, "release-v" + major + ".log"));
                Assert.True(log.Contains(expectedText, StringComparison.Ordinal) || (major == "21" && log.Contains("Not started", StringComparison.Ordinal)));
            }
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Theory]
    [Trait("Category", "ReviewerChain")]
    [InlineData("<configuration><packageSources><clear /></packageSources></configuration>", null)]
    [InlineData("<configuration><packageSources><clear /><add key='cache' value='C:/nuget' /></packageSources></configuration>", null)]
    [InlineData("<configuration><packageSources><add key='feed' value='https://example.invalid' /></packageSources></configuration>", "NuGetConfig must clear inherited package feeds")]
    [InlineData("<configuration><packageSources><clear /><add key='feed' value='https://example.invalid' /></packageSources></configuration>", "Network NuGet feed is forbidden: https://example.invalid")]
    [InlineData("<configuration><packageSources><clear /><add key='feed' value='file://server/feed' /></packageSources></configuration>", "Network NuGet feed is forbidden: file://server/feed")]
    [InlineData("<configuration><packageSources><clear /><add key='feed' value='//server/share' /></packageSources></configuration>", "Network NuGet feed is forbidden: //server/share")]
    [InlineData("<configuration><packageSources><clear /><add key='feed' value='\\\\server\\share' /></packageSources></configuration>", "Network NuGet feed is forbidden: \\\\server\\share")]
    public void OfflineNugetRejectsNetworkFeeds(string contents, string? expected)
    {
        var path = Path.Combine(Path.GetTempPath(), "nuget-" + Guid.NewGuid().ToString("N") + ".config");
        try
        {
            File.WriteAllText(path, contents);
            Assert.Equal(expected, ReleaseValidation.OfflineNuGetError(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    [Trait("Category", "Pipeline")]
    public void ParallelPipelinesOverlapAndWriteSeparateEvidence()
    {
        var directory = Path.Combine(Path.GetTempPath(), "release-pipeline-" + Guid.NewGuid().ToString("N"));
        var active = 0;
        var maximum = 0;
        PipelineResult Execute(string name)
        {
            var current = Interlocked.Increment(ref active);
            InterlockedExtensions.Max(ref maximum, current);
            Thread.Sleep(100);
            Interlocked.Decrement(ref active);
            return new PipelineResult(name, 0, name + " output", "");
        }
        try
        {
            var results = ParallelPipeline.Run(
                [("release-v20", () => Execute("release-v20")), ("release-v21", () => Execute("release-v21"))], directory);
            Assert.Equal(2, results.Count);
            Assert.True(maximum > 1, "Both version pipelines should overlap.");
            Assert.StartsWith("release-v20 output", File.ReadAllText(Path.Combine(directory, "release-v20.log")));
            Assert.StartsWith("release-v21 output", File.ReadAllText(Path.Combine(directory, "release-v21.log")));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    [Trait("Category", "Pipeline")]
    public void ParallelPipelinesHonorSerialAndBoundedLimits()
    {
        var directory = Path.Combine(Path.GetTempPath(), "release-pipeline-limit-" + Guid.NewGuid().ToString("N"));
        var active = 0;
        var maximum = 0;
        PipelineResult Execute(string name)
        {
            var current = Interlocked.Increment(ref active);
            InterlockedExtensions.Max(ref maximum, current);
            Thread.Sleep(40);
            Interlocked.Decrement(ref active);
            return new PipelineResult(name, 0, name, "");
        }
        try
        {
            var jobs = Enumerable.Range(0, 6).Select(index => ($"job-{index}", (Func<PipelineResult>)(() => Execute($"job-{index}")))).ToArray();
            var serialResults = ParallelPipeline.Run(jobs, Path.Combine(directory, "serial"), maxParallelism: 1);
            Assert.Equal(1, maximum);
            maximum = 0;
            var boundedResults = ParallelPipeline.Run(jobs, Path.Combine(directory, "bounded"), maxParallelism: 2);
            Assert.InRange(maximum, 1, 2);
            Assert.Equal(serialResults.Select(result => (result.Name, result.ExitCode, result.StandardOutput, result.StandardError)),
                boundedResults.Select(result => (result.Name, result.ExitCode, result.StandardOutput, result.StandardError)));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    [Trait("Category", "Pipeline")]
    public void ParallelPipelineFailureStopsQueuedJobsAndWaitsForRunningJobs()
    {
        var directory = Path.Combine(Path.GetTempPath(), "release-pipeline-cancel-" + Guid.NewGuid().ToString("N"));
        var runningFinished = false;
        var queuedStarted = false;
        try
        {
        using var runningStarted = new ManualResetEventSlim();
        var error = Assert.Throws<ReleaseException>(() => ParallelPipeline.Run(
                [("failure", () => { Assert.True(runningStarted.Wait(TimeSpan.FromSeconds(5))); return new PipelineResult("failure", 7, "", "failed"); }),
                 ("running", () => { runningStarted.Set(); Thread.Sleep(80); runningFinished = true; return new PipelineResult("running", 0, "", ""); }),
                 ("queued", () => { queuedStarted = true; return new PipelineResult("queued", 0, "", ""); })], directory, maxParallelism: 2));
            Assert.Contains("failure", error.Message);
            Assert.True(runningFinished);
            Assert.False(queuedStarted);
            Assert.Contains("Not started", File.ReadAllText(Path.Combine(directory, "queued.log")));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    [Trait("Category", "Pipeline")]
    public void ParallelPipelinesReportEveryFailureAndRetainBothLogs()
    {
        var directory = Path.Combine(Path.GetTempPath(), "release-pipeline-" + Guid.NewGuid().ToString("N"));
        using var bothStarted = new CountdownEvent(2);
        PipelineResult FailAfterBothStart(string name, int exitCode)
        {
            bothStarted.Signal();
            Assert.True(bothStarted.Wait(TimeSpan.FromSeconds(5)));
            var version = name == "release-v20" ? "20" : "21";
            return new PipelineResult(name, exitCode, "v" + version, "failure " + version);
        }
        try
        {
            var error = Assert.Throws<ReleaseException>(() => ParallelPipeline.Run(
                [("release-v20", () => FailAfterBothStart("release-v20", 7)),
                 ("release-v21", () => FailAfterBothStart("release-v21", 8))], directory, maxParallelism: 2));
            Assert.Contains("release-v20", error.Message);
            Assert.Contains("release-v21", error.Message);
            Assert.StartsWith("v20failure 20", File.ReadAllText(Path.Combine(directory, "release-v20.log")));
            Assert.StartsWith("v21failure 21", File.ReadAllText(Path.Combine(directory, "release-v21.log")));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}

internal static class InterlockedExtensions
{

    internal static void Max(ref int location, int value)
    {
        int current;
        do { current = Volatile.Read(ref location); }
        while (current < value && Interlocked.CompareExchange(ref location, value, current) != current);
    }
}
