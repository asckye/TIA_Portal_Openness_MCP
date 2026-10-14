using System.Diagnostics;
using System.Text.Json;
using System.Runtime.InteropServices;

namespace TiaMcp.ReleaseTool;

internal sealed record CheckJob(string Name, Func<CommandResult> Run, string[] Dependencies, int Slots = 1, bool Exclusive = false)
{
    internal double EstimatedSeconds { get; init; }
    internal string Lane { get; init; } = "general";
    internal ulong? Affinity { get; init; }
    internal bool AllowFailedDependencies { get; init; }
}
internal sealed record CheckTiming(string Name, string Status, int ExitCode, double Seconds, string? Error)
{
    public DateTime StartedAtUtc { get; init; }
    public CheckCpuLoad? CpuLoad { get; init; }
}

// Unlike the release publisher's fail-fast scheduler, collect every independent
// failure here. A failed prerequisite blocks its dependents and makes the run red.
internal static class CheckScheduler
{
    internal static IReadOnlyList<CheckTiming> Run(IReadOnlyList<CheckJob> jobs, int slots, string logs, IReadOnlyDictionary<string, int>? lanes = null)
    {
        lanes ??= new Dictionary<string, int> { ["general"] = slots };
        Validate(jobs, slots, lanes);
        Directory.CreateDirectory(logs);
        var priorities = CriticalCosts(jobs);
        var pending = jobs.OrderByDescending(job => priorities[job.Name]).ToList();
        var running = new Dictionary<Task<CheckTiming>, CheckJob>();
        var done = new Dictionary<string, CheckTiming>(StringComparer.Ordinal);
        while (pending.Count > 0 || running.Count > 0)
        {
            foreach (var job in pending.ToArray())
            {
                if (!job.AllowFailedDependencies && job.Dependencies.Any(dep => done.TryGetValue(dep, out var row) && row.Status != "passed"))
                {
                    var row = new CheckTiming(job.Name, "blocked", 1, 0, "Failed prerequisite: " + string.Join(", ", job.Dependencies.Where(dep => done.TryGetValue(dep, out var r) && r.Status != "passed")));
                    done.Add(job.Name, row); pending.Remove(job);
                    File.WriteAllText(Path.Combine(logs, job.Name + ".log"), row.Error + "\n");
                }
            }
            // Ready inputs of an idle reserved lane must not wait behind the
            // general backlog while their dedicated CPUs sit unused.
            bool FeedsIdleLane(CheckJob input) => pending.Any(next => next.Lane != input.Lane &&
                next.Dependencies.Contains(input.Name, StringComparer.Ordinal) && !running.Values.Any(active => active.Lane == next.Lane));
            foreach (var job in pending.OrderByDescending(FeedsIdleLane).ThenByDescending(job => priorities[job.Name]).ToArray())
            {
                if (!job.Dependencies.All(done.ContainsKey)) continue;
                var lane = running.Values.Where(row => row.Lane == job.Lane).ToArray();
                if (lane.Any(row => row.Exclusive)) continue;
                if (job.Exclusive && lane.Length != 0) continue;
                if (lane.Sum(row => row.Slots) + job.Slots > lanes[job.Lane]) continue;
                pending.Remove(job);
                running.Add(Task.Run(() => Execute(job, logs)), job);
            }
            if (running.Count == 0)
            {
                if (pending.Count != 0) throw new ReleaseException("Check scheduler cannot make progress.");
                break;
            }
            var finished = Task.WhenAny(running.Keys).GetAwaiter().GetResult();
            var result = finished.GetAwaiter().GetResult();
            done.Add(result.Name, result); running.Remove(finished);
            Console.WriteLine($"{result.Name}: {result.Status}; {result.Seconds:F3}s");
        }
        var timings = jobs.Select(job => done[job.Name]).ToArray();
        File.WriteAllText(Path.Combine(logs, "timings.json"), JsonSerializer.Serialize(timings, new JsonSerializerOptions { WriteIndented = true }) + "\n");
        return timings;
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

    private static CheckTiming Execute(CheckJob job, string logs)
    {
        var timer = Stopwatch.StartNew();
        var started = DateTime.UtcNow;
        try
        {
            using var affinity = CheckAffinity.Enter(job.Affinity);
            var result = job.Run();
            File.WriteAllText(Path.Combine(logs, job.Name + ".log"), result.StandardOutput + result.StandardError);
            return new(job.Name, result.ExitCode == 0 ? "passed" : "failed", result.ExitCode, timer.Elapsed.TotalSeconds,
                result.ExitCode == 0 ? null : (result.StandardError.Length == 0 ? result.StandardOutput : result.StandardError).Trim())
                { StartedAtUtc = started, CpuLoad = CheckLoad.Current?.Between(started, DateTime.UtcNow) };
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(logs, job.Name + ".log"), ex.ToString());
            return new(job.Name, "failed", 1, timer.Elapsed.TotalSeconds, ex.Message) { StartedAtUtc = started, CpuLoad = CheckLoad.Current?.Between(started, DateTime.UtcNow) };
        }
    }

    internal static void Validate(IReadOnlyList<CheckJob> jobs, int slots, IReadOnlyDictionary<string, int>? lanes = null)
    {
        lanes ??= new Dictionary<string, int> { ["general"] = slots };
        if (slots < 1) throw new ReleaseException("-MaxParallelism must be at least one", 64);
        var names = jobs.Select(job => job.Name).ToHashSet(StringComparer.Ordinal);
        if (names.Count != jobs.Count) throw new ReleaseException("Duplicate check names", 64);
        foreach (var job in jobs)
            if (!lanes.TryGetValue(job.Lane, out var capacity) || job.Slots < 1 || job.Slots > capacity || job.Dependencies.Any(dep => !names.Contains(dep)))
                throw new ReleaseException("Invalid check resources/dependencies: " + job.Name, 64);
        var visited = new HashSet<string>();
        while (visited.Count < jobs.Count)
        {
            var ready = jobs.Where(job => !visited.Contains(job.Name) && job.Dependencies.All(visited.Contains)).ToArray();
            if (ready.Length == 0) throw new ReleaseException("Check dependency cycle", 64);
            foreach (var job in ready) visited.Add(job.Name);
        }
    }
}

internal sealed record CheckCpuProcess(int Pid, string Name, double CpuSeconds, bool Owned);
internal sealed record CheckCpuSample(DateTime AtUtc, double Seconds, CheckCpuProcess[] Processes, int Unreadable);
internal sealed record CheckCpuLoad(double OwnCoreEquivalent, double OtherCoreEquivalent, CheckCpuProcess[] OtherProcesses, int Unreadable);

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
