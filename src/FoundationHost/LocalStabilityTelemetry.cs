using System.Diagnostics;
using System.Text.Json;

namespace TiaMcp.FoundationHost;

// Opt-in offline-fixture diagnostics. No native calls or MCP protocol changes.
internal sealed class LocalStabilityTelemetry : IDisposable
{
    private readonly Timer timer;
    private readonly string path;
    private readonly object sync = new();
    private LocalStabilityTelemetry(string path)
    {
        this.path = path; Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        timer = new Timer(_ => Sample(), null, 0, 200);
    }
    internal static LocalStabilityTelemetry? Start() => Environment.GetEnvironmentVariable("TIA_MCP_TEST_STABILITY_TELEMETRY") is { Length: > 0 } path
        ? new(Path.GetFullPath(path)) : null;
    private void Sample()
    {
        lock (sync)
        {
            using var process = Process.GetCurrentProcess();
            ThreadPool.GetAvailableThreads(out var availableWorkers, out var availableIo);
            ThreadPool.GetMinThreads(out var minimumWorkers, out var minimumIo);
            ThreadPool.GetMaxThreads(out var maximumWorkers, out var maximumIo);
            var gc = GC.GetGCMemoryInfo();
            File.AppendAllText(path, JsonSerializer.Serialize(new {
                atUtc = DateTime.UtcNow, pid = process.Id, image = Environment.ProcessPath,
                handleCount = process.HandleCount, threads = process.Threads.Count,
                processorAffinity = unchecked((ulong)process.ProcessorAffinity.ToInt64()).ToString("X"), processorCount = Environment.ProcessorCount,
                threadPool = new { threads = ThreadPool.ThreadCount, pending = ThreadPool.PendingWorkItemCount, completed = ThreadPool.CompletedWorkItemCount,
                    availableWorkers, availableIo, minimumWorkers, minimumIo, maximumWorkers, maximumIo },
                gc = new { gc.HeapSizeBytes, gc.TotalCommittedBytes, gc.FragmentedBytes, gc.MemoryLoadBytes, gc.PauseTimePercentage,
                    allocatedBytes = GC.GetTotalAllocatedBytes(), gen0 = GC.CollectionCount(0), gen1 = GC.CollectionCount(1), gen2 = GC.CollectionCount(2) }
            }) + "\n");
        }
    }
    public void Dispose() => timer.Dispose();
}
