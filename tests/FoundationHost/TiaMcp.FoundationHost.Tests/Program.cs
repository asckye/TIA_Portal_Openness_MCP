extern alias foundationhost;
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.FirstOrDefault() == "--foundation-http-fixture")
        {
            using var stopping = new CancellationTokenSource();
            _ = Task.Run(async () => { await Console.In.ReadLineAsync(); stopping.Cancel(); });
            // Select the bundle like src/FoundationHost/Program.cs does; the test output layout has no bundle anchor under check-all.
            var root = TiaOpenness.Shared.BundleLayout.ExtractRootOption(args.Skip(1).ToArray(), out var hostArgs);
            TiaOpenness.Shared.BundleLayout.Initialize(AppContext.BaseDirectory, root);
            var options = foundationhost::TiaMcp.FoundationHost.HostOptions.Parse(hostArgs, AppContext.BaseDirectory);
            using var logger = new foundationhost::TiaMcp.FoundationHost.HostFileLogger(options.ReleaseKey);
            return foundationhost::TiaMcp.FoundationHost.EngineReleaseHost.RunForTest(options, logger, SessionFixtureTool.Add, stopping.Token).GetAwaiter().GetResult();
        }
        if (args.FirstOrDefault() == "--process-lease-fixture") return FoundationProcessLeaseTests.RunFixture(args);
        if (args.Contains("--engine-worker") || args.Contains("--native-session")) return EngineSessionFixture.Run(args);
        Console.Error.WriteLine("Use dotnet run --project build-tools/release -- test-suites -Suite foundation or -Suite foundation-api, or dotnet test on this project.");
        return 2;
    }
}
