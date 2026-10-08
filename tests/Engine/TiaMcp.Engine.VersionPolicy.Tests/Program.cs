using System;

internal static class Program
{
    private static int Main()
    {
        Console.Error.WriteLine("Use dotnet run --project build-tools/release -- test-suites -Suite version-policy, or dotnet test on this project.");
        return 2;
    }
}
