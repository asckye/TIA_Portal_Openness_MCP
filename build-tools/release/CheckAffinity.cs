using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace TiaMcp.ReleaseTool;

internal sealed record CheckChild(string Executable, string[] Arguments, string Directory, Dictionary<string, string?>? Environment, string? Input);

internal static class CheckAffinity
{
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

    internal static (ulong Stability, ulong General) SplitPreferred(ulong available, int reserved, ulong[] physicalCores)
    {
        ulong stability = 0;
        foreach (var physical in physicalCores)
        {
            var mask = physical & available;
            if (System.Numerics.BitOperations.PopCount(stability | mask) > reserved) continue;
            stability |= mask;
            if (System.Numerics.BitOperations.PopCount(stability) == reserved) break;
        }
        if (System.Numerics.BitOperations.PopCount(stability) != reserved || stability == available)
            throw new ReleaseException("-StabilityCores must fit whole available physical cores and leave CPUs for other checks", 64);
        return (stability, available & ~stability);
    }

    internal static (ulong Stability, ulong General) SplitForMachine(ulong available, int reserved) =>
        SplitPreferred(available, reserved, PhysicalCores().OrderByDescending(core => core.Efficiency).ThenBy(core => core.Mask).Select(core => core.Mask).ToArray());

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
        var result = ProcessRunner.Run(child.Executable, child.Arguments, child.Directory, child.Environment, standardInput: child.Input, forwardOutput: true);
        return result.ExitCode;
    }

    internal static SafeFileHandle Assign(Process process, ulong mask)
    {
        if (!OperatingSystem.IsWindows()) throw new ReleaseException("check-all CPU isolation requires Windows");
        var job = CreateJobObject(IntPtr.Zero, null);
        try
        {
            var limits = new BasicLimits { LimitFlags = 0x10, Affinity = new UIntPtr(mask) };
            if (job.IsInvalid || !SetInformationJobObject(job, 2, ref limits, Marshal.SizeOf<BasicLimits>()) || !AssignProcessToJobObject(job, process.Handle))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            return job;
        }
        catch
        {
            job.Dispose();
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            throw;
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
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObject(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int information, ref BasicLimits limits, int length);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLogicalProcessorInformationEx(int relationship, IntPtr buffer, ref uint length);
}
