using System.Diagnostics;
using TiaMcp.BuildCommon;
using Xunit;

namespace TiaMcp.ReleaseTool.Tests;

public sealed class GateWatchdogTests
{
    private sealed class Clock : TimeProvider
    {
        private long ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref ticks);
        internal void Advance(TimeSpan by) => Interlocked.Add(ref ticks, by.Ticks);
    }

    private static ProcessStartInfo Msbuild(params string[] args)
    {
        var start = new ProcessStartInfo("dotnet");
        foreach (var arg in new[] { "msbuild", "worker.csproj" }.Concat(args)) start.ArgumentList.Add(arg);
        return start;
    }

    [Theory]
    [InlineData("0")]
    [InlineData("1.5")]
    public void Commands_reject_invalid_wall_clock_limits_before_starting(string value)
    {
        var options = Options.Parse(["-TimeoutMinutes=" + value], Program.BooleanOptions);
        var merge = Assert.Throws<ReleaseException>(() => MergeCheck.Run(Environment.CurrentDirectory, options));
        var prepare = Assert.Throws<ReleaseException>(() => Prepare.CreateContext(Environment.CurrentDirectory, options));
        Assert.All(new[] { merge, prepare }, error =>
        {
            Assert.Equal(64, error.ExitCode);
            Assert.Contains("-TimeoutMinutes", error.Message);
        });
    }

    [Theory]
    [InlineData("-getItem:ProjectReference")]
    [InlineData("-getProperty:MSBuildToolsPath")]
    [InlineData("-t:ValidateAdapterInputs")]
    [InlineData("-t:ValidateWorkerAdapter")]
    public void Silent_query_stops_its_tree_and_reports_the_invocation(string query)
    {
        var clock = new Clock();
        var start = Msbuild(query, "-p:TiaReleaseKey=15.1");
        var deadline = new ProcessDeadline(start, clock: clock);
        int kills = 0;
        bool NeverExits(int ms)
        {
            Assert.Equal(300_000, ms);
            clock.Advance(TimeSpan.FromMilliseconds(ms));
            return false;
        }
        var failure = Assert.Throws<TimeoutException>(() => deadline.Wait(NeverExits, () => kills++));
        Assert.Equal(1, kills);
        Assert.Contains($"dotnet msbuild worker.csproj {query} -p:TiaReleaseKey=15.1", failure.Message);
        Assert.Contains("elapsed 00:05:00", failure.Message);
    }

    [Fact]
    public void Output_drain_uses_the_remainder_of_a_shorter_existing_budget()
    {
        var clock = new Clock();
        var start = Msbuild("-getProperty:DefineConstants");
        var deadline = new ProcessDeadline(start, 120000, clock);
        deadline.Wait(ms =>
        {
            Assert.Equal(120000, ms);
            clock.Advance(TimeSpan.FromSeconds(110));
            return true;
        }, () => Assert.Fail("Fast exit killed the tree"));
        bool killed = false;
        Assert.Throws<TimeoutException>(() => deadline.Wait(ms =>
        {
            Assert.Equal(10000, ms);
            clock.Advance(TimeSpan.FromMilliseconds(ms));
            return false;
        }, () => killed = true));
        Assert.True(killed);
    }

    [Fact]
    public void Wall_clock_stops_only_active_trees_and_reports_each_jobs_age()
    {
        var clock = new Clock();
        var messages = new List<string>();
        var watch = new GateWatchdog("prepare checks", TimeSpan.FromMinutes(3), clock, messages.Add);
        var killed = new List<int>();
        using var stage = new GateWatchdog.Job(watch, "prepare checks", null);
        using (stage.TrackTree(19, () => killed.Add(19))) { }
        using var selection = new GateWatchdog.Job(watch, "selection", stage);
        using var first = selection.TrackTree(21, () => killed.Add(21));
        clock.Advance(TimeSpan.FromMinutes(1));
        using var adapter = new GateWatchdog.Job(watch, "adapter-inputs", stage);
        using var second = adapter.TrackTree(22, () => killed.Add(22));
        var pending = new TaskCompletionSource<int>();
        var error = Assert.Throws<ReleaseException>(() => watch.WaitForCompletion(pending.Task, interval =>
        {
            Assert.Equal(TimeSpan.FromMinutes(2), interval);
            clock.Advance(interval);
            return false;
        }));
        Assert.NotEqual(0, error.ExitCode);
        Assert.Equal(error.Message, Assert.Single(messages));
        Assert.Contains("selection (00:03:00)", error.Message);
        Assert.Contains("adapter-inputs (00:02:00)", error.Message);
        Assert.Equal(new[] { 21, 22 }, killed.Order().ToArray());
    }

    [Fact]
    public void Expiry_prevents_a_queued_job_or_process_from_starting()
    {
        var clock = new Clock();
        var messages = new List<string>();
        var watch = new GateWatchdog("merge-check", TimeSpan.FromMinutes(1), clock, messages.Add);
        using var job = new GateWatchdog.Job(watch, "selection", null);
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Throws<ReleaseException>(job.RequireTime);
        bool stopped = false;
        Assert.Throws<ReleaseException>(() => job.TrackTree(23, () => stopped = true));
        Assert.True(stopped);
        Assert.Throws<ReleaseException>(() => GateWatchdog.BeginJob("late job"));
        Assert.Contains("selection (00:01:00)", Assert.Single(messages));
    }

    [Fact]
    public void Heartbeat_follows_job_output_and_repeats_only_after_more_silence()
    {
        var clock = new Clock();
        var messages = new List<string>();
        var watch = new GateWatchdog("merge-check", TimeSpan.FromMinutes(90), clock, messages.Add);
        using var job = new GateWatchdog.Job(watch, "worker-isolation", null);
        clock.Advance(TimeSpan.FromMinutes(9));
        job.Output();
        clock.Advance(TimeSpan.FromMinutes(9));
        watch.Poll();
        Assert.Empty(messages);
        clock.Advance(TimeSpan.FromMinutes(1));
        watch.Poll();
        watch.Poll();
        Assert.Contains("heartbeat. Running jobs: worker-isolation (00:19:00)", Assert.Single(messages));
        clock.Advance(TimeSpan.FromMinutes(10));
        watch.Poll();
        Assert.Equal(2, messages.Count);
    }

    [Fact]
    public void Fast_work_keeps_its_result_and_regular_builds_keep_their_wait()
    {
        var clock = new Clock();
        var watch = new GateWatchdog("merge-check", TimeSpan.FromMinutes(90), clock, _ => Assert.Fail("Unexpected diagnostic"));
        int result = watch.Supervise(() =>
        {
            var start = Msbuild("-t:Build");
            new ProcessDeadline(start, clock: clock).Wait(ms =>
            {
                Assert.Equal(Timeout.Infinite, ms);
                return true;
            }, () => Assert.Fail("Fast build killed"));
            return 23;
        });
        Assert.Equal(23, result);
    }
}
