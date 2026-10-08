using System.Diagnostics;
using System.Runtime.InteropServices;

namespace TiaMcp.FoundationHost;

// Binds worker processes to the host's lifetime: the job handle is held until the host exits, and Windows kills
// every assigned worker when the last handle closes (including when the host is terminated). Processes the
// workers start themselves, such as a TIA Portal instance, break away silently and are left running.
internal static class WorkerJob
{
    private const int ExtendedLimitInformation = 9;
    private const uint KillOnJobClose = 0x2000, SilentBreakawayOk = 0x1000;
    private static readonly Lazy<IntPtr> Job = new(Create);

    internal static void Bind(Process process)
    {
        if (!OperatingSystem.IsWindows()) return;
        IntPtr job = Job.Value;
        if (job == IntPtr.Zero || AssignProcessToJobObject(job, process.Handle)) return;
        Trace.TraceWarning($"Worker {process.Id} is not bound to the host lifetime (Win32 error {Marshal.GetLastWin32Error()}).");
    }

    private static IntPtr Create()
    {
        IntPtr job = CreateJobObject(IntPtr.Zero, null);
        if (job == IntPtr.Zero) return IntPtr.Zero;
        var limits = new ExtendedLimits { Basic = new BasicLimits { LimitFlags = KillOnJobClose | SilentBreakawayOk } };
        if (SetInformationJobObject(job, ExtendedLimitInformation, ref limits, (uint)Marshal.SizeOf<ExtendedLimits>())) return job;
        CloseHandle(job);
        return IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimits
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimits
    {
        public BasicLimits Basic;
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr job, int informationClass, ref ExtendedLimits information, uint length);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
