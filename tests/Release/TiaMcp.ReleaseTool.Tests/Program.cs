using System;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.FirstOrDefault()?.StartsWith("--desktop-", StringComparison.Ordinal) == true)
            return TiaMcp.ReleaseTool.Tests.IsolatedDesktopProcessTests.RunFixture(args);
        Console.Error.WriteLine("Use dotnet test on this project.");
        return 2;
    }
}
