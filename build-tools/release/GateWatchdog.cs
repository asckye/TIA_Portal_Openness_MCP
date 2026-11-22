using TiaMcp.BuildCommon;

namespace TiaMcp.ReleaseTool;

// The caller watches the wall clock even when a worker is blocked in managed code
// or has yet to launch its first process. Parallel workers inherit their job scope.
internal sealed class GateWatchdog(string stage, TimeSpan limit, TimeProvider clock, Action<string> report,
    Action<Task, TimeSpan>? waitForCleanup = null)
{
    private static readonly AsyncLocal<Job?> Current = new();
    private readonly object sync = new();
    private readonly Action<Task, TimeSpan> waitForStops = waitForCleanup ?? ((task, timeout) => task.Wait(timeout));
    private long Now => clock.GetTimestamp();
    private readonly long started = clock.GetTimestamp();
    private readonly HashSet<Job> jobs = [];
    private readonly HashSet<Tree> trees = [];
    private Tree[] pendingStops = [];
    private static readonly TimeSpan Silence = TimeSpan.FromMinutes(10);
    private string? timeoutMessage;
    private long? finished;

    internal static int TimeoutMinutes(Options options, int parallelism, int defaultMinutes) =>
        FastSuites.Positive(options, "TimeoutMinutes", parallelism == 1 ? 3 * defaultMinutes : defaultMinutes);

    internal static T Run<T>(string stage, int minutes, Func<T> action)
    {
        var watchdog = new GateWatchdog(stage, TimeSpan.FromMinutes(minutes), TimeProvider.System, Console.Error.WriteLine);
        return watchdog.Supervise(action);
    }

    internal T Supervise<T>(Func<T> action)
    {
        var work = Task.Factory.StartNew(() =>
        {
            using var root = new Job(this, stage, null);
            try { return action(); }
            finally { lock (sync) finished = Now; }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        return WaitForCompletion(work, ((IAsyncResult)work).AsyncWaitHandle.WaitOne);
    }

    internal T WaitForCompletion<T>(Task<T> work, Func<TimeSpan, bool> wait)
    {
        var delay = Poll(work);
        while (delay != Timeout.InfiniteTimeSpan)
        {
            wait(delay);
            delay = Poll(work);
        }
        return work.GetAwaiter().GetResult();
    }

    internal TimeSpan Poll(Task? work = null)
    {
        Tree[] stops;
        string? message = null;
        lock (sync)
        {
            var now = finished ?? Now;
            if (timeoutMessage == null && (finished.HasValue || work?.IsCompleted == true))
            {
                var elapsed = clock.GetElapsedTime(started, now);
                if (elapsed > limit) report($"{stage} finished {elapsed - limit:c} after its wall-clock limit {limit:c}.");
            }
            else if ((message = ObserveExpiry(now)) == null)
            {
                var leaves = jobs.Where(job => job.Children == 0).ToArray();
                var silent = leaves.Where(job => clock.GetElapsedTime(job.LastOutput, now) >= Silence).ToArray();
                if (silent.Length > 0)
                {
                    report($"{stage} heartbeat. Running jobs: {Running(leaves, now)}.");
                    foreach (var job in silent) job.LastOutput = now;
                }
                var untilHeartbeat = leaves.Select(job => Silence - clock.GetElapsedTime(job.LastOutput, now)).DefaultIfEmpty(Silence).Min();
                var left = limit - clock.GetElapsedTime(started, now);
                return TimeSpan.FromMilliseconds(Math.Ceiling(Math.Min(untilHeartbeat.TotalMilliseconds, left.TotalMilliseconds)));
            }
            stops = pendingStops;
            pendingStops = [];
        }
        StopTrees(stops);
        if (message != null) throw new ReleaseException(message);
        return Timeout.InfiniteTimeSpan;
    }

    private string Running(Job[] leaves, long now) => leaves.Length == 0 ? stage : string.Join(", ", leaves.OrderBy(job => job.Name, StringComparer.Ordinal)
        .Select(job => $"{job.Name} ({clock.GetElapsedTime(job.Started, now):c})"));

    // Capture jobs and registrations together at the first observation, including expiry noticed by a worker.
    // The supervisor drains this snapshot outside sync, independently of worker unwinding.
    private string? ObserveExpiry(long now)
    {
        var elapsed = clock.GetElapsedTime(started, now);
        if (timeoutMessage == null && elapsed >= limit)
        {
            var running = Running(jobs.Where(job => job.Children == 0).ToArray(), now);
            timeoutMessage = $"{stage} wall-clock limit {limit:c} reached after {elapsed:c}. Running jobs: {running}.";
            pendingStops = trees.ToArray();
            report(timeoutMessage);
        }
        return timeoutMessage;
    }

    private void StopTrees(Tree[] stops)
    {
        if (stops.Length == 0) return;
        var all = Task.WhenAll(stops.Select(tree => tree.Stop.Value));
        var grace = ProcessDeadline.CleanupTimeout;
        waitForStops(all, grace);
        foreach (var tree in stops)
        {
            var stopped = tree.Stop.Value;
            if (!stopped.IsCompleted) report($"Cleanup of owned PID {tree.Pid} still pending after {grace:c}.");
            else if (stopped.Result is { } error) report($"Could not stop owned PID {tree.Pid}: {error}");
        }
    }

    internal static Job? BeginJob(string name) => Current.Value is { } parent ? new Job(parent.Owner, name, parent) : null;
    internal static Job? ActiveJob => Current.Value;

    internal sealed class Job : IDisposable
    {
        internal GateWatchdog Owner { get; }
        internal string Name { get; }
        internal long Started { get; }
        internal long LastOutput { get; set; }
        internal int Children { get; private set; }
        private readonly Job? parent;

        internal Job(GateWatchdog owner, string name, Job? parent)
        {
            Owner = owner;
            Name = name;
            this.parent = parent;
            lock (owner.sync)
            {
                owner.RequireTime();
                Started = LastOutput = owner.Now;
                if (parent != null) parent.Children++;
                owner.jobs.Add(this);
                Current.Value = this;
            }
        }

        internal void Output()
        {
            lock (Owner.sync) LastOutput = Owner.Now;
        }

        internal void RequireTime()
        {
            lock (Owner.sync) Owner.RequireTime();
        }

        // A launch can finish after the deadline. Its handle remains owned by the
        // caller until registration (or immediate termination) completes.
        internal IDisposable TrackTree(int pid, Action stop)
        {
            var tree = new Tree(Owner, pid, stop);
            string message;
            lock (Owner.sync)
            {
                if (Owner.ObserveExpiry(Owner.Now) is not { } expired)
                {
                    Owner.trees.Add(tree);
                    return tree;
                }
                message = expired;
            }
            Owner.StopTrees([tree]);
            throw new ReleaseException(message);
        }

        public void Dispose()
        {
            lock (Owner.sync)
            {
                Owner.jobs.Remove(this);
                if (parent != null)
                {
                    parent.Children--;
                    parent.LastOutput = Owner.Now;
                }
                Current.Value = parent;
            }
        }
    }

    private void RequireTime()
    {
        if (ObserveExpiry(Now) is { } message) throw new ReleaseException(message);
    }

    private sealed class Tree(GateWatchdog owner, int pid, Action stop) : IDisposable
    {
        internal int Pid => pid;
        // Either the supervisor or the returning worker can initiate cleanup. Both share one stop attempt.
        internal Lazy<Task<string?>> Stop { get; } = new(() => Task.Factory.StartNew<string?>(() =>
        {
            try { stop(); return null; }
            catch (Exception ex) { return ex.Message; }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default));

        public void Dispose()
        {
            lock (owner.sync)
            {
                if (!owner.trees.Remove(this) || owner.timeoutMessage == null) return;
            }
            // Keep the caller's process handles alive while the snapshot uses them, within the same bounded grace.
            Stop.Value.Wait(ProcessDeadline.CleanupTimeout);
        }
    }
}
