using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using TiaMcp.Adapters.Native.Vci;

internal static class VciWorkerRuntimeTests
{
    private static int Main(string[] args)
    {
        var workspace = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "vci-" + Guid.NewGuid().ToString("N")));
        try
        {
            string runtime = RuntimeInformation.FrameworkDescription;
            if (!runtime.StartsWith(".NET Framework 4.8", StringComparison.Ordinal))
                throw new InvalidOperationException("Expected .NET Framework 4.8, got " + runtime);
            if (args.Length == 1 && args[0] == "--status-replies")
            {
                Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new {
                    v16 = VciStatusFixture.Run(workspace, refreshesKnownState: true),
                    v17To19 = VciStatusFixture.Run(workspace, refreshesKnownState: false)
                }));
                return 0;
            }
            if (args.Length is 1 or 2 && args[0] == "--export-replies")
            {
                Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(VciExportFixture.Run(workspace, args.Length == 2 && args[1] == "16")));
                return 0;
            }
            if (args.Length == 1 && args[0] == "--workspace-files")
            {
                Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(VciExportFixture.WorkspaceFileStatus(workspace)));
                return 0;
            }
            Console.WriteLine(runtime);
            string file = Path.Combine(workspace.FullName, "Motor.s7dcl");
            File.Copy(Path.Combine(AppContext.BaseDirectory, "FC_Add_SD.s7dcl"), file);
            CheckRefusal(workspace, "unchanged", 0);
            CheckRefusal(workspace, "changed target", 1, () => File.AppendAllText(file, "\n"));
            string companion = Path.Combine(workspace.FullName, "Other.xml");
            File.WriteAllText(companion, "<Document/>");
            CheckRefusal(workspace, "changed unrelated file", 1, () => File.AppendAllText(companion, "\n"));
            CheckJunction(workspace);
            CheckEnumerationFailure(workspace);
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error.Message); return 1; }
        finally { workspace.Delete(true); }
    }

    private static void CheckRefusal(DirectoryInfo workspace, string scenario, int expectedUnconfirmed, Action? change = null)
    {
        var attempts = new VciExportAttempts();
        bool mapped = attempts.ExportToWorkspace(workspace.FullName, "Motor", () =>
        {
            change?.Invoke();
            throw new IOException("VCI refused");
        }, () => false, _ => { });
        if (mapped || attempts.Issued != 1 || attempts.Unconfirmed != expectedUnconfirmed || attempts.NativeMessage != "VCI refused")
            throw new InvalidOperationException($"{scenario}: expected one refused export and {expectedUnconfirmed} unconfirmed, got {attempts.Unconfirmed}.");
        Console.WriteLine("PASS " + scenario);
    }

    private static void CheckJunction(DirectoryInfo workspace)
    {
        workspace.CreateSubdirectory("Target");
        string link = Path.Combine(workspace.FullName, "Junction");
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), "/d /c mklink /J Junction Target") {
            WorkingDirectory = workspace.FullName, UseShellExecute = false, CreateNoWindow = true
        };
        using var process = Process.Start(start)!;
        process.WaitForExit();
        if (process.ExitCode != 0) throw new IOException("Could not create the workspace junction.");
        try
        {
            if ((File.GetAttributes(link) & FileAttributes.ReparsePoint) == 0) throw new IOException("Expected a junction.");
            CheckRefusal(workspace, "junction", 1);
            if (VciExportPolicy.FileExists(new FileInfo(Path.Combine(link, "Table.xml"))) != null)
                throw new IOException("A missing file below a junction must never be repaired.");
        }
        finally { Directory.Delete(link); }
    }

    private static void CheckEnumerationFailure(DirectoryInfo workspace)
    {
        var folder = workspace.CreateSubdirectory("Locked");
        const uint listDirectory = 1, backupSemantics = 0x02000000;
        using var handle = CreateFileW(folder.FullName, listDirectory, FileShare.None, IntPtr.Zero, FileMode.Open, backupSemantics, IntPtr.Zero);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        try { Directory.GetFileSystemEntries(folder.FullName); }
        catch (IOException error) when ((error.HResult & 0xffff) == 32) // ERROR_SHARING_VIOLATION
        {
            CheckRefusal(workspace, "unreadable subfolder", 1);
            return;
        }
        throw new IOException("Expected directory enumeration to fail while its exclusive handle is open.");
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, FileShare share, IntPtr security,
        FileMode creation, uint flags, IntPtr template);
}
