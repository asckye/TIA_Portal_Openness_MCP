extern alias foundationhost;
extern alias enginehost;
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.FirstOrDefault() == "--foundation-http-fixture")
        {
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
            int invalid = Array.IndexOf(hostArgs, "--fixture-invalid-catalog");
            var fixtureArgs = invalid < 0 ? hostArgs : hostArgs.Take(invalid).ToArray();
            var options = fixtureArgs.Contains("--engine-worker") || fixtureArgs.Contains("--engine-catalog")
                ? FrozenEngineOptions(fixtureArgs)
                : foundationhost::TiaMcp.FoundationHost.HostOptions.Parse(fixtureArgs, AppContext.BaseDirectory);
            string? overrides = Environment.GetEnvironmentVariable("TIA_MCP_WORKER_TIMEOUTS");
            var production = new TiaMcp.WorkerChannel.WorkerTimeoutPolicy(options.EngineTimeoutSeconds, options.WorkerTimeoutConfig, overrides).Budgets;
            var scaled = scaledJson == null ? null : new TiaMcp.WorkerChannel.WorkerTimeoutPolicy(options.EngineTimeoutSeconds, scaledJson, overrides).Budgets;
            // Console.In's synchronized reader can block even in ReadLineAsync. Keep its lifetime wait off the pool used by Kestrel.
            var input = new Thread(() =>
            {
                // Any other line, or the end of input, stops the host as before.
                while (Console.In.ReadLine() is { } line)
                {
                    if (line == FixtureStartupPressure.ReleaseCommand && pressure != null) { pressure.ReleaseCatalog(); continue; }
                    if (scaled == null || line is not (FixtureHostWorkerBudgets.Command + "scaled" or FixtureHostWorkerBudgets.Command + "production")) break;
                    Console.Out.WriteLine(FixtureHostWorkerBudgets.Apply(line.EndsWith("scaled", StringComparison.Ordinal) ? scaled : production));
                }
                stopping.Cancel();
            }) { IsBackground = true, Name = "HTTP fixture input" };
            input.Start();
            // Keep export validation in the test driver after runtime catalog loading retires.
            if (options.EngineCatalog != null)
                _ = new enginehost::TiaMcp.FoundationHost.EngineCatalog(options.EngineCatalog, options.EngineWorkerExe!, options.ReleaseKey);
            if (args.Contains("--fixture-invalid-catalog"))
                _ = new enginehost::TiaMcp.FoundationHost.EngineCatalog(args[Array.IndexOf(args, "--fixture-invalid-catalog") + 1], options.WorkerExe, options.ReleaseKey);
            foundationhost::TiaMcp.FoundationHost.LocalStabilityTelemetry.StartupStage("fixture.export.validated");
            using var logger = new foundationhost::TiaMcp.FoundationHost.HostFileLogger(options.ReleaseKey);
            Func<foundationhost::TiaMcp.FoundationHost.IFoundationSessionWorker, IReadOnlyList<ModelContextProtocol.Server.McpServerTool>,
                IReadOnlyList<ModelContextProtocol.Server.McpServerTool>>? tools = options.PlcWorker ? SessionFixtureTool.PlcTools : null;
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
            return foundationhost::TiaMcp.FoundationHost.EngineReleaseHost.RunForTest(options, logger, options.PlcWorker ? source => source : SessionFixtureTool.Add, stopping.Token,
                tools).GetAwaiter().GetResult();
        }
        if (args.FirstOrDefault() == "--bridge-pool-probe") return SharedWorkerRegressionTests.RunBridgePoolProbeAsync(args[1]).GetAwaiter().GetResult();
        if (args.FirstOrDefault() == "--process-lease-fixture") return FoundationProcessLeaseTests.RunFixture(args);
        if (args.Contains("--engine-worker") || args.Contains("--native-session")) return EngineSessionFixture.Run(args);
        Console.Error.WriteLine("Use dotnet run --project build-tools/release -- test-suites -Suite foundation or -Suite foundation-api, or dotnet test on this project.");
        return 2;
    }

    // This driver keeps the frozen engine-client tests reachable until P8-04l. Production
    // HostOptions refuses the engine options; only this test executable strips them, with
    // the checks the engine host applied to them before the default switch (P8-04j2).
    private static foundationhost::TiaMcp.FoundationHost.HostOptions FrozenEngineOptions(string[] hostArgs)
    {
        int workerIndex = Array.IndexOf(hostArgs, "--engine-worker"), catalogIndex = Array.IndexOf(hostArgs, "--engine-catalog");
        if (hostArgs.Contains("--plc-worker"))
            throw new ArgumentException("--plc-worker cannot be combined with --engine-worker or --engine-catalog.");
        if (workerIndex < 0 || catalogIndex < 0 || workerIndex + 1 >= hostArgs.Length || catalogIndex + 1 >= hostArgs.Length)
            throw new ArgumentException("The engine host requires release 20 or 21 and both --engine-worker and --engine-catalog.");
        string worker = Path.GetFullPath(hostArgs[workerIndex + 1]), catalog = Path.GetFullPath(hostArgs[catalogIndex + 1]);
        var remaining = new List<string>();
        for (int index = 0; index < hostArgs.Length; index++)
        {
            if (hostArgs[index] is "--engine-worker" or "--engine-catalog") { index++; continue; }
            remaining.Add(hostArgs[index]);
        }
        var parsed = foundationhost::TiaMcp.FoundationHost.HostOptions.Parse(remaining.ToArray(), AppContext.BaseDirectory);
        if (parsed.ReleaseKey is not ("20" or "21"))
            throw new ArgumentException("The engine host requires release 20 or 21 and both --engine-worker and --engine-catalog.");
        return new foundationhost::TiaMcp.FoundationHost.HostOptions
        {
            ReleaseKey = parsed.ReleaseKey, BundleRoot = parsed.BundleRoot, WorkerExe = parsed.WorkerExe, BundledWorker = parsed.BundledWorker,
            ApiDirectory = parsed.ApiDirectory, ApiDirectorySource = parsed.ApiDirectorySource, PlcWorker = false,
            EngineWorkerExe = worker, EngineCatalog = catalog, NativeEnabled = parsed.NativeEnabled, CatalogOnly = parsed.CatalogOnly,
            EngineTimeoutSeconds = parsed.EngineTimeoutSeconds, WorkerTimeoutConfig = parsed.WorkerTimeoutConfig, WithUi = parsed.WithUi,
            TiaPortalLocation = parsed.TiaPortalLocation, HostCommand = parsed.HostCommand, OperatorOwner = parsed.OperatorOwner,
            Transport = parsed.Transport, HttpPrefix = parsed.HttpPrefix, ApiKey = parsed.ApiKey, Profile = parsed.Profile
        };
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
            if (!thread.Join(5000)) throw new InvalidOperationException("Owned HTTP startup pressure thread did not exit.");
        busyStopping.Dispose(); catalogReleased.Dispose();
    }
}
