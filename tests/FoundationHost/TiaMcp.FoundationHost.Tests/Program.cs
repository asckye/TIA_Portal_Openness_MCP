extern alias foundationhost;
extern alias enginehost;
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.FirstOrDefault() == "--foundation-http-fixture")
        {
            using var stopping = new CancellationTokenSource();
            // Select the bundle like src/FoundationHost/Program.cs does; the test output layout has no bundle anchor under check-all.
            var root = TiaOpenness.Shared.BundleLayout.ExtractRootOption(args.Skip(1).ToArray(), out var hostArgs);
            TiaOpenness.Shared.BundleLayout.Initialize(AppContext.BaseDirectory, root);
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
            _ = Task.Run(async () =>
            {
                // Any other line, or the end of input, stops the host as before.
                while (await Console.In.ReadLineAsync() is { } line && scaled != null
                    && line is FixtureHostWorkerBudgets.Command + "scaled" or FixtureHostWorkerBudgets.Command + "production")
                    Console.Out.WriteLine(FixtureHostWorkerBudgets.Apply(line.EndsWith("scaled", StringComparison.Ordinal) ? scaled : production));
                stopping.Cancel();
            });
            // Keep export validation in the test driver after runtime catalog loading retires.
            if (options.EngineCatalog != null)
                _ = new enginehost::TiaMcp.FoundationHost.EngineCatalog(options.EngineCatalog, options.EngineWorkerExe!, options.ReleaseKey);
            if (args.Contains("--fixture-invalid-catalog"))
                _ = new enginehost::TiaMcp.FoundationHost.EngineCatalog(args[Array.IndexOf(args, "--fixture-invalid-catalog") + 1], options.WorkerExe, options.ReleaseKey);
            using var logger = new foundationhost::TiaMcp.FoundationHost.HostFileLogger(options.ReleaseKey);
            Func<foundationhost::TiaMcp.FoundationHost.IFoundationSessionWorker, IReadOnlyList<ModelContextProtocol.Server.McpServerTool>,
                IReadOnlyList<ModelContextProtocol.Server.McpServerTool>>? tools = options.PlcWorker ? SessionFixtureTool.PlcTools : null;
            if (scaled != null)
            {
                var fixtureTools = tools;
                tools = (worker, source) => FixtureHostWorkerBudgets.Register(worker, fixtureTools == null ? source : fixtureTools(worker, source));
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
