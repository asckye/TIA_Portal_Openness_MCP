namespace TiaMcp.ReleaseTool;

// The caller watches the wall clock even when a worker is blocked in managed code
// or has yet to launch its first process. Parallel workers inherit their job scope.
internal sealed class GateWatchdog(string stage, TimeSpan limit, TimeProvider clock, Action<string> report)
{
    private static readonly AsyncLocal<Job?> Current = new();
    private readonly object sync = new();
    private long Now => clock.GetTimestamp();
    private readonly long started = clock.GetTimestamp();
    private readonly HashSet<Job> jobs = [];
    private readonly Dictionary<int, Action> trees = [];
    private static readonly TimeSpan Silence = TimeSpan.FromMinutes(10);
    private string? timeoutMessage;
    private long? finished;

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
        while (!wait(Poll())) { }
        Poll();
        return work.GetAwaiter().GetResult();
    }

    internal TimeSpan Poll()
    {
        lock (sync)
        {
            if (timeoutMessage != null) throw new ReleaseException(timeoutMessage);
            var now = finished ?? Now;
            var elapsed = clock.GetElapsedTime(started, now);
            var leaves = jobs.Where(job => job.Children == 0).ToArray();
            string Running() => leaves.Length == 0 ? stage : string.Join(", ", leaves.OrderBy(job => job.Name, StringComparer.Ordinal)
                .Select(job => $"{job.Name} ({clock.GetElapsedTime(job.Started, now):c})"));
            if (elapsed >= limit)
            {
                timeoutMessage = $"{stage} wall-clock limit {limit:c} reached after {elapsed:c}. Running jobs: {Running()}.";
                report(timeoutMessage);
                foreach (var (pid, stop) in trees)
                {
                    try { stop(); }
                    catch (Exception ex) { report($"Could not stop owned PID {pid}: {ex.Message}"); }
                }
                throw new ReleaseException(timeoutMessage);
            }
            var silent = leaves.Where(job => clock.GetElapsedTime(job.LastOutput, now) >= Silence).ToArray();
            if (silent.Length > 0)
            {
                report($"{stage} heartbeat. Running jobs: {Running()}.");
                foreach (var job in silent) job.LastOutput = now;
            }
            var untilHeartbeat = leaves.Select(job => Silence - clock.GetElapsedTime(job.LastOutput, now)).DefaultIfEmpty(Silence).Min();
            return TimeSpan.FromMilliseconds(Math.Ceiling(Math.Min(untilHeartbeat.TotalMilliseconds, (limit - elapsed).TotalMilliseconds)));
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
            lock (Owner.sync)
            {
                if (Owner.timeoutMessage != null)
                {
                    stop();
                    throw new ReleaseException(Owner.timeoutMessage);
                }
                Owner.trees.Add(pid, stop);
                var registration = new Tree(Owner, pid);
                try { Owner.RequireTime(); }
                catch { registration.Dispose(); throw; }
                return registration;
            }
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
        if (timeoutMessage != null || clock.GetElapsedTime(started) >= limit) Poll();
    }

    private sealed class Tree(GateWatchdog owner, int pid) : IDisposable
    {
        public void Dispose() { lock (owner.sync) owner.trees.Remove(pid); }
    }
}
