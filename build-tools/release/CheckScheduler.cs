using System.Diagnostics;
using System.Text.Json;
using System.Runtime.InteropServices;

namespace TiaMcp.ReleaseTool;

internal sealed record CheckJob(string Name, Func<CommandResult> Run, string[] Dependencies, int Slots = 1, bool Exclusive = false)
{
    internal double EstimatedSeconds { get; init; }
    internal double? MeasuredSeconds { get; init; }
    internal double SchedulingSeconds => MeasuredSeconds ?? EstimatedSeconds;
    internal int Priority { get; init; }
    internal string Lane { get; init; } = "general";
    internal string? SensitiveGroup { get; init; }
    internal bool GlobalExclusive { get; init; }
    internal ulong? Affinity { get; init; }
    internal bool AllowFailedDependencies { get; init; }
    internal TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(5);
    internal CheckAllResultBinding? ResultCache { get; init; }
    internal CheckAllNightlyBinding? NightlyCoverage { get; init; }
    internal CheckAllResultSpec? ResultInputs { get; init; }
    internal string? ResultCachePolicy { get; init; }
    internal System.Text.Json.Nodes.JsonObject? CiCoverage { get; init; }
}
internal sealed record CheckTiming(string Name, string Status, int ExitCode, double Seconds, string? Error)
{
    public DateTime StartedAtUtc { get; init; }
    public CheckCpuLoad? CpuLoad { get; init; }
    public CheckResourceUsage? Resources { get; init; }
    public string? AffinityMask { get; init; }
    public string? IdentityHash { get; init; }
    public string? ReusedRunId { get; init; }
    public DateTime? ReusedAtUtc { get; init; }
    public string? EvidencePath { get; init; }
    public System.Text.Json.Nodes.JsonObject? CiCoverage { get; init; }
    public static bool Passed(CheckTiming row) => row.ExitCode == 0 && (row.Status == "passed" ||
        row.Status == "covered by CI" && row.CiCoverage != null ||
        row.Status == "passed (reused)" && row.IdentityHash != null && row.ReusedRunId != null && row.ReusedAtUtc != null && row.EvidencePath != null);
}

