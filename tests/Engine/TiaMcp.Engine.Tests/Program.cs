using System;

namespace TiaMcp.Engine.Tests
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            if (args.Length == 2 && args[0] == "--local-process-fixture") return McpLocalProcessTests.Child(args[1]);
            Console.Error.WriteLine("Use dotnet run --project build-tools/release -- test-suites -Suite offline (or -Suite offline-v20), or dotnet test on this project.");
            return 2;
        }
    }
}
