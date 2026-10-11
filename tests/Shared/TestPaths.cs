using System;
using System.IO;

namespace TiaMcp.Engine.Tests.Shared
{
    public static class TestPaths
    {
        public static string RepositoryRoot(string start)
        {
            for (var directory = new DirectoryInfo(start); directory != null; directory = directory.Parent)
                if (File.Exists(Path.Combine(directory.FullName, "manifest", "package-manifest.json")) &&
                    (File.Exists(Path.Combine(directory.FullName, ".git")) || Directory.Exists(Path.Combine(directory.FullName, ".git"))))
                    return directory.FullName;
            throw new DirectoryNotFoundException("Test output must be inside this repository: " + start);
        }

        public static string RegisteredOutput(string isolatedOutput)
        {
            var directory = new DirectoryInfo(isolatedOutput);
            if (directory.Parent?.Parent?.Parent?.Parent?.Name == "check-all")
            {
                var bin = directory.Parent.Parent.Parent.Parent.Parent!;
                return Path.Combine(bin.FullName, directory.Parent.Name, directory.Name);
            }
            return isolatedOutput;
        }

        public static string WorkerFixture(string project, string file) => Path.Combine(
            RepositoryRoot(AppContext.BaseDirectory), "tests", "WorkerChannel", project, "bin", "Release", "net10.0", file);
    }
}
