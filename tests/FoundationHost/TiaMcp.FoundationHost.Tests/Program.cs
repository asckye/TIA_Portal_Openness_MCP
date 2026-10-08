internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Contains("--engine-worker") || args.Contains("--native-session")) return EngineSessionFixture.Run(args);
        Console.Error.WriteLine("Use dotnet run --project build-tools/release -- test-suites -Suite foundation or -Suite foundation-api, or dotnet test on this project.");
        return 2;
    }
}
