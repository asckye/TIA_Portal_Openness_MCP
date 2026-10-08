using System;

internal static class Program
{
    private static int Main()
    {
        Console.Error.WriteLine("Use dotnet run --project build-tools/release -- test-suites -Suite software-read, or dotnet test on this project.");
        return 2;
    }
}
