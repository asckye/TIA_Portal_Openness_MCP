using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace TiaMcp.DiagnosticClients;

public sealed record WindowsProcess(int Pid, int ParentPid, string Name, string? CommandLine);

public static class WindowsProcesses
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Entry
    {
        public uint Size, Usage, Pid;
        public UIntPtr Heap;
        public uint Module, Threads, ParentPid;
        public int Priority;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Name;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString { public ushort Length, MaximumLength; public IntPtr Buffer; }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeFileHandle CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool Process32FirstW(SafeFileHandle snapshot, ref Entry entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool Process32NextW(SafeFileHandle snapshot, ref Entry entry);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int pid);
    [DllImport("ntdll.dll")] private static extern int NtQueryInformationProcess(SafeProcessHandle process, int informationClass, IntPtr information, int length, out int required);

    public static string? CommandLine(int pid)
    {
        if (!OperatingSystem.IsWindows()) return null;
        using var handle = OpenProcess(0x1000, false, pid); // PROCESS_QUERY_LIMITED_INFORMATION
        if (handle.IsInvalid) return null;
        // Class 60 returns the command line into our buffer; no remote PEB layout or
        // System.Management package is needed. Access/query failure stays fail-closed.
        NtQueryInformationProcess(handle, 60, IntPtr.Zero, 0, out var size);
        if (size < Marshal.SizeOf<UnicodeString>() || size > 1024 * 1024) return null;
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (NtQueryInformationProcess(handle, 60, buffer, size, out _) < 0) return null;
            var value = Marshal.PtrToStructure<UnicodeString>(buffer);
            var offset = value.Buffer.ToInt64() - buffer.ToInt64();
            if (offset < Marshal.SizeOf<UnicodeString>() || offset + value.Length > size || value.Length % 2 != 0) return null;
            return Marshal.PtrToStringUni(value.Buffer, value.Length / 2);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
    public static List<WindowsProcess> Snapshot()
    {
        var rows = new List<WindowsProcess>();
        if (!OperatingSystem.IsWindows()) return rows;
        using var snapshot = CreateToolhelp32Snapshot(2, 0);
        if (snapshot.IsInvalid) return rows;
        var entry = new Entry { Size = (uint)Marshal.SizeOf<Entry>(), Name = "" };
        if (!Process32FirstW(snapshot, ref entry)) return rows;
        do { rows.Add(new WindowsProcess((int)entry.Pid, (int)entry.ParentPid, entry.Name, entry.Name.Equals("Siemens.Automation.Portal.exe", StringComparison.OrdinalIgnoreCase) ? CommandLine((int)entry.Pid) : null)); }
        while (Process32NextW(snapshot, ref entry));
        return rows;
    }
    public static bool UserSession(WindowsProcess process) => process.Name.Equals("Siemens.Automation.Portal.exe", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(process.CommandLine) && !process.CommandLine.Contains("-bootstrapper=", StringComparison.OrdinalIgnoreCase);
    public static bool HeadlessPortal(string? command) => command is not null && command.Contains("siemens.automation.portal.exe", StringComparison.OrdinalIgnoreCase) && command.Contains("openness.loader.bootstrapper", StringComparison.OrdinalIgnoreCase);
    public static bool OwnedHost(string? command, FoundationLaunch launch) => command is not null && command.Contains(launch.Executable, StringComparison.OrdinalIgnoreCase) && command.Contains(launch.BundleRoot, StringComparison.OrdinalIgnoreCase) && command.Contains("--release-key " + launch.ReleaseKey, StringComparison.Ordinal) && command.Contains("--profile full", StringComparison.Ordinal);
    public static bool MatchesStart(int pid, long? ticks)
    {
        if (ticks is null) return false;
        try { using var process = Process.GetProcessById(pid); return process.StartTime.ToUniversalTime().Ticks == ticks; }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
        catch (System.ComponentModel.Win32Exception) { return false; }
    }
    public static void Kill(int pid, bool tree = false)
    {
        try { using var process = Process.GetProcessById(pid); if (!process.HasExited) process.Kill(tree); }
        catch (ArgumentException) { }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }
}
