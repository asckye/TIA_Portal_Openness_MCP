internal static class Program
{
    private static int Main()
    {
        Console.Error.WriteLine("Use python scripts/checks/Test-DotnetSuites.py --suite foundation or --suite foundation-api, or dotnet test on this project.");
        return 2;
    }
}
