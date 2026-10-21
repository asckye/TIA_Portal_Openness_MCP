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

// Session disposal and host stopping use the same bounded non-owning teardown.
internal static class WorkerShutdown
{
    internal const int GracefulTimeoutMilliseconds = 10000;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<IDisposable, byte> Owners = new();
    private static readonly ConsoleHandler ConsoleClosing = signal => {
        if (signal is 2 or 5 or 6) StopAll();
        return false;
    };
    static WorkerShutdown()
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) => StopAll();
        if (OperatingSystem.IsWindows()) _ = SetConsoleCtrlHandler(ConsoleClosing, true);
    }
    internal static void Register(IDisposable owner) => Owners.TryAdd(owner, 0);
    internal static void Unregister(IDisposable owner) => Owners.TryRemove(owner, out _);
    internal static void StopAll()
    {
        // Parallel waits keep the host deadline bounded even with many HTTP sessions.
        Task.WhenAll(Owners.Keys.Select(owner => Task.Run(owner.Dispose))).GetAwaiter().GetResult();
    }
    internal static WorkerTerminationException? Stop(Process? process, TiaMcp.WorkerChannel.ChannelClient? channel,
        Func<Process, string?>? image = null)
    {
        bool busy = channel?.InFlight == true || channel?.OutcomeUnknown == true && channel.CanDisconnect != true;
        try
        {
            try { if (channel != null) channel.Dispose(); else process?.StandardInput.Close(); }
            catch (IOException) /* swallow(teardown): broken stdin cannot prevent the bounded worker exit/termination */ { }
            if (process != null && !process.HasExited && (busy || !process.WaitForExit(GracefulTimeoutMilliseconds)))
                KillOwned(process, image ?? ReadImage);
        }
        catch (WorkerTerminationException refusal) { Trace.TraceError(refusal.ToString()); return refusal; }
        catch (System.ComponentModel.Win32Exception error)
        { var refusal = new WorkerTerminationException("Unable to verify or stop the owned worker.", error); Trace.TraceError(refusal.ToString()); return refusal; }
        catch (InvalidOperationException error)
        {
            if (process != null && !Exited(process))
            { var refusal = new WorkerTerminationException("Unable to confirm the owned worker has exited.", error); Trace.TraceError(refusal.ToString()); return refusal; }
        }
        process?.Dispose();
        return null;
    }
    internal static void KillOwned(Process process)
        => KillOwned(process, ReadImage);
    private static string? ReadImage(Process child) => child.MainModule?.FileName;
    internal static void KillOwned(Process process, Func<Process, string?> image)
    {
        try
        {
            if (process.HasExited) return;
            var expected = Path.GetFullPath(process.StartInfo.FileName);
            string? actual;
            // Closing an idle worker's input lets it leave on its own, and it can do so between HasExited and the image read:
            // the modules of a process that has left cannot be read. The handle held since Process.Start still names the owned
            // child and reports its exit, so a failed or empty probe of that exited child means it is already stopped.
            // A child that is still running keeps the refusal below.
            try { actual = image(process); }
            catch (System.ComponentModel.Win32Exception) when (Exited(process)) /* swallow(teardown): the owned child has already exited, so it is stopped */ { return; }
            if (actual == null && Exited(process)) return;
            if (actual == null || !string.Equals(Path.GetFullPath(actual), expected, StringComparison.OrdinalIgnoreCase))
                throw new WorkerTerminationException($"Refusing to stop worker {process.Id}: executable '{actual}' differs from owned child '{expected}'.");
            process.Kill();
            if (!process.WaitForExit(GracefulTimeoutMilliseconds))
                throw new WorkerTerminationException($"Owned worker {process.Id} did not exit after termination.");
        }
        catch (System.ComponentModel.Win32Exception error)
        { throw new WorkerTerminationException("Unable to verify or stop the owned worker.", error); }
        catch (InvalidOperationException error)
        {
            if (!Exited(process)) throw new WorkerTerminationException("Unable to confirm the owned worker has exited.", error);
        }
    }
    private static bool Exited(Process process)
    {
        try { return process.HasExited; }
        catch (InvalidOperationException) /* swallow(teardown): an unavailable handle cannot establish successful termination */ { return false; }
        catch (System.ComponentModel.Win32Exception) /* swallow(teardown): inaccessible process state cannot establish successful termination */ { return false; }
    }
    private delegate bool ConsoleHandler(uint signal);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleCtrlHandler(ConsoleHandler handler, bool add);
}

internal sealed class WorkerTerminationException(string message, Exception? inner = null) : IOException(message, inner);
