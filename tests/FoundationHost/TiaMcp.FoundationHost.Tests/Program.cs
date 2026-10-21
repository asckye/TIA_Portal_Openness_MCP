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
            var options = foundationhost::TiaMcp.FoundationHost.HostOptions.Parse(invalid < 0 ? hostArgs : hostArgs.Take(invalid).ToArray(), AppContext.BaseDirectory);
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
        if (args.FirstOrDefault() == "--process-lease-fixture") return FoundationProcessLeaseTests.RunFixture(args);
        if (args.Contains("--engine-worker") || args.Contains("--native-session")) return EngineSessionFixture.Run(args);
        Console.Error.WriteLine("Use dotnet run --project build-tools/release -- test-suites -Suite foundation or -Suite foundation-api, or dotnet test on this project.");
        return 2;
    }
}
