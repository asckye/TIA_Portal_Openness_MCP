using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using TiaOpenness.Shared;

namespace TiaOpenness.Gui.Services;

internal sealed record VisibleHost(int Pid, long StartTicks, string ReleaseKey, string ImagePath, string BundleRoot,
    string Transport, string? HttpPrefix, string? Version, bool WorkbenchStarted, bool OlderVersion)
{
    public string Display => $"PID {Pid} · V{ReleaseKey} · {Version ?? "?"}{(OlderVersion ? " ⚠" : "")}\n"
        + $"{new DateTime(StartTicks, DateTimeKind.Utc):u} · {Transport} {HttpPrefix ?? (Transport == "http" ? "?" : "")}\n{ImagePath}";
}

// Used only by human event handlers in the configuration page. No pipe or MCP entry point.
internal sealed class HostLifecycleService(string dataRoot, string currentVersion)
{
    internal IReadOnlyList<VisibleHost> Discover()
    {
        string directory = Path.Combine(dataRoot, "run", "hosts");
        if (!Directory.Exists(directory)) return Array.Empty<VisibleHost>();
        var hosts = new List<VisibleHost>();
        foreach (string file in Directory.EnumerateFiles(directory, "*.json"))
        {
            try
            {
                if (new FileInfo(file).Length > OperatorProtocol.MaximumBytes) continue;
                using var stream = McpSessionClient.OpenRegistration(file);
                var record = JsonNode.Parse(stream)!;
                if ((int?)record["version"] != 1 || (string?)record["userSid"] != LocalPipeSecurity.CurrentSid) continue;
                var host = new VisibleHost((int)record["pid"]!, (long)record["startTicks"]!, (string)record["releaseKey"]!,
                    (string)record["imagePath"]!, (string)record["bundleRoot"]!, (string)record["transport"]!,
                    (string?)record["httpPrefix"], (string?)record["hostVersion"], (bool?)record["workbenchStarted"] == true,
                    IsOlder((string?)record["hostVersion"], currentVersion));
                using var process = VerifiedProcess(host);
                if (host.Transport == "http" && host.HttpPrefix == null)
                    host = host with { HttpPrefix = ListeningEndpoint(host.Pid) };
                hosts.Add(host);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException
                or InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception)
            { Trace.TraceInformation("Host registration unavailable: {0}", error.GetType().Name); }
        }
        return hosts.OrderBy(host => host.Pid).ToArray();
    }
    internal static bool IsOlder(string? actual, string current)
    {
        if (string.IsNullOrEmpty(actual)) return true;
        return TiaMcpConfigurator.UpdateCheck.Compare(current, actual) > 0;
    }
    // Older registrations did not record the HTTP prefix. Read the kernel's listener/PID table;
    // do not probe an endpoint or infer ownership from a saved port number.
    private static string? ListeningEndpoint(int pid)
    {
        foreach (uint family in new uint[] { 2, 23 })
        {
            int size = 0;
            _ = GetExtendedTcpTable(IntPtr.Zero, ref size, false, family, 3, 0);
            for (int attempt = 0; attempt < 3 && size >= 4; attempt++)
            {
                int capacity = size;
                IntPtr buffer = Marshal.AllocHGlobal(capacity);
                try
                {
                    uint result = GetExtendedTcpTable(buffer, ref size, false, family, 3, 0);
                    if (result == 122) continue;
                    if (result != 0) break;
                    int stride = family == 2 ? 24 : 56, count = Marshal.ReadInt32(buffer);
                    if (count < 0 || count > (capacity - 4) / stride) break;
                    for (int index = 0; index < count; index++)
                    {
                        IntPtr row = IntPtr.Add(buffer, 4 + index * stride);
                        if (Marshal.ReadInt32(row, family == 2 ? 20 : 52) != pid) continue;
                        int portOffset = family == 2 ? 8 : 20;
                        int port = Marshal.ReadByte(row, portOffset) * 256 + Marshal.ReadByte(row, portOffset + 1);
                        var address = new byte[family == 2 ? 4 : 16];
                        Marshal.Copy(IntPtr.Add(row, family == 2 ? 4 : 0), address, 0, address.Length);
                        string host = family == 2 ? new IPAddress(address).ToString()
                            : "[" + new IPAddress(address, unchecked((uint)Marshal.ReadInt32(row, 16))) + "]";
                        return "http://" + host + ":" + port + "/";
                    }
                    break;
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
        }
        return null;
    }
    [DllImport("iphlpapi.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, [MarshalAs(UnmanagedType.Bool)] bool order,
        uint family, uint tableClass, uint reserved);
    internal static Process VerifiedProcess(VisibleHost host)
    {
        var process = Process.GetProcessById(host.Pid);
        try
        {
            string? image = process.MainModule?.FileName;
            if (process.HasExited || process.StartTime.ToUniversalTime().Ticks != host.StartTicks
                || image == null || !string.Equals(Path.GetFullPath(image), Path.GetFullPath(host.ImagePath), StringComparison.OrdinalIgnoreCase))
                throw new UnauthorizedAccessException("Host process identity changed; refresh the host list.");
            return process;
        }
        catch { process.Dispose(); throw; }
    }
    internal static async Task Stop(VisibleHost host)
    {
        using var process = VerifiedProcess(host);
        process.Kill();
        await process.WaitForExitAsync().ConfigureAwait(false);
    }
    internal static async Task<Process> Restart(VisibleHost host, ProcessStartInfo start)
    {
        if (host.Transport != "http" || start.UseShellExecute || !Path.IsPathFullyQualified(start.FileName) || !File.Exists(start.FileName))
            throw new InvalidOperationException("A verified HTTP restart configuration is required.");
        await Stop(host).ConfigureAwait(false);
        return Process.Start(start) ?? throw new IOException("Host restart did not start a process.");
    }
}
