using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using TiaOpenness.Shared;
using Xunit;

namespace TiaOpenness.Shared.Tests
{
    // Linked into the GUI suite to exercise both copies of the shared implementation.
    public sealed class DataLocationsTests : IDisposable
    {
        private readonly string scratch = Path.Combine(Path.GetTempPath(), "tia-data-" + Guid.NewGuid().ToString("N"));
        private string Bundle => Path.Combine(scratch, "bundle");
        private string Local => Path.Combine(scratch, "local");
        private string Temp => Path.Combine(scratch, "temp");
        private string Output => Path.Combine(Bundle, "runtime", "v21");

        public DataLocationsTests()
        {
            Directory.CreateDirectory(Output);
            Directory.CreateDirectory(Local);
            Directory.CreateDirectory(Temp);
            Put(Path.Combine(Bundle, "manifest", "package-manifest.json"), "{}");
        }

        private static void Put(string path, string contents)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, contents);
        }

        private DataLocations Resolve(string? configured = null, string? output = null) =>
            DataLocations.Resolve(output ?? Output, configured!, Local, Temp);

        [Fact]
        public void Explicit_data_root_precedes_bundle_and_does_not_probe_it()
        {
            string configured = Path.Combine(scratch, "override 中文");
            var locations = Resolve(configured);
            Assert.Equal(configured, locations.Root);
            Assert.Equal(Path.Combine(configured, "temp"), locations.TempDirectory);
            Assert.False(Directory.Exists(Path.Combine(Bundle, "data")));
        }

        [Fact]
        public void Absolute_override_accepts_forward_slashes()
        {
            string configured = Path.Combine(scratch, "override").Replace('\\', '/');
            Assert.Equal(Path.GetFullPath(configured), Resolve(configured).Root);
        }

        [Fact]
        public void Process_root_stays_cached_when_the_environment_changes()
        {
            string? previous = Environment.GetEnvironmentVariable("TIA_MCP_DATA_DIRECTORY");
            try
            {
                var first = DataLocations.Current;
                Environment.SetEnvironmentVariable("TIA_MCP_DATA_DIRECTORY", Path.Combine(scratch, "second"));
                Assert.Same(first, DataLocations.Current);
            }
            finally
            {
                Environment.SetEnvironmentVariable("TIA_MCP_DATA_DIRECTORY", previous);
            }
        }

        [Theory]
        [InlineData("relative")]
        [InlineData("C:relative")]
        [InlineData("\\relative")]
        public void Relative_data_override_is_rejected(string configured)
        {
            Assert.Throws<ArgumentException>(() => Resolve(configured));
            Assert.False(Directory.Exists(Path.Combine(Bundle, "data")));
        }

        [Fact]
        public void Writable_bundle_creates_data_and_removes_its_probe()
        {
            var locations = Resolve();
            Assert.Equal(Path.Combine(Bundle, "data"), locations.Root);
            Assert.True(Directory.Exists(locations.Root));
            Assert.Empty(Directory.GetFileSystemEntries(locations.Root));
        }

        [Theory]
        [InlineData("diagnostics")]
        [InlineData("leases")]
        [InlineData("config")]
        [InlineData("ui")]
        [InlineData("logs")]
        [InlineData("reports")]
        [InlineData("temp")]
        public void Subdirectories_use_the_documented_names(string name)
        {
            var locations = Resolve();
            string actual = name switch
            {
                "diagnostics" => locations.Diagnostics(null!),
                "leases" => locations.LeasesDirectory,
                "config" => locations.ConfigDirectory,
                "ui" => Path.GetDirectoryName(locations.UiFilePath)!,
                "logs" => locations.LogsDirectory,
                "reports" => locations.ReportsDirectory,
                _ => locations.TempDirectory
            };
            Assert.Equal(Path.Combine(locations.Root, name), actual);
            if (name == "logs" || name == "temp") Assert.True(Directory.Exists(actual));
        }

        [Fact]
        public void Unwritable_bundle_falls_back_and_keeps_that_decision()
        {
            // A file occupying the directory makes creation fail on every test account,
            // including administrators, without editing machine ACLs.
            string blocked = Path.Combine(Bundle, "data");
            File.WriteAllText(blocked, "occupied");
            var locations = Resolve();
            AssertFallback(locations);
            File.Delete(blocked);
            AssertFallback(locations);
            Assert.False(Directory.Exists(blocked));
        }

        [Fact]
        public void Output_without_a_bundle_uses_the_original_locations()
        {
            AssertFallback(Resolve(output: Temp));
        }

        private void AssertFallback(DataLocations locations)
        {
            Assert.Null(locations.Root);
            Assert.Equal(Path.Combine(Local, "TiaMcp", "diagnostics"), locations.Diagnostics(null!));
            Assert.Equal(Path.Combine(Local, "TiaMcp", "instance-leases"), locations.LeasesDirectory);
            Assert.Equal(Path.Combine(Local, "TiaPortalMcp"), locations.ConfigDirectory);
            Assert.Equal(Path.Combine(Local, "TiaOpennessStudio", "ui.settings"), locations.UiFilePath);
            Assert.Equal(Temp, locations.LogsDirectory);
            Assert.Equal(Path.Combine(Temp, "TiaMcpReports"), locations.ReportsDirectory);
            Assert.Equal(Temp, locations.TempDirectory);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Diagnostics_override_precedes_both_data_root_sources(bool useOverride)
        {
            var locations = Resolve(useOverride ? Path.Combine(scratch, "override") : null);
            string diagnostics = Path.Combine(scratch, "journal");
            Assert.Equal(diagnostics, locations.Diagnostics(diagnostics));
            Assert.Equal(Path.Combine(locations.Root, "diagnostics"), locations.Diagnostics(" "));
            // Validation stays with the writer/reader so their existing exception types survive.
            Assert.Equal("relative", locations.Diagnostics("relative"));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Config_and_ui_migration_copies_missing_files_without_overwriting(bool useOverride)
        {
            var locations = Resolve(useOverride ? Path.Combine(scratch, "override") : null);
            string oldConfig = Path.Combine(Local, "TiaPortalMcp");
            string oldUi = Path.Combine(Local, "TiaOpennessStudio", "ui.settings");
            Put(Path.Combine(oldConfig, "http-v21.json"), "old http");
            Put(Path.Combine(oldConfig, "client.json"), "old client");
            Put(oldUi, "language=Chinese\ntheme=Dark");
            Put(Path.Combine(locations.Root, "config", "client.json"), "new client");

            Assert.Equal("old http", File.ReadAllText(Path.Combine(locations.ConfigDirectory, "http-v21.json")));
            Assert.Equal("new client", File.ReadAllText(Path.Combine(locations.ConfigDirectory, "client.json")));
            Assert.Equal("language=Chinese\ntheme=Dark", File.ReadAllText(locations.UiFilePath));
            File.WriteAllText(locations.UiFilePath, "theme=Light");
            var nextProcess = Resolve(useOverride ? locations.Root : null);
            Assert.Equal("theme=Light", File.ReadAllText(nextProcess.UiFilePath));
            Assert.Equal("old http", File.ReadAllText(Path.Combine(oldConfig, "http-v21.json")));
            Assert.Equal("old client", File.ReadAllText(Path.Combine(oldConfig, "client.json")));
            Assert.Equal("language=Chinese\ntheme=Dark", File.ReadAllText(oldUi));
        }

        [Fact]
        public void Migration_waits_for_first_use_and_does_not_copy_logs_or_scratch()
        {
            Put(Path.Combine(Local, "TiaPortalMcp", "client.json"), "config");
            Put(Path.Combine(Local, "TiaOpennessStudio", "ui.settings"), "theme=Dark");
            Put(Path.Combine(Local, "TiaMcp", "diagnostics", "calls-old.jsonl"), "old");
            Put(Path.Combine(Local, "TiaMcp", "instance-leases", "old.json"), "old");
            Put(Path.Combine(Temp, "TiaMcpServer.log"), "old");
            Put(Path.Combine(Temp, "scratch.xml"), "old");
            var locations = Resolve();
            Assert.Empty(Directory.GetFileSystemEntries(locations.Root));
            Assert.False(Directory.Exists(locations.Diagnostics(null!)));
            Assert.False(Directory.Exists(locations.LeasesDirectory));
            Assert.Empty(Directory.GetFiles(locations.LogsDirectory));
            Assert.Empty(Directory.GetFiles(locations.TempDirectory));
            Assert.True(File.Exists(Path.Combine(locations.ConfigDirectory, "client.json")));
            Assert.True(File.Exists(locations.UiFilePath));
        }

        [Fact]
        public void Production_sources_keep_user_and_temp_paths_in_the_resolver()
        {
            string root = RepositoryRoot();
            var pattern = new Regex(@"\bPath\s*\.\s*GetTempPath\s*\(\s*\)|\bSpecialFolder\s*\.\s*(?:LocalApplicationData|ApplicationData)\b");
            var allowed = new System.Collections.Generic.Dictionary<string, int>
            {
                ["src/Studio/Gui/Configuration/ClientProfiles.cs"] = 2,
                ["src/Engine/Cli/McpConfigInstaller.cs"] = 1,
                // The crash log beside the executable and its original null-path fallback stay unchanged.
                ["src/Studio/Gui/App.xaml.cs"] = 1
            };
            foreach (string tree in new[] { "src", "src/Shared", "src/Studio" })
                foreach (string path in Directory.EnumerateFiles(Path.Combine(root, tree), "*.cs", SearchOption.AllDirectories))
                {
                    string relative = path.Substring(root.Length + 1).Replace('\\', '/');
                    if (relative.Split('/').Any(p => p == "bin" || p == "bin-v20" || p == "obj" || p == "obj-v20" || p == "Generated") ||
                        relative.EndsWith(".g.cs", StringComparison.Ordinal) || relative.EndsWith(".g.i.cs", StringComparison.Ordinal) ||
                        relative.EndsWith(".generated.cs", StringComparison.Ordinal) || relative.EndsWith(".designer.cs", StringComparison.Ordinal) ||
                        relative == "src/Shared/DataLocations.cs") continue;
                    int count = pattern.Matches(File.ReadAllText(path)).Count;
                    allowed.TryGetValue(relative, out int maximum);
                    Assert.True(count <= maximum, relative + ": direct per-user/temp path uses " + count + " > " + maximum);
                }
        }

        private static string RepositoryRoot([CallerFilePath] string source = "")
        {
            for (var directory = new DirectoryInfo(Path.GetDirectoryName(source)!); directory != null; directory = directory.Parent)
                if (File.Exists(Path.Combine(directory.FullName, "AGENTS.md"))) return directory.FullName;
            throw new DirectoryNotFoundException("Repository root not found.");
        }

        public void Dispose()
        {
            Assert.Equal(new DirectoryInfo(Path.GetTempPath()).FullName.TrimEnd(Path.DirectorySeparatorChar),
                new DirectoryInfo(Path.GetFullPath(scratch)).Parent!.FullName.TrimEnd(Path.DirectorySeparatorChar));
            Directory.Delete(scratch, true);
        }
    }
}
