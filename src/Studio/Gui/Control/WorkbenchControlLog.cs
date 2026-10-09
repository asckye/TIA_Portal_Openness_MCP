using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TiaOpenness.Shared;

namespace TiaOpenness.Gui.ControlChannel;

internal sealed class WorkbenchControlLog(string? path = null)
{
    internal sealed record Activity(string Operation, string Status);
    private readonly string _path = path ?? System.IO.Path.Combine(DataLocations.Current.DiagnosticsDirectory,
        "workbench-control-" + Environment.ProcessId + "-" + Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks + ".jsonl");
    private readonly object _gate = new();
    internal void Record(WorkbenchControlRequest request, WorkbenchControlResponse response, long elapsedMs)
    {
        try
        {
            // Client labels and parameters are untrusted: retain a digest, never their full contents.
            byte[] arguments = JsonSerializer.SerializeToUtf8Bytes(request.Arguments, request.Arguments.GetType(), WorkbenchControlProtocol.Json);
            string row = JsonSerializer.Serialize(new { utc = DateTimeOffset.UtcNow.ToString("O"), requestId = request.RequestId,
                origin = new { host = request.Origin.Host, releaseKey = request.Origin.ReleaseKey,
                    hostProcessId = request.Origin.HostProcessId, mcpSession = request.Origin.McpSession },
                operation = request.Operation, argumentDigest = Convert.ToHexStringLower(SHA256.HashData(arguments)),
                status = response.Status, refusal = response.Refusal, elapsedMs }, WorkbenchControlProtocol.Json) + "\n";
            lock (_gate)
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
                JournalRetention.Load(JournalRetention.SettingsPath).Rotate(_path, Encoding.UTF8.GetByteCount(row));
                File.AppendAllText(_path, row, new UTF8Encoding(false));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { Trace.TraceWarning("Workbench control log unavailable: {0}", ex.GetType().Name); }
    }
    internal static Activity? ReadActivity(string requestId, string? directory = null)
    {
        directory ??= DataLocations.Current.DiagnosticsDirectory;
        if (!WorkbenchControlProtocol.IsHex(requestId, 32) || !Directory.Exists(directory)) return null;
        string[] files;
        try { files = Directory.GetFiles(directory, "workbench-control-*.jsonl*"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { Trace.TraceInformation("Workbench control logs unavailable: {0}", ex.GetType().Name); return null; }
        foreach (string file in files)
        {
            try
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                string? row;
                while ((row = reader.ReadLine()) != null)
                {
                    // A partial tail from an interrupted append has no complete object.
                    if (!row.EndsWith('}') || !row.Contains(requestId, StringComparison.Ordinal)) continue;
                    using var json = JsonDocument.Parse(row);
                    var item = json.RootElement;
                    if (item.GetProperty("requestId").GetString() == requestId)
                        return new(item.GetProperty("operation").GetString()!, item.GetProperty("status").GetString()!);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or InvalidOperationException)
            { Trace.TraceInformation("Workbench control log changed while reading: {0}", ex.GetType().Name); }
        }
        return null;
    }
}
