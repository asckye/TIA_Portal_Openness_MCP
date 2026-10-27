using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace TiaMcp.ReleaseTool;

internal sealed record CheckChild(string Executable, string[] Arguments, string Directory, Dictionary<string, string?>? Environment, string? Input, string? CleanupJob);

internal static class CheckAffinity
{
    private static readonly object runnerGate = new();
    private static SafeFileHandle? runnerJob;
    private static bool runnerReady;
    internal const string RunnerJobVariable = "TIA_MCP_RUNNER_JOB";
    internal const string CommandJobVariable = "TIA_MCP_COMMAND_JOB";
    private const string BaselineWorkersVariable = "TIA_MCP_RUNNER_MIN_WORKERS";

    internal static void EnsureRunnerJob()
    {
        if (!OperatingSystem.IsWindows()) return;
        lock (runnerGate)
        {
            if (runnerReady) { GC.KeepAlive(runnerJob); return; }
            using var process = Process.GetCurrentProcess();
            if (Environment.GetEnvironmentVariable(RunnerJobVariable) is { } inherited)
            {
                using var job = OpenJobObject(4, false, inherited);
                if (!job.IsInvalid && IsMember(job, process) && (LimitFlags(job) & 0x3800u) == 0x2000u)
                {
                    if (!int.TryParse(Environment.GetEnvironmentVariable(BaselineWorkersVariable), out int workers) || workers < 1) SaveBaselineWorkers();
                    runnerReady = true; return;
                }
            }
            var name = "Local\\TIA-Runner-" + Guid.NewGuid().ToString("N");
            var owned = Create(null, name);
            try
            {
                if (!AssignProcessToJobObject(owned, process.Handle)) throw new Win32Exception(Marshal.GetLastWin32Error());
                // Keep the owner's non-inherited handle alive until OS process exit.
                // Closing it earlier would also terminate this owning process.
                runnerJob = owned;
                SaveBaselineWorkers();
                Environment.SetEnvironmentVariable(RunnerJobVariable, name);
                runnerReady = true;
            }
            catch { owned.Dispose(); throw; }
        }
    }

