using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using Microsoft.Win32;

// Root Studio launcher, single desktop application. Configuration is embedded
// in that application's main window; this bootstrapper has no separate UI.
internal static class DesktopLauncher
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            // Built by the CLR 4 compiler without a net48 target/config: this check
            // can run on earlier .NET 4.x installations before starting the desktop.
            using (var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32))
            using (var full = machine.OpenSubKey(@"SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full"))
            {
                if (!HasRequiredFramework(full == null ? null : full.GetValue("Release")))
                {
                    MessageBox.Show(MissingFrameworkMessage, "TIA Portal", MessageBoxButton.OK, MessageBoxImage.Error);
                    return 1;
                }
            }
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

    internal static bool HasRequiredFramework(object release)
    {
        return release is int && (int)release >= 528040;
    }

    internal const string MissingFrameworkMessage =
        "缺少 Microsoft .NET Framework 4.8 或更高版本。\n" +
        "TIA MCP 引擎和 Openness 工作进程需要它才能运行；随包提供的 .NET 桌面运行时不能替代它。\n" +
        "请从微软官网下载并安装 .NET Framework 4.8 Runtime，完成后重新启动工作台：\n" +
        "https://dotnet.microsoft.com/download/dotnet-framework/net48\n\n" +
        "Microsoft .NET Framework 4.8 or later is required by the TIA MCP engines and Openness workers. " +
        "Install the Runtime from the Microsoft link above, then restart the workbench.";
}
