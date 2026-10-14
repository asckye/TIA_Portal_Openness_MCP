using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using TiaOpenness.Shared;

namespace TiaMcp.PluginLauncher;

internal static class HostProcess
{
    private const uint Suspended = 4, UseStdHandles = 0x100, KillOnClose = 0x2000, SilentBreakaway = 0x1000;

    internal static int Run(string executable, string[] arguments)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("The plugin launcher requires Windows.");
        using var job = CreateJobObject(IntPtr.Zero, null);
        if (job.IsInvalid) throw Error("Create host lifetime job");
        // The host owns a separate worker job. TIA processes started by those workers
        // retain the host's existing breakaway policy and are not owned by this launcher.
        var limits = new ExtendedLimits { Basic = new BasicLimits { LimitFlags = KillOnClose | SilentBreakaway } };
        if (!SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimits>())) throw Error("Configure host lifetime job");
        using var input = InheritStandardHandle(-10);
        using var output = InheritStandardHandle(-11);
        using var error = InheritStandardHandle(-12);
        var startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>(), Flags = UseStdHandles,
            Input = input.DangerousGetHandle(), Output = output.DangerousGetHandle(), Error = error.DangerousGetHandle() };
        var command = new StringBuilder(string.Join(" ", new[] { executable }.Concat(arguments).Select(ProcessArguments.Quote)));
        if (!CreateProcess(executable, command, IntPtr.Zero, IntPtr.Zero, true, Suspended, IntPtr.Zero,
                null, ref startup, out var child)) throw Error("Start host " + executable);
        using var process = new SafeFileHandle(child.Process, true);
        using var thread = new SafeFileHandle(child.Thread, true);
        if (!AssignProcessToJobObject(job, process))
        {
            var failure = Error("Bind host lifetime job");
            StopSuspendedHost(process, executable);
            throw failure;
        }
        if (ResumeThread(thread) == uint.MaxValue) throw Error("Resume host");
        if (WaitForSingleObject(process, uint.MaxValue) != 0) throw Error("Wait for host exit");
        if (!GetExitCodeProcess(process, out uint exitCode)) throw Error("Read host exit code");
        return unchecked((int)exitCode);
    }

    private static SafeFileHandle InheritStandardHandle(int id)
    {
        IntPtr process = GetCurrentProcess();
        if (!DuplicateHandle(process, GetStdHandle(id), process, out var duplicate, 0, true, 2))
            throw Error("Inherit standard stream");
        return duplicate;
    }

    private static void StopSuspendedHost(SafeFileHandle process, string executable)
    {
        var path = new StringBuilder(32768); uint length = (uint)path.Capacity;
        if (!QueryFullProcessImageName(process, 0, path, ref length)) throw Error("Verify suspended host image");
        if (!string.Equals(Path.GetFullPath(path.ToString()), Path.GetFullPath(executable), StringComparison.OrdinalIgnoreCase))
            throw new IOException("Suspended host image differs from selected executable.");
        if (!TerminateProcess(process, 1)) throw Error("Stop suspended host after job binding failed");
    }

    private static Win32Exception Error(string operation) => new(Marshal.GetLastWin32Error(), operation);

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        public int Size;
        public IntPtr Reserved, Desktop, Title;
        public uint X, Y, Width, Height, XChars, YChars, Fill, Flags;
        public ushort ShowWindow, ReservedSize;
        public IntPtr ReservedBytes, Input, Output, Error;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInfo { public IntPtr Process, Thread; public uint ProcessId, ThreadId; }
    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimits
    {
        public long ProcessTime, JobTime;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSet, MaximumWorkingSet;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimits
    {
        public BasicLimits Basic;
        public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes;
        public UIntPtr ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcess(string application, StringBuilder command, IntPtr processAttributes,
        IntPtr threadAttributes, bool inheritHandles, uint flags, IntPtr environment, string? directory,
        ref StartupInfo startup, out ProcessInfo information);
    [DllImport("kernel32.dll", EntryPoint = "CreateJobObjectW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObject(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int informationClass, ref ExtendedLimits limits, uint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, SafeFileHandle process);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(SafeFileHandle thread);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeFileHandle handle, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(SafeFileHandle process, out uint exitCode);
    [DllImport("kernel32.dll")]
    private static extern IntPtr GetStdHandle(int id);
    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DuplicateHandle(IntPtr sourceProcess, IntPtr source, IntPtr targetProcess,
        out SafeFileHandle target, uint access, bool inherit, uint options);
    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageName(SafeFileHandle process, uint flags, StringBuilder path, ref uint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateProcess(SafeFileHandle process, uint exitCode);
}