// Unlike the release publisher's fail-fast scheduler, collect every independent
// failure here. A failed prerequisite blocks its dependents and makes the run red.
internal static class CheckScheduler
{
    private static readonly AsyncLocal<(string Name, DateTime Deadline)?> deadline = new();
    private static readonly AsyncLocal<string?> evidenceDirectory = new();
    // -FailFast (development/merge runs only): after the first executed failure nothing new starts; running jobs finish and
    // every unstarted job is recorded as blocked, so the run still fails and can never serve as release evidence.
    internal static readonly AsyncLocal<bool> FailFast = new();
    internal static (string Name, DateTime Deadline)? Deadline => deadline.Value;
    internal static string? EvidenceDirectory => evidenceDirectory.Value;
    internal static Task<CheckTiming> Start(CheckJob job, string logs)
    {
        Directory.CreateDirectory(logs);
        return Task.Factory.StartNew(() => Execute(job, logs), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    internal static CommandResult AwaitRuntime(Task<CommandResult> ready)
    {
        if (Deadline is not { } budget) return ready.GetAwaiter().GetResult();
        var remaining = budget.Deadline - DateTime.UtcNow;
        if (remaining <= TimeSpan.Zero) throw new ReleaseException("Check job timed out: " + budget.Name);
        try { return ready.WaitAsync(remaining).GetAwaiter().GetResult(); }
        catch (TimeoutException) { throw new ReleaseException("Check job timed out: " + budget.Name); }
    }
    internal static TimeSpan JobTimeout(CheckJob job, IReadOnlyDictionary<string, double> history) =>
        TimeSpan.FromSeconds(Math.Max(job.Name is "build-once" or "suite-output-parity" ? 1800 : 300,
            3 * Math.Max(job.EstimatedSeconds, history.GetValueOrDefault(job.Name))));
    internal static IReadOnlyList<CheckTiming> Run(IReadOnlyList<CheckJob> jobs, int slots, string logs, IReadOnlyDictionary<string, int>? lanes = null,
        IReadOnlyList<CheckTiming>? prerequisites = null, CheckPacking? packing = null,
        IReadOnlyDictionary<Task<CheckTiming>, CheckJob>? started = null,
        IReadOnlyDictionary<Task<CheckTiming>, CheckJob>? background = null)
    {
        lanes ??= new Dictionary<string, int> { ["general"] = slots };
        Validate(jobs, slots, lanes, prerequisites?.Select(row => row.Name).ToArray());
        Directory.CreateDirectory(logs);
        var running = started == null ? new Dictionary<Task<CheckTiming>, CheckJob>() : new(started);
        var startedNames = running.Values.Select(job => job.Name).ToHashSet(StringComparer.Ordinal);
        if (startedNames.Count != running.Count || startedNames.Any(name => !jobs.Any(job => job.Name == name)))
            throw new ReleaseException("Invalid already-started checks", 64);
        var pending = jobs.Where(job => !startedNames.Contains(job.Name)).ToList();
        var done = (prerequisites ?? []).ToDictionary(row => row.Name, StringComparer.Ordinal);
        CheckJob[] Active() => running.Values.Concat(background?.Where(pair => !pair.Key.IsCompleted).Select(pair => pair.Value) ?? []).ToArray();
        string? stoppedAfter = null;
        while (pending.Count > 0 || running.Count > 0)
        {
            if (stoppedAfter != null)
            {
                foreach (var job in pending)
                {
                    var row = new CheckTiming(job.Name, "blocked", 1, 0, "Not started: -FailFast stopped the run after " + stoppedAfter + " failed");
                    done.Add(job.Name, row);
                    File.WriteAllText(Path.Combine(logs, job.Name + ".log"), row.Error + "\n");
                }
                pending.Clear();
            }
            foreach (var job in pending.ToArray())
            {
                if (!job.AllowFailedDependencies && job.Dependencies.Any(dep => done.TryGetValue(dep, out var row) && !CheckTiming.Passed(row)))
                {
                    var row = new CheckTiming(job.Name, "blocked", 1, 0, "Failed prerequisite: " + string.Join(", ", job.Dependencies.Where(dep => done.TryGetValue(dep, out var r) && !CheckTiming.Passed(r))));
                    done.Add(job.Name, row); pending.Remove(job);
                    File.WriteAllText(Path.Combine(logs, job.Name + ".log"), row.Error + "\n");
                }
            }
            foreach (var job in ReadyOrder(pending, Active(), packing != null))
            {
                if (!job.Dependencies.All(done.ContainsKey)) continue;
                var active = Active();
                if (active.Any(row => row.GlobalExclusive) || job.GlobalExclusive && active.Length != 0) continue;
                // An updater probes process names conservatively, including
                // unreadable images. Drain every lane before its real probe.
                if (!job.GlobalExclusive && pending.Any(next => next.GlobalExclusive && next.Dependencies.All(done.ContainsKey))) continue;
                var lane = active.Where(row => row.Lane == job.Lane).ToArray();
                if (lane.Any(row => row.Exclusive)) continue;
                if (job.Exclusive && lane.Length != 0) continue;
                if (lane.Sum(row => row.Slots) + job.Slots > lanes[job.Lane]) continue;
                var admitted = job;
                if (packing != null)
                {
                    if (job.Lane == "general")
                    {
                        var mask = packing.General;
                        foreach (var reserved in jobs.Where(next => next.Lane != "general" && next.Affinity != null).GroupBy(next => next.Lane))
                            if (!active.Any(row => row.Lane == reserved.Key) &&
                                !pending.Any(next => next.Lane == reserved.Key && next.Dependencies.All(done.ContainsKey)))
                                mask |= reserved.First().Affinity!.Value;
                        mask |= packing.Available & ~jobs.Where(next => next.Lane != "general").Aggregate(0UL, (used, next) => used | next.Affinity.GetValueOrDefault());
                        // Leases are immutable for the lifetime of a command job.
                        // A ready owner closes its lane to new borrowers; below it
                        // waits for every overlapping lease (including descendants).
                        if (!packing.CanAdmit(job, mask, active)) continue;
                        admitted = job with { Affinity = mask };
                    }
                    else if (active.Any(row => row.Lane == "general" && (row.Affinity.GetValueOrDefault() & job.Affinity.GetValueOrDefault()) != 0) ||
                        !packing.CanAdmit(job, job.Affinity.GetValueOrDefault(), active)) continue;
                }
                pending.Remove(job);
                // Jobs block on child processes/barriers. Dedicated threads keep
                // reserved one/two-core CLR pools from serializing admitted work.
                running.Add(Start(admitted, logs), admitted);
            }
            if (running.Count == 0)
            {
                var waiting = background?.Keys.Where(task => !task.IsCompleted).ToArray() ?? [];
                if (pending.Count != 0 && waiting.Length != 0) { Task.WhenAny(waiting).GetAwaiter().GetResult(); continue; }
                if (pending.Count != 0) throw new ReleaseException("Check scheduler cannot make progress.");
                break;
            }
            var finished = Task.WhenAny(running.Keys.Concat(background?.Keys.Where(task => !task.IsCompleted) ?? [])).GetAwaiter().GetResult();
            if (!running.ContainsKey(finished)) continue;
            var result = finished.GetAwaiter().GetResult();
            done.Add(result.Name, result); running.Remove(finished);
            Console.WriteLine($"{result.Name}: {result.Status}; {result.Seconds:F3}s");
            if (FailFast.Value && stoppedAfter == null && !CheckTiming.Passed(result))
            {
                stoppedAfter = result.Name;
                Console.WriteLine($"-FailFast: {result.Name} failed; finishing {running.Count} running job(s) and starting no new ones.");
            }
        }
        var timings = jobs.Select(job => done[job.Name]).ToArray();
        File.WriteAllText(Path.Combine(logs, "timings.json"), JsonSerializer.Serialize(timings, new JsonSerializerOptions { WriteIndented = true }) + "\n");
        return timings;
    }

    internal static CheckJob[] ReadyOrder(IReadOnlyCollection<CheckJob> pending, IEnumerable<CheckJob> running, bool packing)
    {
        var active = running.ToArray();
        // Keep the original catalog/capture bootstrap ordering when no measured
        // ordering history is available. Measured work uses ready-command LPT.
        var estimates = pending.Any(job => job.MeasuredSeconds != null) ? null : CriticalCosts(pending.ToArray());
        // Ready inputs of an idle reserved lane retain precedence. Within each
        // lane and priority, start the longest measured command first; a short
        // parent must not inherit its children's cost and postpone long work.
        bool FeedsIdleLane(CheckJob input) => pending.Any(next => next.Lane != input.Lane &&
            next.Dependencies.Contains(input.Name, StringComparer.Ordinal) && !active.Any(job => job.Lane == next.Lane));
        return pending.OrderBy(job => packing && job.Lane == "general").ThenByDescending(FeedsIdleLane).ThenByDescending(job => job.Priority)
            .ThenByDescending(job => estimates == null ? job.SchedulingSeconds : estimates[job.Name])
            .ThenBy(job => job.Name, StringComparer.Ordinal).ToArray();
    }

    internal static Dictionary<string, double> CriticalCosts(IReadOnlyList<CheckJob> jobs)
    {
        var costs = new Dictionary<string, double>(StringComparer.Ordinal);
        var children = jobs.SelectMany(job => job.Dependencies.Select(dependency => (dependency, job)))
            .GroupBy(pair => pair.dependency).ToDictionary(group => group.Key, group => group.Select(pair => pair.job).ToArray());
        double Cost(CheckJob job)
        {
            if (costs.TryGetValue(job.Name, out var cost)) return cost;
            return costs[job.Name] = job.EstimatedSeconds + (children.TryGetValue(job.Name, out var next) ? next.Max(Cost) : 0);
        }
        foreach (var job in jobs) Cost(job);
        return costs;
    }

    internal static CheckPerformance Performance(IReadOnlyList<CheckJob> jobs, IReadOnlyList<CheckTiming> timings, DateTime start, double wallSeconds)
    {
        var rows = timings.ToDictionary(row => row.Name, StringComparer.Ordinal);
        var paths = new Dictionary<string, (double Seconds, string[] Names)>(StringComparer.Ordinal);
        var inputs = jobs.ToDictionary(job => job.Name, StringComparer.Ordinal);
        (double Seconds, string[] Names) PathTo(string name)
        {
            if (paths.TryGetValue(name, out var path)) return path;
            var previous = inputs[name].Dependencies.Select(PathTo).OrderByDescending(path => path.Seconds)
                .ThenBy(path => string.Join("/", path.Names), StringComparer.Ordinal).FirstOrDefault();
            return paths[name] = (previous.Seconds + rows[name].Seconds, [.. previous.Names ?? [], name]);
        }
        foreach (var job in jobs) PathTo(job.Name);
        var critical = paths.Values.OrderByDescending(path => path.Seconds)
            .ThenBy(path => string.Join("/", path.Names), StringComparer.Ordinal).FirstOrDefault();
        var active = timings.Where(row => row.StartedAtUtc != default && row.Seconds > 0).ToArray();
        int Peak(IEnumerable<CheckTiming> values)
        {
            int count = 0, peak = 0;
            foreach (var point in values.SelectMany(row => new[] { (At: row.StartedAtUtc, Delta: 1), (At: row.StartedAtUtc.AddSeconds(row.Seconds), Delta: -1) })
                .OrderBy(point => point.At).ThenBy(point => point.Delta))
                peak = Math.Max(peak, count += point.Delta);
            return peak;
        }
        var windows = Enumerable.Range(0, Math.Max(1, (int)Math.Ceiling(wallSeconds / 300))).Select(index =>
        {
            double from = index * 300, to = Math.Min(wallSeconds, from + 300);
            var left = start.AddSeconds(from); var right = start.AddSeconds(to);
            double seconds = active.Sum(row => Math.Max(0, (new[] { right, row.StartedAtUtc.AddSeconds(row.Seconds) }.Min() - new[] { left, row.StartedAtUtc }.Max()).TotalSeconds));
            return new CheckConcurrencyWindow(from, to, to > from ? seconds / (to - from) : 0);
        }).ToArray();
        var lanes = jobs.GroupBy(job => job.Lane).OrderBy(group => group.Key, StringComparer.Ordinal).Select(group =>
        {
            var names = group.Select(job => job.Name).ToHashSet(StringComparer.Ordinal);
            var members = active.Where(row => names.Contains(row.Name)).ToArray();
            double seconds = members.Sum(row => row.Seconds);
            return new CheckLaneConcurrency(group.Key, seconds, wallSeconds > 0 ? seconds / wallSeconds : 0, Peak(members));
        }).ToArray();
        double total = active.Sum(row => row.Seconds);
        return new(critical.Names ?? [], critical.Seconds, total, wallSeconds > 0 ? total / wallSeconds : 0, Peak(active), lanes, windows);
    }

    private static CheckTiming Execute(CheckJob job, string logs)
    {
        var timer = Stopwatch.StartNew();
        var started = DateTime.UtcNow;
        using var resources = CheckResourceMeter.Start();
        try
        {
            using var affinity = CheckAffinity.Enter(job.Affinity);
            deadline.Value = (job.Name, started + job.Timeout);
            evidenceDirectory.Value = Path.Combine(logs, job.Name + "-processes");
            var cached = job.NightlyCoverage != null ? job.NightlyCoverage.TryReuse() : job.ResultCache?.TryReuse(job.Name);
            if (cached != null)
            {
                if (DateTime.UtcNow >= deadline.Value.Value.Deadline) throw new ReleaseException("Check job timed out: " + job.Name);
                File.WriteAllText(Path.Combine(logs, job.Name + ".log"), JsonSerializer.Serialize(cached) + "\n");
                return cached with { Seconds = timer.Elapsed.TotalSeconds, StartedAtUtc = started };
            }
            var result = job.Run();
            if (DateTime.UtcNow >= deadline.Value.Value.Deadline) throw new ReleaseException("Check job timed out: " + job.Name);
            File.WriteAllText(Path.Combine(logs, job.Name + ".log"), result.StandardOutput + result.StandardError);
            var row = new CheckTiming(job.Name, result.ExitCode == 0 ? job.CiCoverage == null ? "passed" : "covered by CI" : "failed", result.ExitCode, timer.Elapsed.TotalSeconds,
                result.ExitCode == 0 ? null : (result.StandardError.Length == 0 ? result.StandardOutput : result.StandardError).Trim())
                { StartedAtUtc = started, CpuLoad = CheckLoad.Current?.Between(started, DateTime.UtcNow), CiCoverage = job.CiCoverage,
                    Resources = resources.Usage(timer.Elapsed.TotalSeconds), AffinityMask = job.Affinity?.ToString("X") };
            var recorded = job.NightlyCoverage != null ? job.NightlyCoverage.Record(row) : job.ResultCache?.Record(row, Path.Combine(logs, job.Name + ".log")) ?? row;
            if (DateTime.UtcNow >= deadline.Value.Value.Deadline) throw new ReleaseException("Check job timed out: " + job.Name);
            return recorded;
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(logs, job.Name + ".log"), ex.ToString());
            return new(job.Name, "failed", 1, timer.Elapsed.TotalSeconds, ex.Message) { StartedAtUtc = started, CpuLoad = CheckLoad.Current?.Between(started, DateTime.UtcNow),
                Resources = resources.Usage(timer.Elapsed.TotalSeconds), AffinityMask = job.Affinity?.ToString("X") };
        }
        finally { deadline.Value = null; evidenceDirectory.Value = null; }
    }

    internal static void Validate(IReadOnlyList<CheckJob> jobs, int slots, IReadOnlyDictionary<string, int>? lanes = null, IReadOnlyList<string>? prerequisites = null)
    {
        lanes ??= new Dictionary<string, int> { ["general"] = slots };
        if (slots < 1) throw new ReleaseException("-MaxParallelism must be at least one", 64);
        var names = jobs.Select(job => job.Name).ToHashSet(StringComparer.Ordinal);
        if (names.Count != jobs.Count) throw new ReleaseException("Duplicate check names", 64);
        foreach (var name in prerequisites ?? [])
            if (!names.Add(name)) throw new ReleaseException("Duplicate completed check: " + name, 64);
        foreach (var job in jobs)
            if (job.Timeout <= TimeSpan.Zero || !lanes.TryGetValue(job.Lane, out var capacity) || job.Slots < 1 || job.Slots > capacity || job.Dependencies.Any(dep => !names.Contains(dep)))
                throw new ReleaseException("Invalid check resources/dependencies: " + job.Name, 64);
        var visited = new HashSet<string>(prerequisites ?? [], StringComparer.Ordinal);
        while (visited.Count < names.Count)
        {
            var ready = jobs.Where(job => !visited.Contains(job.Name) && job.Dependencies.All(visited.Contains)).ToArray();
            if (ready.Length == 0) throw new ReleaseException("Check dependency cycle", 64);
            foreach (var job in ready) visited.Add(job.Name);
        }
    }
}

internal sealed record CheckConcurrencyWindow(double FromSeconds, double ToSeconds, double AverageConcurrency);
internal sealed record CheckLaneConcurrency(string Lane, double JobSeconds, double AverageConcurrency, int PeakConcurrency);
internal sealed record CheckPerformance(string[] CriticalPath, double CriticalPathSeconds, double JobSeconds,
    double AverageConcurrency, int PeakConcurrency, CheckLaneConcurrency[] Lanes, CheckConcurrencyWindow[] Windows);

internal sealed record CheckCpuProcess(int Pid, string Name, double CpuSeconds, bool Owned);
internal sealed record CheckCpuSample(DateTime AtUtc, double Seconds, CheckCpuProcess[] Processes, int Unreadable);
internal sealed record CheckCpuLoad(double OwnCoreEquivalent, double OtherCoreEquivalent, CheckCpuProcess[] OtherProcesses, int Unreadable);

internal sealed record CheckResourceUsage(double CpuSeconds, double WallSeconds, ulong PeakMemoryBytes, string Source)
{
    public double CoreDemand => WallSeconds > 0 ? CpuSeconds / WallSeconds : 0;
}

internal sealed record CheckPacking(ulong General, double CpuPerJob, ulong MemoryPerJob, ulong MemoryBudget, int Cap)
{
    internal ulong Available { get; init; }
    [System.Text.Json.Serialization.JsonIgnore]
    internal IReadOnlyDictionary<string, CheckResourceUsage> History { get; init; } = new Dictionary<string, CheckResourceUsage>();
    internal bool MeasuredMemory { get; init; } = true;
    internal int Parallelism => Math.Min(History.Count == 0 ? Capacity(Available != 0 ? Available : General) : Cap,
        Math.Max(1, System.Numerics.BitOperations.PopCount(Available != 0 ? Available : General)));
    internal double Cpu(CheckJob job) => History.TryGetValue(job.Name, out var row) && row.WallSeconds >= 1 && row.CpuSeconds > 0
        ? Math.Max(1, row.CoreDemand) : Math.Max(1, CpuPerJob);
    internal ulong Memory(CheckJob job) => MeasuredMemory && History.TryGetValue(job.Name, out var row) && row.PeakMemoryBytes > 0
        ? Math.Max(256UL << 20, row.PeakMemoryBytes) : MemoryPerJob;
    internal bool MemoryFits(CheckJob job, IEnumerable<CheckJob> running)
    {
        var active = running.ToArray();
        return active.Sum(row => (double)Memory(row)) + Memory(job) <= MemoryBudget;
    }

