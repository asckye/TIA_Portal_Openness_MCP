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

        // Parallel shard processes share one output directory, and their worker fixtures hash this file at startup.
        // File.WriteAllText truncates it in place, so another process could hash an empty file or be refused by the
        // writer's handle and exit before its handshake. Write the content once and replace the file atomically.
        public static void WriteFixtureFile(string path, string content)
        {
            for (int attempt = 0; ; attempt++)
            {
                string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    if (File.Exists(path) && File.ReadAllText(path) == content) return;
                    File.WriteAllText(temporary, content);
                    if (File.Exists(path)) File.Replace(temporary, path, null);
                    else File.Move(temporary, path);
                    return;
                }
                catch (Exception error) when ((error is IOException || error is UnauthorizedAccessException) && attempt < 100)
                {
                    // Another process is reading or replacing the file; its content is checked again.
                    System.Threading.Thread.Sleep(10);
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
        }
    }
}
