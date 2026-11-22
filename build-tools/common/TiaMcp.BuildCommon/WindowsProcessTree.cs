using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace TiaMcp.BuildCommon;

// The suspended child joins our kill-on-close job before it can create any descendants.
// Only its three standard handles are inherited, even when other shards launch concurrently.
public sealed class WindowsProcessTree : IDisposable
{
    private static readonly object StartLock = new();
    private readonly DesktopHandle? _desktop;
    private SafeFileHandle? _job;
    private SafeFileHandle? _completion;
    private SafeFileHandle? _process;
    private AnonymousPipeServerStream? _output;
    private AnonymousPipeServerStream? _error;
    private AnonymousPipeServerStream? _input;
    private bool _assigned;
    private readonly object _finishLock = new();
    private bool _finishAttempted;
    private ExceptionDispatchInfo? _finishFailure;

    public string? DesktopPath { get; }
    public uint ProcessId { get; private set; }
    public StreamReader StandardOutput { get; private set; } = null!;
    public StreamReader StandardError { get; private set; } = null!;
    public StreamWriter StandardInput { get; private set; } = null!;

    private WindowsProcessTree(string? name)
    {
        if (name == null) return;
        var station = new StringBuilder(256);
        if (!GetUserObjectInformation(GetProcessWindowStation(), 2, station, station.Capacity * sizeof(char), out _))
            throw Failure("read the GUI test window station");
        DesktopPath = station + "\\" + name;
        _desktop = CreateDesktop(name, null, IntPtr.Zero, 0, 0x000F01FF, IntPtr.Zero);
        if (_desktop.IsInvalid)
        {
            int code = Marshal.GetLastWin32Error();
            _desktop.Dispose();
            throw Failure("create isolated GUI test desktop '" + DesktopPath + "'", code);
        }
    }

    // Keep ordinary redirected launches from inheriting another concurrent launch's temporary client handles.
    public static void StartRegular(Process process)
    {
        lock (StartLock) process.Start();
    }