    internal bool CanAdmit(CheckJob job, ulong mask, IEnumerable<CheckJob> running)
    {
        var active = running.ToArray();
        if (job.Lane == "general" && active.Where(row => row.Lane == "general").Sum(row => row.Slots) + job.Slots > Cap) return false;
        // Known commands reserve their own measured demand, not the p95 peak of
        // an unrelated heavy shard. Every command reserves at least one core:
        // sub-core averages measured under contention cannot justify more jobs.
        // The same limits apply to the first command and to borrowed CPUs.
        var competing = active.Where(row => row.Lane == job.Lane || (row.Affinity.GetValueOrDefault() & mask) != 0).ToArray();
        bool cpu = competing.Sum(Cpu) + Cpu(job) <= System.Numerics.BitOperations.PopCount(mask);
        return cpu && MemoryFits(job, active);
    }

    internal static Dictionary<string, double> ReadOrderCosts(string root, string? baseline)
    {
        var directory = Path.Combine(root, "bin-build/check-timings");
        var maximums = Path.Combine(directory, "check-costs.json");
        var costs = File.Exists(maximums) ? JsonSerializer.Deserialize<Dictionary<string, double>>(File.ReadAllText(maximums))! : new();
        // Existing worktrees already have successful command wall times in the
        // resource history. Seed them before the first ordering-history write.
        foreach (var (name, row) in Read(root, null).Where(pair => pair.Value.WallSeconds > 0)) costs[name] = row.WallSeconds;
        var path = Path.Combine(directory, "check-order-costs.json");
        if (File.Exists(path))
            foreach (var (name, seconds) in JsonSerializer.Deserialize<Dictionary<string, double>>(File.ReadAllText(path))!) costs[name] = seconds;
        if (baseline == null) return costs;
        using var report = JsonDocument.Parse(File.ReadAllText(baseline));
        foreach (var row in report.RootElement.GetProperty("checks").Deserialize<CheckTiming[]>()!.Where(row => row.Status == "passed" && row.Seconds > 0))
            costs[row.Name] = row.Seconds;
        return costs;
    }
    internal static int ReservedCores(IEnumerable<CheckJob> jobs, IReadOnlyDictionary<string, CheckResourceUsage> history, int workers, bool exclusive = true)
    {
        var groups = jobs.GroupBy(job => job.SensitiveGroup ?? job.Name).ToArray();
        if (groups.Length == 0) return 1;
        double demand = groups.SelectMany(group => group).Select(job => history.GetValueOrDefault(job.Name)?.CoreDemand ?? 1).Max();
        int count = Math.Min(workers, groups.Length);
        return exclusive ? count * Math.Max(2, (int)Math.Ceiling(demand)) : Math.Max(2, (int)Math.Ceiling(count * demand));
    }
    internal int Capacity(ulong mask, int reservedJobs = 0) => Math.Max(1, Math.Min(Cap, Math.Min(
        (int)Math.Ceiling(System.Numerics.BitOperations.PopCount(mask) / Math.Max(.05, CpuPerJob)),
        (int)Math.Min(int.MaxValue, MemoryBudget / Math.Max(1UL, MemoryPerJob)) - reservedJobs)));

