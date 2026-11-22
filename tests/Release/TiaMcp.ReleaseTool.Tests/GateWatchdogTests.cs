using System.Collections.Concurrent;
using System.Diagnostics;
using TiaMcp.BuildCommon;
using TiaMcp.Engine.Tests.Shared;
using Xunit;

namespace TiaMcp.ReleaseTool.Tests;

public sealed class GateWatchdogTests
{
    internal sealed class Clock : TimeProvider
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
        var killed = new ConcurrentBag<int>();
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
    public async Task Wall_clock_throws_while_a_tree_is_stuck_in_finish()
    {
        var clock = new Clock();
        var messages = new ConcurrentQueue<string>();
        var inFinish = new TaskCompletionSource();
        var releaseFinish = new TaskCompletionSource();
        var otherStopped = new TaskCompletionSource();
        Task? cleanup = null;
        var watch = new GateWatchdog("merge-check", TimeSpan.FromMinutes(1), clock, messages.Enqueue, (all, grace) =>
        {
            cleanup = all;
            Assert.Equal(ProcessDeadline.CleanupTimeout, grace);
            Assert.True(Task.WhenAll(inFinish.Task, otherStopped.Task).Wait(TestDeadlines.Hang));
            clock.Advance(grace);
            Assert.False(all.IsCompleted);
        });
        using var job = new GateWatchdog.Job(watch, "GUI tests", null);
        using var stuck = job.TrackTree(41, () =>
        {
            inFinish.SetResult();
            releaseFinish.Task.GetAwaiter().GetResult();
        });
        using var other = job.TrackTree(42, () =>
        {
            job.Output(); // Needs the watchdog lock while the other tree is still in Finish.
            otherStopped.SetResult();
        });
        var work = new TaskCompletionSource<int>();
        var supervisor = Task.Run(() => watch.WaitForCompletion(work.Task, interval =>
        {
            clock.Advance(interval);
            return false;
        }));
        try
        {
            var error = await Assert.ThrowsAsync<ReleaseException>(() => supervisor.WaitAsync(TestDeadlines.Hang));
            Assert.NotEqual(0, error.ExitCode);
            Assert.Contains("GUI tests (00:01:00)", error.Message);
            Assert.Contains(messages, line => line.Contains("owned PID 41 still pending"));
            Assert.False(releaseFinish.Task.IsCompleted);
        }
        finally
        {
            releaseFinish.SetResult();
            if (cleanup != null) await cleanup.WaitAsync(TestDeadlines.Hang);
        }
    }

    [Fact]
    public void Disposing_one_registration_keeps_the_other_tree_with_the_same_pid()
    {
        var clock = new Clock();
        var watch = new GateWatchdog("merge-check", TimeSpan.FromMinutes(1), clock, _ => { });
        using var job = new GateWatchdog.Job(watch, "selection", null);
        int stopped = 0, alreadyExited = 0;
        var first = job.TrackTree(21, () => alreadyExited++);
        using var second = job.TrackTree(21, () => stopped++);
        first.Dispose();
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Throws<ReleaseException>(job.RequireTime);
        Assert.Throws<ReleaseException>(() => watch.Poll());
        Assert.Equal(0, alreadyExited);
        Assert.Equal(1, stopped);
    }

