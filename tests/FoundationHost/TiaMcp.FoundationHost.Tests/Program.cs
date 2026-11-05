extern alias foundationhost;
using TiaMcp.Engine.Tests.Shared;
internal static class Program
{
    private static int Main(string[] args) => TiaMcp.Engine.Tests.Shared.FixtureCrashGuard.Run(() => Run(args));

    private static int Run(string[] args)
    {
        if (args.FirstOrDefault() == "--foundation-http-fixture")
        {
            int faultAt = Array.IndexOf(args, "--fixture-fault");
            string? fault = faultAt < 0 ? null : args[faultAt + 1];
            if (faultAt >= 0) args = args.Where((_, i) => i != faultAt && i != faultAt + 1).ToArray();
            if (fault == "startup") throw new InvalidOperationException("Forced fixture fault (startup)");
            foundationhost::TiaMcp.FoundationHost.LocalStabilityTelemetry.StartupStage("fixture.begin");
            using var stopping = new CancellationTokenSource();
            // Select the bundle like src/FoundationHost/Program.cs does; the test output layout has no bundle anchor under check-all.
            var root = TiaOpenness.Shared.BundleLayout.ExtractRootOption(args.Skip(1).ToArray(), out var hostArgs);
            TiaOpenness.Shared.BundleLayout.Initialize(AppContext.BaseDirectory, root);
            using var pressure = hostArgs.Contains("--fixture-startup-pressure") ? new FixtureStartupPressure(stopping.Token) : null;
            hostArgs = hostArgs.Where(arg => arg != "--fixture-startup-pressure").ToArray();
            // The scaled budgets apply only when the test asks for them (FixtureHostWorkerBudgets); workers start under production.
            int scaledAt = Array.IndexOf(hostArgs, "--fixture-scaled-worker-budgets");
            string? scaledJson = scaledAt < 0 ? null : File.ReadAllText(hostArgs[scaledAt + 1]);
            if (scaledAt >= 0) hostArgs = hostArgs.Where((_, i) => i != scaledAt && i != scaledAt + 1).ToArray();
            var options = foundationhost::TiaMcp.FoundationHost.HostOptions.Parse(hostArgs, AppContext.BaseDirectory);
            string? overrides = Environment.GetEnvironmentVariable("TIA_MCP_WORKER_TIMEOUTS");
            var production = new TiaMcp.WorkerChannel.WorkerTimeoutPolicy(options.EngineTimeoutSeconds, options.WorkerTimeoutConfig, overrides).Budgets;
            var scaled = scaledJson == null ? null : new TiaMcp.WorkerChannel.WorkerTimeoutPolicy(options.EngineTimeoutSeconds, scaledJson, overrides).Budgets;
            // Console.In's synchronized reader can block even in ReadLineAsync. Keep its lifetime wait off the pool used by Kestrel.
            var input = new Thread(() =>
            {
                // Any other line, or the end of input, stops the host as before.
                while (Console.In.ReadLine() is { } line)
                {
                    if (line == "fixture-fault-background" && fault == "background")
                        throw new InvalidOperationException("Forced fixture fault (background)");
                    if (line == FixtureStartupPressure.ReleaseCommand && pressure != null) { pressure.ReleaseCatalog(); continue; }
                    if (scaled == null || line is not (FixtureHostWorkerBudgets.Command + "scaled" or FixtureHostWorkerBudgets.Command + "production")) break;
                    Console.Out.WriteLine(FixtureHostWorkerBudgets.Apply(line.EndsWith("scaled", StringComparison.Ordinal) ? scaled : production));
                }
                stopping.Cancel();
            }) { IsBackground = true, Name = "HTTP fixture input" };
            input.Start();
            foundationhost::TiaMcp.FoundationHost.LocalStabilityTelemetry.StartupStage("fixture.options.parsed");
            using var logger = new foundationhost::TiaMcp.FoundationHost.HostFileLogger(options.ReleaseKey);
            Func<foundationhost::TiaMcp.FoundationHost.IFoundationSessionWorker, IReadOnlyList<ModelContextProtocol.Server.McpServerTool>,
                IReadOnlyList<ModelContextProtocol.Server.McpServerTool>>? tools = SessionFixtureTool.PlcTools;
            if (scaled != null)
            {
                var fixtureTools = tools;
                tools = (worker, source) => FixtureHostWorkerBudgets.Register(worker, fixtureTools == null ? source : fixtureTools(worker, source));
            }
            if (pressure != null)
            {
                var fixtureTools = tools;
                tools = (worker, source) => {
                    pressure.WaitForCatalog(stopping.Token);
                    return fixtureTools == null ? source : fixtureTools(worker, source);
                };
            }
            return foundationhost::TiaMcp.FoundationHost.SharedReleaseHost.RunForTest(options, logger, source => source, stopping.Token,
                tools).GetAwaiter().GetResult();
        }
        if (args.FirstOrDefault() == "--bridge-pool-probe") return SharedWorkerRegressionTests.RunBridgePoolProbeAsync(args[1]).GetAwaiter().GetResult();
        if (args.FirstOrDefault() == "--process-lease-fixture") return FoundationProcessLeaseTests.RunFixture(args);
        if (args.Contains("--native-session")) return EngineSessionFixture.Run(args);
        Console.Error.WriteLine("Use dotnet run --project build-tools/release -- test-suites -Suite foundation or -Suite foundation-api, or dotnet test on this project.");
        return 2;
    }
}

// Child-process-only pressure: neither affinity nor busy threads affect the test runner or another shard.
internal sealed class FixtureStartupPressure : IDisposable
{
    internal const string ReleaseCommand = "fixture-release-catalog";
    private readonly CancellationTokenSource busyStopping;
    private readonly ManualResetEventSlim catalogReleased = new();
    private readonly Thread[] busy;
    internal FixtureStartupPressure(CancellationToken stopping)
    {
        if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
        {
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            ulong available = unchecked((ulong)process.ProcessorAffinity.ToInt64()), selected = 0;
            for (int bit = 0, count = 0; bit < 64 && count < 2; bit++)
                if ((available & (1UL << bit)) != 0) { selected |= 1UL << bit; count++; }
            process.ProcessorAffinity = unchecked((nint)(long)selected);
        }
        busyStopping = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        using var started = new CountdownEvent(2);
        busy = Enumerable.Range(0, 2).Select(_ => new Thread(() => {
            started.Signal();
            while (!busyStopping.IsCancellationRequested) Thread.SpinWait(1024);
        }) { IsBackground = true, Name = "HTTP startup CPU pressure" }).ToArray();
        foreach (var thread in busy) thread.Start();
        started.Wait();
        foundationhost::TiaMcp.FoundationHost.LocalStabilityTelemetry.StartupStage("fixture.pressure.started");
    }
    internal void ReleaseCatalog() { busyStopping.Cancel(); catalogReleased.Set(); }
    internal void WaitForCatalog(CancellationToken token)
    {
        foundationhost::TiaMcp.FoundationHost.LocalStabilityTelemetry.StartupStage("fixture.catalog.waiting");
        // The parent releases this only after health succeeds. An eager pre-listen catalog cannot pass the existing startup wait.
        catalogReleased.Wait(token);
        foundationhost::TiaMcp.FoundationHost.LocalStabilityTelemetry.StartupStage("fixture.catalog.released");
    }
    public void Dispose()
    {
        busyStopping.Cancel();
        foreach (var thread in busy)
            if (!thread.Join(TestDeadlines.HangMilliseconds)) throw new InvalidOperationException("Owned HTTP startup pressure thread did not exit.");
        busyStopping.Dispose(); catalogReleased.Dispose();
    }
}
