using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace TiaMcp.FoundationHost;

internal sealed record PortalWindows(bool MainWindowDisabled, string[] Titles);

internal static class PortalWindowProbe
{
    internal static PortalWindows Read(int processId)
    {
        if (!OperatingSystem.IsWindows()) return new(false, []);
        try
        {
            using var process = Process.GetProcessById(processId);
            var main = process.MainWindowHandle;
            var titles = new List<string>();
            EnumWindows((window, _) => {
                GetWindowThreadProcessId(window, out uint owner);
                if (owner == (uint)processId && IsWindowVisible(window))
                {
                    var title = new StringBuilder(512);
                    if (GetWindowText(window, title, title.Capacity) > 0) titles.Add(title.ToString());
                }
                return true;
            }, IntPtr.Zero);
            return new(main != IntPtr.Zero && !IsWindowEnabled(main), titles.ToArray());
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or Win32Exception)
        { /* swallow(env-probe): an unavailable process has no observable windows. */ return new(false, []); }
    }

    private delegate bool EnumWindow(IntPtr window, IntPtr parameter);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindow callback, IntPtr parameter);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll")] private static extern bool IsWindowEnabled(IntPtr window);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr window, StringBuilder text, int maximum);
}