    [Theory]
    [InlineData(90, 1, 270)]
    [InlineData(90, 2, 90)]
    [InlineData(120, 1, 360)]
    [InlineData(120, 6, 120)]
    public void Serial_gates_scale_only_the_default_limit(int normal, int parallelism, int expected)
    {
        var defaults = Options.Parse([], Program.BooleanOptions);
        var explicitLimit = Options.Parse(["-TimeoutMinutes=17"], Program.BooleanOptions);
        Assert.Equal(expected, GateWatchdog.TimeoutMinutes(defaults, parallelism, normal));
        Assert.Equal(17, GateWatchdog.TimeoutMinutes(explicitLimit, parallelism, normal));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Missing_active_process_zero_expires_even_if_other_messages_keep_arriving(bool quiet)
    {
        var clock = new Clock();
        var waits = new List<int>();
        uint? Read(int remaining)
        {
            waits.Add(remaining);
            clock.Advance(TimeSpan.FromMilliseconds(quiet ? remaining : Math.Min(remaining, 2000)));
            return quiet ? null : 6u; // JOB_OBJECT_MSG_NEW_PROCESS
        }
        var error = Assert.Throws<TimeoutException>(() => WindowsProcessTree.WaitForEmptyJob(51, clock, clock.GetTimestamp(), Read));
        Assert.Equal(quiet ? new[] { 5000 } : new[] { 5000, 3000, 1000 }, waits);
        Assert.Contains("owned PID 51", error.Message);
        Assert.Contains("ACTIVE_PROCESS_ZERO", error.Message);
    }

    [Fact]
    public void Exceptional_exit_attaches_cleanup_failure_and_normal_disposal_surfaces_it()
    {
        var primary = new IOException("launch failed");
        var timeout = new TimeoutException("missing ACTIVE_PROCESS_ZERO");
        void Close() => throw timeout;
        WindowsProcessTree.ClosePreservingFailure(primary, Close);
        Assert.Same(timeout, primary.Data["ProcessTreeCleanup"]);
        Assert.Same(timeout, Assert.Throws<TimeoutException>(() => WindowsProcessTree.ClosePreservingFailure(null, Close)));
    }

    [Fact]
    public void Exit_at_the_deadline_can_read_the_queued_empty_job_notification()
    {
        var clock = new Clock();
        var deadline = new ProcessDeadline(Msbuild("-getProperty:TargetFramework"), 1000, clock);
        clock.Advance(TimeSpan.FromSeconds(1));
        deadline.FinishAfterExit(budget => WindowsProcessTree.WaitForEmptyJob(52, clock, clock.GetTimestamp(), wait =>
        {
            Assert.Equal(ProcessDeadline.CleanupTimeout.TotalMilliseconds, wait);
            return 4; // ACTIVE_PROCESS_ZERO
        }, budget));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Completed_work_keeps_its_result_or_exception_and_reports_its_overrun(bool fail)
    {
        var clock = new Clock();
        var messages = new List<string>();
        var watch = new GateWatchdog("merge-check", TimeSpan.FromSeconds(1), clock, messages.Add);
        var error = new IOException("worker failure");
        var work = new TaskCompletionSource<int>();
        int Run() => watch.WaitForCompletion(work.Task, delay =>
        {
            clock.Advance(delay + TimeSpan.FromMilliseconds(1));
            if (fail) work.SetException(error);
            else work.SetResult(23);
            return false;
        });
        if (fail) Assert.Same(error, Assert.Throws<IOException>(() => Run()));
        else Assert.Equal(23, Run());
        Assert.Contains("finished 00:00:00.0010000 after its wall-clock limit", Assert.Single(messages));
    }

    [Fact]
    public void Completed_work_still_drains_the_stop_requested_by_an_expired_job()
    {
        var clock = new Clock();
        var watch = new GateWatchdog("merge-check", TimeSpan.FromMinutes(1), clock, _ => { });
        using var job = new GateWatchdog.Job(watch, "worker", null);
        int stops = 0;
        using var tree = job.TrackTree(41, () => Interlocked.Increment(ref stops));
        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Throws<ReleaseException>(job.RequireTime);
        Assert.Equal(23, watch.WaitForCompletion(Task.FromResult(23), _ => throw new InvalidOperationException("Work already completed")));
        Assert.Equal(1, stops);
    }

    [Fact]
    public void Normal_exit_succeeds_when_the_tree_finishes_after_the_watchdog_grace()
    {
        var clock = new Clock();
        var start = new ProcessStartInfo("dotnet");
        start.ArgumentList.Add("test");
        var deadline = new ProcessDeadline(start, clock: clock);
        deadline.Wait(_ => true, () => Assert.Fail("The root already exited"));
        var waits = new List<int>();
        uint? Read(int remaining)
        {
            waits.Add(remaining);
            if (waits.Count > 1) return 4; // ACTIVE_PROCESS_ZERO
            clock.Advance(TimeSpan.FromMilliseconds(Math.Min(7000, remaining)));
            return 7; // EXIT_PROCESS
        }
        ProcessRunner.FinishAfterExit(deadline, timeout =>
            WindowsProcessTree.WaitForEmptyJob(52, clock, clock.GetTimestamp(), Read, timeout));
        Assert.Equal(new[] { 120000, 113000 }, waits);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Normal_cleanup_preserves_a_shorter_command_budget_and_reports_the_command(bool timeout)
    {
        var clock = new Clock();
        var deadline = new ProcessDeadline(Msbuild("-getProperty:TargetFramework"), 30000, clock);
        clock.Advance(TimeSpan.FromSeconds(18));
        var error = Assert.Throws<ReleaseException>(() => ProcessRunner.FinishAfterExit(deadline, remaining =>
        {
            Assert.Equal(TimeSpan.FromSeconds(12), remaining);
            if (timeout) throw new TimeoutException("missing completion");
            throw new IOException("completion port failed");
        }));
        Assert.Contains("dotnet msbuild worker.csproj -getProperty:TargetFramework", error.Message);
        Assert.Contains(timeout ? "missing completion" : "completion port failed", error.Message);
        Assert.Equal(timeout ? typeof(TimeoutException) : typeof(IOException), error.GetBaseException().GetType());
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
