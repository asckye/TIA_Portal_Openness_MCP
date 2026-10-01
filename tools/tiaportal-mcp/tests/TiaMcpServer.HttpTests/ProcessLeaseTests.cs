using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;

internal static partial class Program
{
    private static object AcquireTestLease(string root, long ticks) => Server.GetType("TiaMcpServer.Siemens.PortalProcessLease", true)!
        .GetMethod("Acquire", All)!.Invoke(null, new object[] { root, 54321, ticks })!;
    private static void CleanLease(object lease) => lease.GetType().GetMethod("ReleaseCleanly", All)!.Invoke(lease, null);
    private static int LeaseFixture(string[] args)
    {
        using var lease = (IDisposable)AcquireTestLease(args[2], long.Parse(args[3]));
        Console.WriteLine("RESERVED"); Console.Out.Flush();
        if (Console.ReadLine() == "release") CleanLease(lease);
        return 0;
    }
    private static async Task ProcessLeaseTests()
    {
        string root = Path.Combine(Path.GetTempPath(), "tia-lease-process-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Process Start(long ticks) => Process.Start(new ProcessStartInfo(Assembly.GetExecutingAssembly().Location,
            "\"" + Server.Location + "\" lease-holder \"" + root + "\" " + ticks) {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true })!;
        bool Refused(long ticks)
        {
            try { using var lease = (IDisposable)AcquireTestLease(root, ticks); CleanLease(lease); return false; }
            catch (TargetInvocationException ex) when (ex.InnerException is InvalidOperationException) { return true; }
        }
        try
        {
            await Test("separate MCP process cannot reserve an active TIA identity", async () => {
                using var owner = Start(100);
                try
                {
                    Check(await Bounded(owner.StandardOutput.ReadLineAsync()) == "RESERVED", "Child did not reserve lease");
                    Check(Refused(100), "Competing process acquired the same identity");
                    using (var other = (IDisposable)AcquireTestLease(root, 101)) CleanLease(other);
                    owner.StandardInput.WriteLine("release"); owner.StandardInput.Flush();
                    Check(owner.WaitForExit(5000) && owner.ExitCode == 0, "Clean holder failed");
                    using var next = (IDisposable)AcquireTestLease(root, 100); CleanLease(next);
                }
                finally { if (!owner.HasExited) owner.Kill(); }
            });
            await Test("killed owner remains blocked; changed TIA start time can bind", async () => {
                using var owner = Start(200);
                try
                {
                    Check(await Bounded(owner.StandardOutput.ReadLineAsync()) == "RESERVED", "Child did not reserve lease");
                    owner.Kill(); Check(owner.WaitForExit(5000), "Owner did not exit");
                    Check(Refused(200), "Crash uncertainty marker was ignored");
                    using var restarted = (IDisposable)AcquireTestLease(root, 201); CleanLease(restarted);
                }
                finally { if (!owner.HasExited) owner.Kill(); }
            });
            Console.WriteLine("COMPLETE: 2 process lease checks passed; no TIA connection attempted");
        }
        finally { Directory.Delete(root, true); }
    }
}
