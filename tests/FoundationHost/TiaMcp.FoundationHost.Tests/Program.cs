internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Contains("--engine-worker") || args.Contains("--native-session")) return EngineSessionFixture.Run(args);
        Console.Error.WriteLine("Use python scripts/checks/Test-DotnetSuites.py --suite foundation or --suite foundation-api, or dotnet test on this project.");
        return 2;
    }
}