    public static WindowsProcessTree Start(ProcessStartInfo start, string? name = null)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows process jobs require Windows.");
        var owned = new WindowsProcessTree(name);
        // Process.Start can also inherit handles. Keep failure cleanup under this
        // lock as well, until every locally inheritable pipe client is closed.
        lock (StartLock)
        {
            try
            {
                owned.Launch(start);
                return owned;
            }
            catch (Exception error)
            {
                owned.Dispose(error);
                throw;
            }
        }
    }

    private void Launch(ProcessStartInfo start)
    {
        _job = CreateJobObject(IntPtr.Zero, null);
        if (_job.IsInvalid) throw Failure("create the owned process job");
        var limits = new JobLimits { Basic = new BasicLimits { LimitFlags = 0x2000 /* KILL_ON_JOB_CLOSE */ } };
        if (!SetInformationJobObject(_job, 9, ref limits, (uint)Marshal.SizeOf<JobLimits>()))
            throw Failure("configure the owned process job");
        _completion = CreateIoCompletionPort(new IntPtr(-1), IntPtr.Zero, UIntPtr.Zero, 1);
        if (_completion.IsInvalid) throw Failure("create the process completion port");
        var association = new JobCompletion { CompletionKey = new IntPtr(1), CompletionPort = _completion.DangerousGetHandle() };
        if (!SetInformationJobObject(_job, 7, ref association, (uint)Marshal.SizeOf<JobCompletion>()))
            throw Failure("observe the owned process job");

        _output = new(PipeDirection.In, HandleInheritability.Inheritable);
        _error = new(PipeDirection.In, HandleInheritability.Inheritable);
        _input = new(PipeDirection.Out, HandleInheritability.Inheritable);
        StandardOutput = new(_output, start.StandardOutputEncoding ?? Console.OutputEncoding);
        StandardError = new(_error, start.StandardErrorEncoding ?? Console.OutputEncoding);
        StandardInput = new(_input, start.StandardInputEncoding ?? Console.InputEncoding);
        var handles = new[] { _input.ClientSafePipeHandle.DangerousGetHandle(),
            _output.ClientSafePipeHandle.DangerousGetHandle(), _error.ClientSafePipeHandle.DangerousGetHandle() };
        nuint bytes = 0;
        InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref bytes);
        var attributes = Marshal.AllocHGlobal(checked((int)bytes));
        var inherited = Marshal.AllocHGlobal(handles.Length * IntPtr.Size);
        var environment = Marshal.StringToHGlobalUni(string.Join('\0', start.Environment.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Where(pair => pair.Value != null).Select(pair => pair.Key + "=" + pair.Value)) + "\0\0");
        bool initialized = false;
        try
        {
            if (!InitializeProcThreadAttributeList(attributes, 1, 0, ref bytes)) throw Failure("initialize process startup attributes");
            initialized = true;
            Marshal.Copy(handles, 0, inherited, handles.Length);
            if (!UpdateProcThreadAttribute(attributes, 0, new IntPtr(0x00020002 /* HANDLE_LIST */), inherited,
                (nuint)(handles.Length * IntPtr.Size), IntPtr.Zero, IntPtr.Zero)) throw Failure("restrict inherited handles");
            var startup = new StartupInfoEx
            {
                Startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfoEx>(), Desktop = DesktopPath,
                    Flags = 0x100 /* USESTDHANDLES */, Input = handles[0], Output = handles[1], Error = handles[2] },
                Attributes = attributes
            };
            var command = new StringBuilder(string.Join(' ', new[] { start.FileName }.Concat(start.ArgumentList).Select(Quote)));
            if (!CreateProcess(null, command, IntPtr.Zero, IntPtr.Zero, true,
                0x08080404 /* NO_WINDOW | EXTENDED_STARTUPINFO | UNICODE_ENVIRONMENT | SUSPENDED */,
                environment, start.WorkingDirectory, ref startup, out var child))
                throw Failure(DesktopPath == null ? "start " + start.FileName : "start GUI tests on isolated desktop '" + DesktopPath + "'");
            _process = new(child.Process, true);
            ProcessId = child.ProcessId;
            using var thread = new SafeFileHandle(child.Thread, true);
            if (!AssignProcessToJobObject(_job, _process)) throw Failure("own the owned process");
            _assigned = true;
            _output.DisposeLocalCopyOfClientHandle();
            _error.DisposeLocalCopyOfClientHandle();
            _input.DisposeLocalCopyOfClientHandle();
            if (ResumeThread(thread) == uint.MaxValue) throw Failure("resume the owned process");
        }
        finally
        {
            if (initialized) DeleteProcThreadAttributeList(attributes);
            Marshal.FreeHGlobal(attributes);
            Marshal.FreeHGlobal(inherited);
            Marshal.FreeHGlobal(environment);
        }
    }

    public bool WaitForExit(int milliseconds)
    {
        uint result = WaitForSingleObject(_process!, unchecked((uint)milliseconds));
        if (result == uint.MaxValue) throw Failure("wait for the owned process");
        return result == 0;
    }

    public int GetExitCode() => GetExitCodeProcess(_process!, out uint code) ? unchecked((int)code)
        : throw Failure("read the isolated GUI test exit code");

    // Completion messages are event-driven. Wait for the entire owned tree, including an
    // orphaned testhost, before closing its desktop or waiting for redirected pipe EOF.
    public void Finish() => Finish(ProcessDeadline.NormalExitCleanupTimeout);

    public void Finish(TimeSpan timeout) => Finish(timeout, disposing: false);

    private void Finish(TimeSpan timeout, bool disposing)
    {
        var clock = TimeProvider.System;
        long started = clock.GetTimestamp();
        if (!Monitor.TryEnter(_finishLock, timeout))
            throw CleanupTimedOut(ProcessId, "another cleanup attempt", timeout);
        try
        {
            if (_finishAttempted)
            {
                // Dispose still closes the handles after a failed Finish without repeating or masking that failure.
                if (!disposing) _finishFailure?.Throw();
                return;
            }
            _finishAttempted = true;
            try { FinishTree(clock, started, timeout); }
            catch (Exception ex)
            {
                _finishFailure = ExceptionDispatchInfo.Capture(ex);
                throw;
            }
        }
        finally { Monitor.Exit(_finishLock); }
    }

    private void FinishTree(TimeProvider clock, long started, TimeSpan timeout)
    {
        if (_assigned)
        {
            if (!TerminateJobObject(_job!, 1)) throw Failure("terminate the owned process job");
            WaitForEmptyJob(ProcessId, clock, started, ReadJobMessage, timeout);
        }
        else if (_process != null)
        {
            if (!TerminateProcess(_process, 1)) throw Failure("terminate the suspended process");
            if (!WaitForExit(RemainingMilliseconds(clock, started, timeout))) throw CleanupTimedOut(ProcessId, "suspended process exit", timeout);
        }
    }

    private uint? ReadJobMessage(int milliseconds)
    {
        if (GetQueuedCompletionStatus(_completion!, out uint message, out _, out _, (uint)milliseconds)) return message;
        int code = Marshal.GetLastWin32Error();
        if (code == 258 /* WAIT_TIMEOUT */) return null;
        throw Failure("wait for the owned process tree", code);
    }

    internal static void WaitForEmptyJob(uint pid, TimeProvider clock, long started, Func<int, uint?> read, TimeSpan? cleanupTimeout = null)
    {
        var timeout = cleanupTimeout ?? ProcessDeadline.CleanupTimeout;
        int remaining;
        while ((remaining = RemainingMilliseconds(clock, started, timeout)) > 0)
        {
            uint? message = read(remaining);
            if (message == 4 /* ACTIVE_PROCESS_ZERO */) return;
            if (message == null) break;
        }
        throw CleanupTimedOut(pid, "ACTIVE_PROCESS_ZERO", timeout);
    }

    private static int RemainingMilliseconds(TimeProvider clock, long started, TimeSpan timeout) =>
        (int)Math.Max(0, Math.Ceiling((timeout - clock.GetElapsedTime(started)).TotalMilliseconds));

    private static TimeoutException CleanupTimedOut(uint pid, string waitingFor, TimeSpan timeout) =>
        new($"Cleanup of owned PID {pid} timed out waiting for {waitingFor} within {timeout:c}.");

    // Callers unwinding a failed launch or command pass its exception through their finally block.
    public void Dispose(Exception? primaryFailure) => ClosePreservingFailure(primaryFailure, Dispose);

    internal static void ClosePreservingFailure(Exception? primaryFailure, Action close)
    {
        try { close(); }
        catch (Exception cleanup) when (primaryFailure != null) { primaryFailure.Data["ProcessTreeCleanup"] = cleanup; }
    }

    public void Dispose()
    {
        using (_desktop)
        using (_completion)
        using (_job)
        using (_process)
        using (_error)
        using (_output)
        using (_input)
        using (StandardError)
        using (StandardOutput)
        using (StandardInput)
        {
            Finish(ProcessDeadline.CleanupTimeout, disposing: true);
        }
    }

    private static IOException Failure(string action, int? error = null)
    {
        int code = error ?? Marshal.GetLastWin32Error();
        return new($"Cannot {action}: Win32 {code}: {new Win32Exception(code).Message}.");
    }

    private static string Quote(string value)
    {
        var result = new StringBuilder("\"");
        int slashes = 0;
        foreach (char ch in value)
        {
            if (ch == '\\') { slashes++; continue; }
            result.Append('\\', ch == '"' ? slashes * 2 + 1 : slashes).Append(ch);
            slashes = 0;
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }

    private sealed class DesktopHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public DesktopHandle() : base(true) { }
        protected override bool ReleaseHandle() => CloseDesktop(handle);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        internal int Size;
        internal string? Reserved, Desktop, Title;
        internal int X, Y, Width, Height, XChars, YChars, Fill, Flags;
        internal short Show, ReservedSize;
        internal IntPtr ReservedBytes, Input, Output, Error;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx { internal StartupInfo Startup; internal IntPtr Attributes; }
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInfo { internal IntPtr Process, Thread; internal uint ProcessId, ThreadId; }
    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimits
    {
        internal long ProcessTime, JobTime;
        internal uint LimitFlags;
        internal nuint MinimumWorkingSet, MaximumWorkingSet;
        internal uint ActiveProcessLimit;
        internal nuint Affinity;
        internal uint PriorityClass, SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters { internal ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; }
    [StructLayout(LayoutKind.Sequential)]
    private struct JobLimits { internal BasicLimits Basic; internal IoCounters Io; internal nuint ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory; }
    [StructLayout(LayoutKind.Sequential)]
    private struct JobCompletion { internal IntPtr CompletionKey, CompletionPort; }

    [DllImport("user32.dll", EntryPoint = "CreateDesktopW", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern DesktopHandle CreateDesktop(string name, string? device, IntPtr mode, uint flags, uint access, IntPtr security);
    [DllImport("user32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr GetProcessWindowStation();
    [DllImport("user32.dll", EntryPoint = "GetUserObjectInformationW", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool GetUserObjectInformation(IntPtr handle, int index, StringBuilder value, int length, out int needed);
    [DllImport("user32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool CloseDesktop(IntPtr desktop);
    [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool CreateProcess(string? application, StringBuilder command, IntPtr processSecurity, IntPtr threadSecurity,
        bool inherit, uint flags, IntPtr environment, string directory, ref StartupInfoEx startup, out ProcessInfo process);
    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref nuint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value, nuint size, IntPtr previous, IntPtr returned);
    [DllImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern void DeleteProcThreadAttributeList(IntPtr list);
    [DllImport("kernel32.dll", EntryPoint = "CreateJobObjectW", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern SafeFileHandle CreateJobObject(IntPtr security, string? name);
    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int kind, ref JobLimits limits, uint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int kind, ref JobCompletion completion, uint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, SafeFileHandle process);
    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool TerminateJobObject(SafeFileHandle job, uint code);
    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool TerminateProcess(SafeFileHandle process, uint code);
    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint ResumeThread(SafeFileHandle thread);
    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint WaitForSingleObject(SafeFileHandle process, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool GetExitCodeProcess(SafeFileHandle process, out uint code);
    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern SafeFileHandle CreateIoCompletionPort(IntPtr file, IntPtr existing, UIntPtr key, uint threads);
    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool GetQueuedCompletionStatus(SafeFileHandle port, out uint bytes, out UIntPtr key, out IntPtr overlapped, uint milliseconds);
}