    internal static CheckPacking Plan(ulong general, IEnumerable<CheckResourceUsage> history, ulong memory, int cap)
    {
        var rows = history.Where(row => row.WallSeconds >= 1 && row.CpuSeconds > 0).ToArray();
        double wall = rows.Sum(row => row.WallSeconds);
        double demand = wall > 0 ? rows.Sum(row => row.CpuSeconds) / wall : 1;
        var peaks = rows.Where(row => row.PeakMemoryBytes > 0).Select(row => row.PeakMemoryBytes).Order().ToArray();
        ulong peak = peaks.Length == 0 ? 512UL << 20 : peaks[(int)Math.Ceiling(peaks.Length * .95) - 1];
        return new(general, demand, Math.Max(256UL << 20, peak), memory / 2, cap);
    }

    internal static Dictionary<string, CheckResourceUsage> Read(string root, string? baseline)
    {
        var path = Path.Combine(root, "bin-build/check-timings/check-resources.json");
        var rows = File.Exists(path) ? JsonSerializer.Deserialize<Dictionary<string, CheckResourceUsage>>(File.ReadAllText(path))! : new();
        if (baseline == null) return rows;
        using var document = JsonDocument.Parse(File.ReadAllText(baseline));
        var checks = document.RootElement.GetProperty("checks").Deserialize<CheckTiming[]>()!;
        foreach (var row in checks.Where(row => row.Seconds >= 1 && row.Status == "passed"))
        {
            if (row.Resources is { CpuSeconds: > 0 } measured) { rows[row.Name] = measured; continue; }
            if (rows.ContainsKey(row.Name) || row.CpuLoad == null) continue;
            // Older reports sampled the WHOLE runner, not individual jobs.
            // Apportion that measured demand by time-weighted overlap and label
            // it explicitly; never treat the overlapping totals as job-owned CPU.
            double overlap = checks.Where(other => other.StartedAtUtc != default).Sum(other => Math.Max(0,
                (new[] { row.StartedAtUtc.AddSeconds(row.Seconds), other.StartedAtUtc.AddSeconds(other.Seconds) }.Min() -
                 new[] { row.StartedAtUtc, other.StartedAtUtc }.Max()).TotalSeconds)) / row.Seconds;
            rows[row.Name] = new(row.CpuLoad.OwnCoreEquivalent * row.Seconds / Math.Max(1, overlap), row.Seconds, 0, "legacy runner CPU apportioned by overlapping job-seconds");
        }
        return rows;
    }
}

// Only this scheduled command's kernel job accounting is attributed to it.
// Concurrent commands, the runner sampler and unrelated processes are excluded.
internal sealed class CheckResourceMeter : IDisposable
{
    private static readonly AsyncLocal<CheckResourceMeter?> current = new();
    private readonly CheckResourceMeter? previous = current.Value;
    private readonly object sync = new();
    private double cpu;
    private ulong memory;
    internal static CheckResourceMeter Start() { var meter = new CheckResourceMeter(); current.Value = meter; return meter; }
    internal static IDisposable Observe(Microsoft.Win32.SafeHandles.SafeFileHandle? job) => new Observation(current.Value, job);
    private sealed class Observation(CheckResourceMeter? meter, Microsoft.Win32.SafeHandles.SafeFileHandle? job) : IDisposable
    {
        public void Dispose()
        {
            if (meter == null || job == null) return;
            var usage = CheckAffinity.Accounting(job);
            lock (meter.sync) { meter.cpu += usage.CpuSeconds; meter.memory += usage.PeakMemoryBytes; }
        }
    }
    internal CheckResourceUsage Usage(double wall) { lock (sync) return new(cpu, wall, memory, "owned command jobs (kernel CPU and sum of peak committed memory)"); }
    public void Dispose() => current.Value = previous;
}

// Sample deltas, not accumulated process CPU since login. Descendants (including
// test hosts and fixture workers) belong to this run. Unreadable/short-lived
// processes are recorded as a limitation rather than inferred to be idle.
internal sealed class CheckLoad : IDisposable
{
    internal static CheckLoad? Current { get; private set; }
    private readonly string output;
    private readonly object sync = new();
    private readonly List<CheckCpuSample> samples = [];
    private readonly HashSet<int> owned = [Environment.ProcessId];
    private Dictionary<int, (DateTime Start, double Cpu)> previous = [];
    private DateTime sampled = DateTime.UtcNow;
    private readonly Timer timer;
    private CheckLoad(string output)
    {
        this.output = output; Sample(); timer = new Timer(_ => Sample(), null, 1000, 1000);
    }
    internal static CheckLoad Start(string output) => Current = new(output);
    internal void Register(int pid) { lock (sync) owned.Add(pid); }
    private void Sample()
    {
        lock (sync)
        {
            var now = DateTime.UtcNow; var next = new Dictionary<int, (DateTime, double)>();
            var rows = new List<CheckCpuProcess>(); int unreadable = 0;
            var live = Process.GetProcesses(); var parents = new Dictionary<int, int>();
            // Establish ancestry before counting deltas. PID enumeration order
            // must not label a grandchild as unrelated concurrent CPU load.
            if (OperatingSystem.IsWindows())
            {
                foreach (var process in live)
                    try
                    {
                        if (process.Id != 0 && NtQueryInformationProcess(process.Handle, 0, out var info, Marshal.SizeOf<BasicInfo>(), out _) == 0)
                            parents[process.Id] = info.Parent.ToInt32();
                    }
                    catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException) { }
                bool changed;
                do
                {
                    changed = false;
                    foreach (var (pid, parent) in parents)
                        if (!owned.Contains(pid) && owned.Contains(parent)) { owned.Add(pid); changed = true; }
                } while (changed);
            }
            foreach (var process in live)
                using (process)
                {
                    if (process.Id == 0) continue;
                    try
                    {
                        var start = process.StartTime.ToUniversalTime(); var cpu = process.TotalProcessorTime.TotalSeconds;
                        next[process.Id] = (start, cpu);
                        bool ours = owned.Contains(process.Id);
                        if (ours) owned.Add(process.Id);
                        var delta = previous.TryGetValue(process.Id, out var old) && old.Start == start ? Math.Max(0, cpu - old.Cpu) : start >= sampled ? cpu : 0;
                        if (delta > 0) rows.Add(new(process.Id, process.ProcessName, delta, ours));
                    }
                    catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException) { unreadable++; }
                }
            samples.Add(new(now, (now - sampled).TotalSeconds, rows.ToArray(), unreadable)); previous = next; sampled = now;
        }
    }
    internal CheckCpuLoad Between(DateTime start, DateTime end)
    {
        lock (sync)
        {
            var rows = samples.Where(s => s.AtUtc >= start && s.AtUtc <= end).ToArray();
            double seconds = rows.Sum(s => s.Seconds); var processes = rows.SelectMany(s => s.Processes).ToArray();
            var others = processes.Where(p => !p.Owned).GroupBy(p => (p.Pid, p.Name)).Select(g => new CheckCpuProcess(g.Key.Pid, g.Key.Name, g.Sum(p => p.CpuSeconds), false)).OrderByDescending(p => p.CpuSeconds).Take(12).ToArray();
            return new(seconds == 0 ? 0 : processes.Where(p => p.Owned).Sum(p => p.CpuSeconds) / seconds,
                seconds == 0 ? 0 : processes.Where(p => !p.Owned).Sum(p => p.CpuSeconds) / seconds, others, rows.Select(s => s.Unreadable).DefaultIfEmpty().Max());
        }
    }
    public void Dispose()
    {
        timer.Dispose(); Sample(); lock (sync) File.WriteAllText(output, JsonSerializer.Serialize(new { logicalCores = Environment.ProcessorCount,
            limitation = "One-second live-process samples; exited processes between samples and unreadable processes can undercount CPU.", samples }, new JsonSerializerOptions { WriteIndented = true }) + "\n");
        Current = null;
    }
    [StructLayout(LayoutKind.Sequential)] private struct BasicInfo { internal IntPtr Exit, Peb, Affinity, Priority, Pid, Parent; }
    [DllImport("ntdll.dll")] private static extern int NtQueryInformationProcess(IntPtr handle, int kind, out BasicInfo info, int size, out int returned);
}
