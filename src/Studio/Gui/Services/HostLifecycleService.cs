using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using TiaOpenness.Shared;

namespace TiaOpenness.Gui.Services;

internal sealed record HostLifecycleTiming(TimeSpan Grace, TimeSpan PollInterval, TimeSpan ConfirmationWait)
{
    internal static readonly HostLifecycleTiming Default = new(McpSessionClient.OwnedHostExitTimeout,
        McpSessionClient.RegistrationPollInterval, Timeout.InfiniteTimeSpan);
}

internal sealed record VisibleHost(int Pid, long StartTicks, string ReleaseKey, string ImagePath, string BundleRoot,
    string Transport, string? HttpPrefix, string? Version, bool WorkbenchStarted, bool OlderVersion,
    string? PipeName = null, bool CallInProgress = false, string? RegistrationPath = null)
{
    public string Display => $"PID {Pid} · V{ReleaseKey} · {Version ?? "?"}{(OlderVersion ? " ⚠" : "")}\n"
        + $"{new DateTime(StartTicks, DateTimeKind.Utc):u} · {Transport} {HttpPrefix ?? (Transport == "http" ? "?" : "")}\n{ImagePath}";
}

// Used only by human event handlers in the configuration page. No pipe or MCP entry point.
internal sealed class HostLifecycleService(string dataRoot, string currentVersion)
{
    internal static void PrepareWorkbenchHost(ProcessStartInfo start)
    {
        // HostRegistration already resolves WorkbenchDataDirectory. Forcing that
        // fallback as a data root would also move the shared process leases.
        start.Environment["TIA_MCP_WORKBENCH_STARTED"] = "1";
    }

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
                if (JsonNode.Parse(stream) is not JsonObject record) throw new InvalidDataException("Expected a host registration object.");
                if ((int?)record["version"] != 1 || (string?)record["userSid"] != LocalPipeSecurity.CurrentSid) continue;
                if (record["pid"] is not JsonValue pidValue || !pidValue.TryGetValue<int>(out int pid) || pid <= 0
                    || record["startTicks"] is not JsonValue ticksValue || !ticksValue.TryGetValue<long>(out long ticks)
                    || ticks <= 0 || ticks > DateTime.MaxValue.Ticks)
                    throw new InvalidDataException("Missing or invalid host process identity.");
                string Required(string key) => record[key] is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text)
                    ? text : throw new InvalidDataException("Missing or invalid host registration field: " + key);
                string image = Required("imagePath"), bundle = Required("bundleRoot"), transport = Required("transport");
                if (!Path.IsPathFullyQualified(image) || !Path.IsPathFullyQualified(bundle) || transport is not ("http" or "stdio" or "operator"))
                    throw new InvalidDataException("Invalid host registration paths or transport.");
                string? pipe = (string?)record["pipeName"];
                if (pipe != null && pipe != OperatorPipe.Name(LocalPipeSecurity.CurrentSid, dataRoot, pid, ticks))
                    throw new InvalidDataException("Host control pipe identity mismatch.");
                if ((bool?)record["operatorAvailable"] == false) pipe = null;
                bool busy = CallsInProgress(record);
                var host = new VisibleHost(pid, ticks, Required("releaseKey"), image, bundle, transport,
                    (string?)record["httpPrefix"], (string?)record["hostVersion"], (bool?)record["workbenchStarted"] == true,
                    IsOlder((string?)record["hostVersion"], currentVersion), pipe, busy, file);
                using var process = VerifiedProcess(host);
                if (host.Transport == "http" && host.HttpPrefix == null)
                    host = host with { HttpPrefix = ListeningEndpoint(host.Pid) };
                hosts.Add(host);
            }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException
                or InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception)
            { Trace.TraceInformation("Host registration unavailable ({0}): {1}", Path.GetFileName(file), error.GetType().Name); }
        }
        return hosts.OrderBy(host => host.Pid).ToArray();
    }
    internal static bool IsOlder(string? actual, string current)
    {
        if (string.IsNullOrEmpty(actual)) return true;
        return TiaMcpConfigurator.UpdateCheck.Compare(current, actual) > 0;
    }
    private static bool CallsInProgress(JsonObject record) => (record["sessions"] as JsonArray)?.OfType<JsonObject>().Any(session =>
        (bool?)session["callInProgress"] == true || session["lane"] != null || (string?)session["worker"]?["state"] is "busy" or "stopping") == true;
    private static bool ReportedBusy(VisibleHost host)
    {
        if (host.CallInProgress) return true;
        if (host.RegistrationPath == null || !File.Exists(host.RegistrationPath)) return false;
        if (new FileInfo(host.RegistrationPath).Length > OperatorProtocol.MaximumBytes) throw new InvalidDataException("Invalid host registration size.");
        using var stream = McpSessionClient.OpenRegistration(host.RegistrationPath);
        if (JsonNode.Parse(stream) is not JsonObject record || (int?)record["pid"] != host.Pid
            || (long?)record["startTicks"] != host.StartTicks || (string?)record["userSid"] != LocalPipeSecurity.CurrentSid)
            throw new UnauthorizedAccessException("Host registration identity changed; refresh the host list.");
        return CallsInProgress(record);
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
    internal static async Task RequestShutdown(VisibleHost host)
    {
        if (host.PipeName == null) throw new IOException("This host has no available graceful control channel.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var pipe = new NamedPipeClientStream(".", host.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
        await pipe.ConnectAsync(1000, deadline.Token).ConfigureAwait(false);
        if (!LocalPipeSecurity.ServerProcessMatches(pipe, LocalPipeSecurity.CurrentSid, host.ImagePath, host.Pid))
            throw new UnauthorizedAccessException("Host control server identity mismatch.");
        using var verified = VerifiedProcess(host);
        await OperatorProtocol.Write(pipe, new OperatorHandshake { Action = "shutdown",
            Workbench = new OperatorWorkbench { Pid = Environment.ProcessId, Version = TiaOpenness.Gui.ViewModels.MainViewModel.AppVersion } }, deadline.Token).ConfigureAwait(false);
        var response = await OperatorProtocol.Read<OperatorHandshakeResult>(pipe, deadline.Token).ConfigureAwait(false);
        if (response.Protocol != "tiamcp.operator" || response.Version != 1) throw new InvalidDataException("Invalid host control response.");
        if (!response.Accepted)
        {
            if (response.Error == "HOST_BUSY") throw new InvalidOperationException("A host call is in progress. Wait for it to finish before Stop or Restart.");
            throw new IOException(response.Error ?? "Host shutdown was refused.");
        }
    }
    internal static async Task Stop(VisibleHost host, Func<VisibleHost, Task>? requestShutdown = null,
        Func<VisibleHost, Task<bool>>? confirmForce = null, HostLifecycleTiming? timing = null)
    {
        if (ReportedBusy(host)) throw new InvalidOperationException("A host call is in progress. Wait for it to finish before Stop or Restart.");
        using var process = VerifiedProcess(host);
        var settings = timing ?? HostLifecycleTiming.Default;
        try { await (requestShutdown ?? RequestShutdown)(host).ConfigureAwait(false); }
        catch (Exception error) when (error is IOException or TimeoutException or OperationCanceledException)
        { Trace.TraceWarning("Graceful host shutdown request failed: {0}", error.GetType().Name); }
        using var deadline = new CancellationTokenSource(settings.Grace);
        try { await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false); return; }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested) /* swallow(teardown): only an explicit human confirmation may force termination after the grace period */ { }
        if (process.HasExited) return;
        if (ReportedBusy(host)) throw new InvalidOperationException("A host call is in progress. Wait for it to finish before Stop or Restart.");
        if (confirmForce == null || !await confirmForce(host).WaitAsync(settings.ConfirmationWait).ConfigureAwait(false))
            throw new TimeoutException("The host did not stop gracefully. TIA may need a restart if the host is forcibly terminated.");
        if (ReportedBusy(host)) throw new InvalidOperationException("A host call is in progress. Wait for it to finish before Stop or Restart.");
        using var force = VerifiedProcess(host);
        force.Kill();
        await force.WaitForExitAsync().ConfigureAwait(false);
    }
    internal static async Task<Process> Restart(VisibleHost host, ProcessStartInfo start,
        Func<VisibleHost, Task>? requestShutdown = null, Func<VisibleHost, Task<bool>>? confirmForce = null, HostLifecycleTiming? timing = null)
    {
        if (host.Transport != "http" || start.UseShellExecute || !Path.IsPathFullyQualified(start.FileName) || !File.Exists(start.FileName))
            throw new InvalidOperationException("A verified HTTP restart configuration is required.");
        await Stop(host, requestShutdown, confirmForce, timing).ConfigureAwait(false);
        return Process.Start(start) ?? throw new IOException("Host restart did not start a process.");
    }
}