    private static void SaveBaselineWorkers()
    {
        ThreadPool.GetMinThreads(out int workers, out _);
        Environment.SetEnvironmentVariable(BaselineWorkersVariable, workers.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    internal static int BaselineWorkers => int.Parse(Environment.GetEnvironmentVariable(BaselineWorkersVariable)!, System.Globalization.CultureInfo.InvariantCulture);

    internal static void ConfigureRuntime(IDictionary<string, string?> environment, ulong mask)
    {
        int processors = System.Numerics.BitOperations.PopCount(mask);
        environment["DOTNET_PROCESSOR_COUNT"] = processors.ToString(System.Globalization.CultureInfo.InvariantCulture);
        // Reserved affinity must not also shrink the pre-existing I/O worker
        // capacity. Blocking pipe/HTTP fixtures otherwise starve their callbacks.
        // The kernel CPU cap and every fixture deadline remain unchanged.
        if (BaselineWorkers > processors &&
            !environment.ContainsKey("DOTNET_ThreadPool_ForceMinWorkerThreads") && !environment.ContainsKey("COMPlus_ThreadPool_ForceMinWorkerThreads"))
        {
            var minimum = BaselineWorkers.ToString("x", System.Globalization.CultureInfo.InvariantCulture);
            environment["DOTNET_ThreadPool_ForceMinWorkerThreads"] = minimum;
            environment["COMPlus_ThreadPool_ForceMinWorkerThreads"] = minimum;
        }
    }

    internal static SafeFileHandle Create(ulong? mask = null, string? name = null)
    {
        var job = CreateJobObject(IntPtr.Zero, name);
        try
        {
            // Neither BREAKAWAY_OK nor SILENT_BREAKAWAY_OK is enabled. Children
            // inherit both the runner job and any nested command/affinity job.
            var limits = new ExtendedLimits { Basic = new BasicLimits {
                LimitFlags = 0x2000u | (mask == null ? 0u : 0x10u), Affinity = new UIntPtr(mask ?? 0) } };
            if (job.IsInvalid || !SetInformationJobObject(job, 9, ref limits, Marshal.SizeOf<ExtendedLimits>()))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            return job;
        }
        catch { job.Dispose(); throw; }
    }

    private static bool HasExited(Process process)
    {
        try { return process.HasExited; }
        catch (InvalidOperationException) { return true; } // No process object is left to open.
        catch (Win32Exception) { return false; }
    }

    internal static bool IsMember(SafeFileHandle job, Process process)
    {
        if (!IsProcessInJob(process.Handle, job, out bool member)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return member;
    }

    internal static uint LimitFlags(SafeFileHandle job)
    {
        int size = Marshal.SizeOf<ExtendedLimits>(); var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (!QueryInformationJobObject(job, 9, buffer, size, out _)) throw new Win32Exception(Marshal.GetLastWin32Error());
            return Marshal.PtrToStructure<ExtendedLimits>(buffer).Basic.LimitFlags;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    internal sealed record JobProcess(int Pid, DateTime? StartTimeUtc, string? ImagePath, bool Member, string? Error = null);
    internal static JobProcess Observe(SafeFileHandle job, Process process) => new(process.Id, process.StartTime.ToUniversalTime(), process.MainModule?.FileName, IsMember(job, process));

    internal static JobProcess[] Snapshot(SafeFileHandle job) => JobProcesses(job).Select(pid => {
        try { using var process = Process.GetProcessById(pid); return Observe(job, process); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
        { return new JobProcess(pid, null, null, false, ex.Message); }
    }).ToArray();

    private static void Record(SafeFileHandle job, string reason, string? evidence)
    {
        evidence ??= Path.Combine(OwnershipRoot, "bin-build/process-jobs", Guid.NewGuid().ToString("N") + ".json");
        ReleaseCommands.RequireCheckAllOwnedPath(OwnershipRoot, evidence);
        Directory.CreateDirectory(Path.GetDirectoryName(evidence)!);
        var contents = JsonSerializer.Serialize(new { observedUtc = DateTime.UtcNow, reason, processes = Snapshot(job) }, new JsonSerializerOptions { WriteIndented = true }) + "\n";
        // Persist the membership snapshot before any termination, not afterwards.
        File.WriteAllText(evidence, contents);
    }

    internal static void StopJobProcess(SafeFileHandle job, Process process)
    {
        if (process.HasExited) return;
        if (!IsMember(job, process)) throw new ReleaseException("Refusing to terminate a process outside the owned job: " + process.Id);
        Record(job, "owned member cleanup", null);
        process.Kill();
    }

    internal static void StopOwnedProcess(Process process, string root)
    {
        if (OperatingSystem.IsWindows())
        {
            using var job = OpenJobObject(4, false, Environment.GetEnvironmentVariable(RunnerJobVariable)!);
            StopJobProcess(job, process);
            return;
        }
        if (process.HasExited) return;
        var prefix = Path.GetFullPath(root).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        if (!(process.MainModule?.FileName?.StartsWith(prefix, StringComparison.Ordinal) == true) &&
            !File.ReadAllText("/proc/" + process.Id + "/cmdline").Contains(prefix, StringComparison.Ordinal))
            throw new ReleaseException("Owned child executable/command is outside this worktree: " + process.Id);
        process.Kill();
    }

    internal static string OwnershipRoot => Environment.GetEnvironmentVariable("TIA_MCP_CHECK_ORIGINAL_ROOT") ?? TiaMcp.BuildCommon.Repository.FindRoot();
    private static readonly AsyncLocal<ulong?> current = new();
    internal static ulong? Current => current.Value;
    internal static IDisposable Enter(ulong? mask)
    {
        var previous = current.Value; current.Value = mask;
        return new Scope(() => current.Value = previous);
    }
    private sealed class Scope(Action dispose) : IDisposable { public void Dispose() => dispose(); }

    internal static (ulong Stability, ulong General) Split(ulong available, int reserved)
    {
        var cores = Enumerable.Range(0, 64).Where(bit => (available & (1UL << bit)) != 0).ToArray();
        if (reserved < 1 || reserved >= cores.Length) throw new ReleaseException("-StabilityCores must reserve at least one core and leave at least one for other checks", 64);
        ulong stability = cores.Take(reserved).Aggregate(0UL, (mask, bit) => mask | (1UL << bit));
        return (stability, available & ~stability);
    }

    private const string WholeCoreRefusal = "-StabilityCores must fit whole available physical cores and leave CPUs for other checks";

    internal static (ulong Stability, ulong General) SplitPreferred(ulong available, int reserved, ulong[] physicalCores) =>
        TrySplitPreferred(available, reserved, physicalCores) ?? throw new ReleaseException(WholeCoreRefusal, 64);

    private static (ulong Stability, ulong General)? TrySplitPreferred(ulong available, int reserved, IEnumerable<ulong> physicalCores)
    {
        ulong stability = 0;
        foreach (var physical in physicalCores)
        {
            var mask = physical & available;
            if (System.Numerics.BitOperations.PopCount(stability | mask) > reserved) continue;
            stability |= mask;
            if (System.Numerics.BitOperations.PopCount(stability) == reserved) break;
        }
        return System.Numerics.BitOperations.PopCount(stability) != reserved || stability == available ? null : (stability, available & ~stability);
    }

    internal static ulong[] PreferredPhysicalCores() =>
        PhysicalCores().OrderByDescending(core => core.Efficiency).ThenBy(core => core.Mask).Select(core => core.Mask).ToArray();

    internal static (ulong Stability, ulong General) SplitForMachine(ulong available, int reserved) =>
        SplitPreferred(available, reserved, PreferredPhysicalCores());

    internal const string FullIsolation = "full", DegradedIsolation = "degraded";

    // Sensitive is the reserved sensitive lane's mask; zero means no reserved
    // lane, and the sensitive checks then run alone in the general lane.
    internal sealed record CheckAllCpuLayout(ulong Sensitive, ulong Stability, ulong General, string Isolation, string? Reason);

    // The check-all layout: the sensitive lane takes whole physical cores first,
    // the stability lane whole cores from the rest, general work what remains.
    // When the requested sizes cannot all be held with a general CPU left, the
    // first fitting layout below runs and is recorded as degraded:
    //   1. both lanes resized to the nearest whole-core sizes, still disjoint;
    //   2. no reserved sensitive lane (as before P8-72): sensitive checks run alone
    //      in the general lane, the stability lane keeps whole cores;
    //   3. the sensitive lane keeps whole cores, stability shares the general CPUs;
    //   4. one physical core: no reserved lanes, everything shares all CPUs.
    // An explicit -SensitiveCores/-StabilityCores keeps its exact size and is never
    // shared; both explicit keep the strict refusal. No check changes either way.
    internal static CheckAllCpuLayout PlanCheckAll(ulong available, int sensitiveCores, bool sensitiveExplicit, int stabilityCores, bool stabilityExplicit, ulong[] physicalCores)
    {
        if (TrySplitPreferred(available, sensitiveCores, physicalCores) is { } strictSensitive &&
            TrySplitPreferred(strictSensitive.General, stabilityCores, physicalCores) is { } strictRest)
            return new(strictSensitive.Stability, strictRest.Stability, strictRest.General, FullIsolation, null);
        if (sensitiveExplicit && stabilityExplicit) throw new ReleaseException(WholeCoreRefusal, 64);
        int logical = System.Numerics.BitOperations.PopCount(available);
        string Cpus(int count, string noun = "CPU") => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{count} {noun}{(count == 1 ? "" : "s")}");
        string Count(ulong mask) => Cpus(System.Numerics.BitOperations.PopCount(mask));
        string requested = string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"{Cpus(logical, "available CPU")} on {Cpus(physicalCores.Count(core => (core & available) != 0), "physical core")} cannot hold the {(sensitiveExplicit ? "requested" : "default")} " +
            $"{sensitiveCores}-CPU sensitive lane and the {(stabilityExplicit ? "requested" : "default")} {stabilityCores}-CPU stability lane on whole physical cores with a general CPU left");
        CheckAllCpuLayout Degraded(ulong sensitive, ulong stability, ulong general, string layout) =>
            new(sensitive, stability, general, DegradedIsolation, requested + "; " + string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{layout} (sensitive {sensitive:X}, stability {stability:X}, general {general:X})"));
        // Explicit sizes stay exact; defaults try the nearest sizes, smaller first on a tie.
        IEnumerable<int> Sizes(int size, bool exact) => exact ? [size] : Enumerable.Range(1, Math.Max(0, logical - 1)).OrderBy(k => Math.Abs(k - size)).ThenBy(k => k);
        foreach (int sensitive in Sizes(sensitiveCores, sensitiveExplicit))
            if (TrySplitPreferred(available, sensitive, physicalCores) is { } reserved)
                foreach (int stability in Sizes(stabilityCores, stabilityExplicit))
                    if (TrySplitPreferred(reserved.General, stability, physicalCores) is { } rest)
                        return Degraded(reserved.Stability, rest.Stability, rest.General,
                            $"lanes resized to whole physical cores and still disjoint: sensitive {Count(reserved.Stability)}, stability {Count(rest.Stability)}");
        if (!sensitiveExplicit)
            // The general lane, which then runs the sensitive checks, keeps the first choice of cores.
            foreach (int stability in Sizes(stabilityCores, stabilityExplicit))
                if (TrySplitPreferred(available, stability, Enumerable.Reverse(physicalCores)) is { } split)
                    return Degraded(0, split.Stability, split.General,
                        $"no reserved sensitive lane: each sensitive check runs alone in the general lane on its {Count(split.General)}; the stability lane keeps {Count(split.Stability)} on whole physical cores");
        if (!stabilityExplicit)
            foreach (int sensitive in Sizes(sensitiveCores, sensitiveExplicit))
                if (TrySplitPreferred(available, sensitive, physicalCores) is { } split)
                    return Degraded(split.Stability, split.General, split.General,
                        $"the stability lane shares the general lane's {Count(split.General)}; the sensitive lane keeps {Count(split.Stability)} on whole physical cores");
        if (!sensitiveExplicit && !stabilityExplicit && available != 0)
            return Degraded(0, available, available, "no reserved lanes: each sensitive check runs alone in the general lane and the stability lane shares every available CPU");
        var (option, value) = sensitiveExplicit ? ("SensitiveCores", sensitiveCores) : ("StabilityCores", stabilityCores);
        throw new ReleaseException(string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"-{option} {value} must fit whole available physical cores and leave CPUs for other checks (available CPU mask {available:X})"), 64);
    }

    internal static (ulong Stability, ulong General) SplitSensitive(ulong available, int reserved)
    {
        return SplitForMachine(available, reserved);
    }

    internal static int DefaultSensitiveCores(ulong available) =>
        Math.Min(8, Math.Max(1, System.Numerics.BitOperations.PopCount(available) / 2));

    internal static IReadOnlyDictionary<string, int> AssignSensitiveLanes(List<CheckJob> jobs, ulong mask, int workers, ulong[]? physicalCores = null)
    {
        int cores = System.Numerics.BitOperations.PopCount(mask);
        physicalCores ??= PreferredPhysicalCores();
        workers = Math.Min(workers, Math.Max(1, cores / 4));
        var masks = new ulong[workers];
        ulong remaining = mask;
        for (int i = 0; i < workers; i++)
        {
            masks[i] = i == workers - 1 ? remaining : SplitPreferred(remaining, cores / workers, physicalCores).Stability;
            remaining &= ~masks[i];
        }
        var loads = new double[workers];
        var capacities = Enumerable.Range(0, workers).ToDictionary(i => "sensitive-" + i, _ => 1);
        // Keep each worker exclusive on its own CPUs. Long sensitive suite
        // partitions can overlap without sharing a CPU with another runner lane.
        foreach (var group in jobs.Where(job => job.Lane == "sensitive").GroupBy(job => job.SensitiveGroup ?? job.Name)
            .OrderByDescending(group => group.OrderBy(job => job.Name, StringComparer.Ordinal).Sum(job => Math.Max(1, job.EstimatedSeconds)))
            .ThenBy(group => group.Key, StringComparer.Ordinal).ToArray())
        {
            int index = Array.IndexOf(loads, loads.Min());
            // Fixtures can write beside their assembly (adapter identities, for
            // example). Different registered filters of that assembly must not
            // become concurrent writers when a second worker is introduced.
            foreach (var job in group.OrderBy(job => job.Name, StringComparer.Ordinal))
            {
                jobs[jobs.IndexOf(job)] = job with { Lane = "sensitive-" + index, Affinity = masks[index] };
                loads[index] += Math.Max(1, job.EstimatedSeconds);
            }
        }
        return capacities;
    }

    // check-all lanes: exclusive checks go to the reserved sensitive workers, or
    // stay in the general lane, where each runs alone, when the layout reserves
    // no sensitive lane. Jobs without an affinity get their lane's CPUs.
    internal static IReadOnlyDictionary<string, int> AssignLanes(List<CheckJob> jobs, CheckAllCpuLayout layout, int sensitiveParallelism, ulong[]? physicalCores = null)
    {
        bool reserved = layout.Sensitive != 0;
        for (int i = 0; i < jobs.Count; i++)
        {
            var job = jobs[i];
            jobs[i] = job with
            {
                Lane = job.Exclusive && job.Lane == "general" && reserved ? "sensitive" : job.Lane,
                Affinity = job.Affinity ?? (job.Exclusive && reserved ? layout.Sensitive : layout.General)
            };
        }
        return reserved ? AssignSensitiveLanes(jobs, layout.Sensitive, sensitiveParallelism, physicalCores) : new Dictionary<string, int>();
    }

    internal static (ulong Mask, byte Efficiency)[] PhysicalCores()
    {
        uint length = 0; GetLogicalProcessorInformationEx(0, IntPtr.Zero, ref length);
        if (length == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        var buffer = Marshal.AllocHGlobal(checked((int)length));
        try
        {
            if (!GetLogicalProcessorInformationEx(0, buffer, ref length)) throw new Win32Exception(Marshal.GetLastWin32Error());
            var cores = new List<(ulong, byte)>();
            for (int offset = 0; offset < length;)
            {
                var entry = IntPtr.Add(buffer, offset); int size = Marshal.ReadInt32(entry, 4);
                int groups = (ushort)Marshal.ReadInt16(entry, 30);
                if (groups != 1 || Marshal.ReadInt16(entry, 40) != 0)
                    throw new ReleaseException("check-all affinity currently requires one Windows processor group", 64);
                cores.Add((unchecked((ulong)Marshal.ReadInt64(entry, 32)), Marshal.ReadByte(entry, 9)));
                offset += size;
            }
            return cores.ToArray();
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    internal static int Child()
    {
        // The parent assigns this waiting launcher to its affinity job before
        // sending the command. Thus even immediately spawned grandchildren are
        // in the job; Process.Start followed by assigning the actual test races.
        var child = JsonSerializer.Deserialize<CheckChild>(Console.In.ReadLine() ?? throw new ReleaseException("Missing check child command"))!;
        using var job = child.CleanupJob == null ? null : OpenJobObject(0x001F003F, false, child.CleanupJob);
        if (job?.IsInvalid == true) throw new Win32Exception(Marshal.GetLastWin32Error());
        var result = ProcessRunner.Run(child.Executable, child.Arguments, child.Directory, child.Environment, standardInput: child.Input, forwardOutput: true,
            afterExit: job == null ? null : () => StopAndWait(job, CheckAffinity.OwnershipRoot, Environment.ProcessId));
        return result.ExitCode;
    }

    internal static int Probe(string path)
    {
        ReleaseCommands.RequireCheckAllOwnedPath(CheckAffinity.OwnershipRoot, path);
        var fields = File.ReadAllLines(path);
        if (fields[0] == "runtime-policy")
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows runtime affinity verification");
            using var process = Process.GetCurrentProcess(); ThreadPool.GetMinThreads(out int workers, out _);
            File.WriteAllText(fields[1], JsonSerializer.Serialize(new { mask = unchecked((ulong)process.ProcessorAffinity.ToInt64()), processors = Environment.ProcessorCount, workers }));
            return 0;
        }
        if (fields[0] == "observe-job")
        {
            using var job = OpenJobObject(4, false, Environment.GetEnvironmentVariable(CommandJobVariable)!);
            using var process = Process.GetProcessById(int.Parse(fields[2]));
            var observation = Observe(job, process);
            File.WriteAllText(fields[1], JsonSerializer.Serialize(observation));
            return observation.Member ? 0 : 1;
        }
        if (fields[0] is "child" or "exit-after-input")
        {
            File.WriteAllText(fields[1], Environment.ProcessId.ToString());
            if (fields[0] == "exit-after-input") { Console.In.ReadLine(); return 0; }
            using var blocked = new ManualResetEventSlim(); blocked.Wait();
            return 0;
        }
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet") { UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in new[] { typeof(Program).Assembly.Location, "--check-probe", fields[1] }) start.ArgumentList.Add(argument);
        using var child = Process.Start(start) ?? throw new ReleaseException("Cannot start owned probe");
        if (!SpinWait.SpinUntil(() => File.Exists(fields[2]), TimeSpan.FromSeconds(10))) throw new ReleaseException("Owned child did not signal readiness");
        File.WriteAllText(fields[3], "parent exited after child readiness");
        Console.WriteLine(child.Id);
        if (fields[0] == "waiting-parent") { using var blocked = new ManualResetEventSlim(); blocked.Wait(); }
        return 0;
    }

    internal static SafeFileHandle Assign(Process process, ulong mask, string? name = null)
    {
        if (!OperatingSystem.IsWindows()) throw new ReleaseException("check-all CPU isolation requires Windows");
        EnsureRunnerJob();
        var job = Create(mask, name);
        try
        {
            if (!AssignProcessToJobObject(job, process.Handle)) throw new Win32Exception(Marshal.GetLastWin32Error());
            return job;
        }
        catch
        {
            job.Dispose();
            using var owner = OpenJobObject(4, false, Environment.GetEnvironmentVariable(RunnerJobVariable)!);
            StopJobProcess(owner, process);
            throw;
        }
    }

    internal static IDisposable CompleteOnDispose(SafeFileHandle job, string root) => new Scope(() => StopAndWait(job, root));

    internal static void StopAndWait(SafeFileHandle job, string root, int? exclude = null, bool grace = true, string? evidence = null,
        Func<int, Process>? findProcess = null)
    {
        // Keep the owned job open until all its processes have released their
        // working directories, including descendants whose parent already exited.
        var clock = Stopwatch.StartNew();
        var observed = new Dictionary<int, Process>();
        var lookup = findProcess ?? Process.GetProcessById;
        try
        {
            bool Application(int pid)
            {
                if (pid == exclude) return false;
                if (exclude == null) return true;
                Process? process = null;
                try
                {
                    process = lookup(pid);
                    // A launcher's own console broker cannot exit until the launcher
                    // returns. The outer runner still waits for it after that exit.
                    return !string.Equals(process.MainModule?.FileName, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "conhost.exe"), StringComparison.OrdinalIgnoreCase);
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { return false; }
                // Reading the modules of a process that is exiting fails with ERROR_PARTIAL_COPY;
                // it is gone only if it really has exited, otherwise the error stands.
                catch (Win32Exception) when (process != null && HasExited(process)) { return false; }
                finally { process?.Dispose(); }
            }
            int[] Remaining()
            {
                var members = JobProcesses(job).Where(Application).ToArray();
                foreach (int pid in members)
                {
                    if (observed.ContainsKey(pid)) continue;
                    Process? process = null;
                    try
                    {
                        process = lookup(pid);
                        // Opening the handle before termination also pins this PID's
                        // identity while the kernel finishes process rundown.
                        if (IsMember(job, process)) { observed.Add(pid, process); process = null; }
                    }
                    catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { } // Exited before lookup or handle observation.
                    finally { process?.Dispose(); }
                }
                return members;
            }
            while (grace && Remaining().Length != 0 && clock.Elapsed < TimeSpan.FromSeconds(5)) Thread.Sleep(25);
            var remaining = Remaining();
            if (remaining.Length == 0 && grace) { WaitForJobExit(Remaining, observed.Values, clock); return; }
            Record(job, grace ? "owned descendant cleanup" : "timeout", evidence);
            if (exclude == null)
            {
                // Membership is the ownership boundary, including system SDK nodes.
                if (!TerminateJobObject(job, 1)) throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            else foreach (int pid in remaining)
            {
                try
                {
                    using var process = lookup(pid);
                    StopJobProcess(job, process);
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { } // The observed member exited first.
            }
            WaitForJobExit(Remaining, observed.Values, clock);
        }
        finally { foreach (var process in observed.Values) process.Dispose(); }
    }

    internal static void WaitForJobExit(Func<int[]> remaining, IEnumerable<Process> observed, Stopwatch clock)
    {
        while (remaining().Length != 0)
        {
            if (clock.Elapsed > TimeSpan.FromSeconds(30)) throw new ReleaseException("Owned affinity job did not drain");
            Thread.Sleep(25);
        }
        // Job accounting can reach zero before a terminated process releases its
        // cwd and other resources. Wait on each retained handle as well.
        foreach (var process in observed)
            if (!process.WaitForExit((int)Math.Max(0, 30000 - clock.ElapsedMilliseconds)))
                throw new ReleaseException("Owned affinity job did not drain");
    }

    private static int[] JobProcesses(SafeFileHandle job)
    {
        for (int size = 1024; ; size *= 2)
        {
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                if (!QueryInformationJobObject(job, 3, buffer, size, out _))
                {
                    int error = Marshal.GetLastWin32Error();
                    if (error is 122 or 234) continue;
                    throw new Win32Exception(error);
                }
                int count = Marshal.ReadInt32(buffer, 4);
                return Enumerable.Range(0, count).Select(index => checked((int)Marshal.ReadIntPtr(buffer, 8 + index * IntPtr.Size))).ToArray();
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimits
    {
        internal long ProcessTime, JobTime;
        internal uint LimitFlags;
        internal UIntPtr MinimumWorkingSet, MaximumWorkingSet;
        internal uint ActiveProcesses;
        internal UIntPtr Affinity;
        internal uint Priority, Scheduling;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimits
    {
        internal BasicLimits Basic;
        internal ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes;
        internal UIntPtr ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
    }
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsProcessInJob(IntPtr process, SafeFileHandle job, [MarshalAs(UnmanagedType.Bool)] out bool member);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObject(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle OpenJobObject(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, string name);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int information, ref ExtendedLimits limits, int length);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateJobObject(SafeFileHandle job, uint code);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryInformationJobObject(SafeFileHandle job, int information, IntPtr buffer, int length, out int returned);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLogicalProcessorInformationEx(int relationship, IntPtr buffer, ref uint length);
}
