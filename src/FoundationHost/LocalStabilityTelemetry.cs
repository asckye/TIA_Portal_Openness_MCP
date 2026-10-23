using System.Diagnostics;
using System.Text.Json;

namespace TiaMcp.FoundationHost;

// Opt-in offline-fixture diagnostics. No native calls or MCP protocol changes.
internal sealed class LocalStabilityTelemetry : IDisposable
{
    private readonly Timer timer;
    private readonly string path;
    private readonly object sync = new();
    private static readonly object StartupSync = new();
    private LocalStabilityTelemetry(string path)
    {
        this.path = path; Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        timer = new Timer(_ => Sample(), null, 0, 200);
    }
    internal static LocalStabilityTelemetry? Start() => Environment.GetEnvironmentVariable("TIA_MCP_TEST_STABILITY_TELEMETRY") is { Length: > 0 } path
        ? new(Path.GetFullPath(path)) : null;
    internal static void StartupStage(string stage)
    {
        if (Environment.GetEnvironmentVariable("TIA_MCP_TEST_STABILITY_TELEMETRY") is not { Length: > 0 } path) return;
        using var process = Process.GetCurrentProcess();
        lock (StartupSync) File.AppendAllText(path + ".startup", JsonSerializer.Serialize(new {
            pid = process.Id,
            stage, elapsedMs = (DateTime.UtcNow - process.StartTime.ToUniversalTime()).TotalMilliseconds,
            cpuMs = process.TotalProcessorTime.TotalMilliseconds, threadPoolThreads = ThreadPool.ThreadCount,
            pending = ThreadPool.PendingWorkItemCount
        }) + "\n");
    }
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
                processorAffinity = Affinity(process), processorCount = Environment.ProcessorCount,
                threadPool = new { threads = ThreadPool.ThreadCount, pending = ThreadPool.PendingWorkItemCount, completed = ThreadPool.CompletedWorkItemCount,
                    availableWorkers, availableIo, minimumWorkers, minimumIo, maximumWorkers, maximumIo },
                gc = new { gc.HeapSizeBytes, gc.TotalCommittedBytes, gc.FragmentedBytes, gc.MemoryLoadBytes, gc.PauseTimePercentage,
                    allocatedBytes = GC.GetTotalAllocatedBytes(), gen0 = GC.CollectionCount(0), gen1 = GC.CollectionCount(1), gen2 = GC.CollectionCount(2) }
            }) + "\n");
        }
    }
    // Processor affinity exists only on Windows and Linux; other platforms record no mask.
    private static string? Affinity(Process process) => OperatingSystem.IsWindows() || OperatingSystem.IsLinux()
        ? unchecked((ulong)process.ProcessorAffinity.ToInt64()).ToString("X") : null;
    public void Dispose() => timer.Dispose();
}
