using System.Diagnostics;
using TiaMcp.BuildCommon;

namespace TiaMcp.ReleaseTool;

internal static class IsolatedDesktopProcess
{
    internal static WindowsProcessTree Start(ProcessStartInfo start, string name)
    {
        if (!OperatingSystem.IsWindows()) throw new ReleaseException("Isolated GUI test desktops require Windows; refusing to use the interactive desktop.");
        try { return WindowsProcessTree.Start(start, name); }
        catch (IOException ex) { throw new ReleaseException(ex.Message + " No interactive-desktop fallback."); }
    }
}
