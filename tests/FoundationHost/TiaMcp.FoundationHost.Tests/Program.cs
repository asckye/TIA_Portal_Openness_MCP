extern alias foundationhost;
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.FirstOrDefault() == "--foundation-http-fixture")
        {
            using var stopping = new CancellationTokenSource();
            _ = Task.Run(async () => { await Console.In.ReadLineAsync(); stopping.Cancel(); });
            var options = foundationhost::TiaMcp.FoundationHost.HostOptions.Parse(args.Skip(1).ToArray(), AppContext.BaseDirectory);
            using var logger = new foundationhost::TiaMcp.FoundationHost.HostFileLogger(options.ReleaseKey);
            return foundationhost::TiaMcp.FoundationHost.EngineReleaseHost.Run(options, logger, stopping.Token).GetAwaiter().GetResult();
        }
        if (args.FirstOrDefault() == "--process-lease-fixture") return FoundationProcessLeaseTests.RunFixture(args);
        if (args.Contains("--engine-worker") || args.Contains("--native-session")) return EngineSessionFixture.Run(args);
        Console.Error.WriteLine("Use dotnet run --project build-tools/release -- test-suites -Suite foundation or -Suite foundation-api, or dotnet test on this project.");
        return 2;
    }
}
