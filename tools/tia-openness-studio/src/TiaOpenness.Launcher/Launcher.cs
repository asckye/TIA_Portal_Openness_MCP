using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;

// Compatibility filename, single desktop application. Configuration is embedded
// in that application's main window; this bootstrapper has no separate UI.
internal static class DesktopLauncher
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            string root = AppDomain.CurrentDomain.BaseDirectory;
            string desktop = Path.Combine(root, "runtime", "studio", "TiaOpenness.exe");
            if (!File.Exists(desktop)) throw new FileNotFoundException("请完整解压交付包后启动。The complete desktop bundle is required.", desktop);
            var arguments = args.Length == 0 ? new[] { "--mcp" } : args;
            using (var process = Process.Start(new ProcessStartInfo(desktop, String.Join(" ", arguments.Select(TiaOpenness.Shared.ProcessArguments.Quote))) {
                WorkingDirectory = root, UseShellExecute = false }))
            {
                if (process == null) throw new InvalidOperationException("The desktop could not be started.");
            }
            return 0;
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "TIA Portal", MessageBoxButton.OK, MessageBoxImage.Error); return 1; }
    }
}
