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

        [Theory]
        [InlineData("14sp1")]
        [InlineData("15.1")]
        [InlineData("20")]
        [InlineData("21")]
        [InlineData("studio")]
        public void Logs_use_release_directories_and_process_start_names(string release)
        {
            var locations = Resolve();
            string file = locations.LogFile("TiaMcpServer.log", release);
            Assert.Equal(Path.Combine(Bundle, "data", "logs", release), Path.GetDirectoryName(file));
            Assert.Contains(System.Diagnostics.Process.GetCurrentProcess().Id + "-", Path.GetFileName(file));
            Assert.Matches(@"TiaMcpServer-\d+-\d{8}-\d{6}-\d{7}-[a-f0-9]{32}\.log$", file);
            locations.AppendLog("TiaMcpServer.log", release, "test");
            Assert.Equal("test" + Environment.NewLine, File.ReadAllText(file));
            Assert.Empty(Directory.GetFiles(Output));
        }

        [Fact]
        public void Read_only_install_uses_user_logs_and_never_executable_directory()
        {
            File.WriteAllText(Path.Combine(Bundle, "data"), "blocked");
            var locations = Resolve();
            string file = locations.LogFile("TiaOpenness.crash.log", "studio");
            Assert.Equal(Path.Combine(Temp, "TiaMcp", "logs", "studio"), Path.GetDirectoryName(file));
            locations.AppendLog("TiaOpenness.crash.log", "studio", "test");
            Assert.Empty(Directory.GetFiles(Output));
            Assert.Equal(Path.Combine(Local, "TiaMcp", "logs", "audit"), locations.AuditDirectory);
        }

        [Fact]
        public void No_writable_log_location_reports_the_purpose_and_target()
        {
            string blocked = Path.Combine(scratch, "blocked"); File.WriteAllText(blocked, "file");
            var locations = DataLocations.Resolve(Temp, null!, blocked, blocked);
            Assert.Contains("IO_FAILED", Assert.Throws<IOException>(() => locations.LogFile("host.log", "21")).Message);
            Assert.Contains(blocked, Assert.Throws<IOException>(() => locations.LogFile("host.log", "21")).Message);
            Assert.Contains("DIAGNOSTIC_WRITE_FAILED", Assert.Throws<IOException>(() => _ = locations.WritableAuditDirectory).Message);
            var missing = DataLocations.Resolve(Temp, null!, "", "");
            // No configured override: an inherited TIA_MCP_DIAGNOSTICS_DIRECTORY (release checks set one) must not hide the failure.
            Assert.Contains("DIAGNOSTIC_WRITE_FAILED", Assert.Throws<IOException>(() => _ = missing.Diagnostics(null!)).Message);
            Assert.False(File.Exists(Path.Combine(Output, "host.log")));
        }

        [Fact]
        public void Invalid_explicit_data_target_never_silently_falls_back()
        {
            string blocked = Path.Combine(scratch, "blocked"); File.WriteAllText(blocked, "file");
            var locations = Resolve(blocked);
            Assert.Contains(blocked, Assert.Throws<IOException>(() => locations.LogFile("host.log", "21")).Message);
            Assert.Empty(Directory.GetFileSystemEntries(Temp));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("relative")]
        public void Missing_or_invalid_LocalAppData_never_uses_the_bundle_python(string? local)
        {
            var error = Assert.Throws<DataLocationIOException>(() => DataLocations.EcosystemPython(null!, local!));
            Assert.Contains("IO_FAILED", error.Message); Assert.Contains("ecosystem-python", error.TargetPath);
            Assert.False(Directory.Exists(Path.Combine(Bundle, "bin-build")));
        }

        [Fact]
        public void Python_override_wins_even_with_no_or_unwritable_LocalAppData()
        {
            string python = Path.Combine(scratch, "explicit-python.exe"); File.WriteAllText(python, "fixture");
            Assert.Equal(python, DataLocations.EcosystemPython(python, null!));
            Assert.Equal(python, DataLocations.EcosystemPython(python, python));
            Assert.Throws<DataLocationIOException>(() => DataLocations.EcosystemPython("", Local));
            Assert.Throws<DataLocationIOException>(() => DataLocations.EcosystemPython("relative", Local));
        }

        [Fact]
        public void Default_python_requires_the_writable_user_environment_and_never_migrates_private_files()
        {
            string environment = Path.Combine(Local, "TiaMcp", "ecosystem-python");
            string python = Path.Combine(environment, "Scripts", "python.exe");
            Put(Path.Combine(Bundle, "bin-build", "ecosystem-python", "Scripts", "python.exe"), "private");
            Assert.Contains(environment, Assert.Throws<DataLocationIOException>(() => DataLocations.EcosystemPython(null!, Local)).Message);
            Assert.False(Directory.Exists(environment));
            Directory.CreateDirectory(Path.GetDirectoryName(environment)!); File.WriteAllText(environment, "blocked");
            Assert.Contains(environment, Assert.Throws<DataLocationIOException>(() => DataLocations.EcosystemPython(null!, Local)).Message);
            File.Delete(environment); Put(python, "user");
            Assert.Equal(python, DataLocations.EcosystemPython(null!, Local));
            Assert.Equal("user", File.ReadAllText(python));
            Assert.Equal("private", File.ReadAllText(Path.Combine(Bundle, "bin-build", "ecosystem-python", "Scripts", "python.exe")));
        }

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

        [Fact]
        public void Development_checkout_keeps_data_under_bin_build()
        {
            File.WriteAllText(Path.Combine(Bundle, "TiaPortalOpenness.slnx"), "<Solution />");
            var locations = Resolve();
            Assert.Equal(Path.Combine(Bundle, "bin-build/data"), locations.Root);
            Assert.True(Directory.Exists(locations.Root));
            Assert.False(Directory.Exists(Path.Combine(Bundle, "data")));
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
            Assert.Equal(Path.Combine(Temp, "TiaMcp", "logs"), locations.LogsDirectory);
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
                ["src/Updater/Program.cs"] = 1,
                ["src/Updater/Updater.cs"] = 1
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

        private static string OldLog(string directory, string purpose, int index)
        {
            string path = Path.Combine(directory, purpose + "-2147483647-20000101-000000-0000000-" + index.ToString("x32") + ".log");
            Put(path, "old log");
            File.SetLastWriteTimeUtc(path, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes(index));
            return path;
        }

        [Fact]
        public void Startup_retention_keeps_newest_32_per_purpose_and_prunes_both_roots()
        {
            var locations = Resolve();
            foreach (string logs in new[] { Path.Combine(Bundle, "data", "logs"), Path.Combine(Temp, "TiaMcp", "logs") })
                foreach (string key in new[] { "14sp1", "20", "21", "studio" })
                    foreach (string purpose in new[] { "TiaMcpServer", "TiaMcpServer.hmi-read", "TiaOpenness.crash" })
                        for (int index = 0; index < 40; index++) OldLog(Path.Combine(logs, key), purpose, index);
            locations.PruneLogs();
            foreach (string logs in new[] { Path.Combine(Bundle, "data", "logs"), Path.Combine(Temp, "TiaMcp", "logs") })
                foreach (string key in new[] { "14sp1", "20", "21", "studio" })
                    foreach (string purpose in new[] { "TiaMcpServer", "TiaMcpServer.hmi-read", "TiaOpenness.crash" })
                    {
                        var files = Directory.GetFiles(Path.Combine(logs, key), purpose + "-*.log");
                        Assert.Equal(32, files.Length);
                        Assert.All(files, path => Assert.InRange(Convert.ToInt32(Path.GetFileNameWithoutExtension(path).Split('-').Last(), 16), 8, 39));
                    }
        }

        [Fact]
        public void Retention_skips_locked_live_unrelated_and_nested_files()
        {
            var locations = Resolve();
            string directory = locations.LogDirectory("21");
            for (int index = 0; index < 40; index++) OldLog(directory, "TiaMcpServer", index);
            string locked = OldLog(directory, "TiaMcpServer", 100);
            string live = locations.LogFile("TiaMcpServer.log", "21"); Put(live, "live");
            string stale = OldLog(directory, "TiaMcpServer", 101);
            string reusedPid = stale.Replace("-2147483647-", "-" + System.Diagnostics.Process.GetCurrentProcess().Id + "-");
            File.Move(stale, reusedPid); File.SetLastWriteTimeUtc(reusedPid, new DateTime(1999, 1, 1));
            File.SetLastWriteTimeUtc(locked, new DateTime(1999, 1, 1)); File.SetLastWriteTimeUtc(live, new DateTime(1999, 1, 1));
            string unrelated = Path.Combine(directory, "TiaMcpServer.log"), unknown = OldLog(directory, "unrelated", 0);
            string malformed = Path.Combine(directory, "TiaMcpServer-2147483647-99999999-000000-0000000-" + new string('a', 32) + ".log");
            string nested = OldLog(Path.Combine(directory, "nested"), "TiaMcpServer", 0);
            string outside = OldLog(Temp, "TiaMcpServer", 0);
            Put(unrelated, "keep"); Put(malformed, "keep");
            using (var handle = new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete))
                locations.PruneLogs();
            Assert.True(File.Exists(locked)); Assert.True(File.Exists(live)); Assert.False(File.Exists(reusedPid));
            Assert.True(File.Exists(unrelated)); Assert.True(File.Exists(unknown)); Assert.True(File.Exists(malformed));
            Assert.True(File.Exists(nested)); Assert.True(File.Exists(outside));
            Assert.Equal(34, Directory.GetFiles(directory, "TiaMcpServer-*.log").Count(path => path != malformed));
            locations.PruneLogs(); Assert.False(File.Exists(locked)); Assert.True(File.Exists(live));
        }

        [Fact]
        public void Retention_failure_is_reported_once_and_does_not_interrupt_cleanup()
        {
            // A read-only file blocks deletion only on Windows; elsewhere the directory's permissions decide.
            if (Environment.OSVersion.Platform != PlatformID.Win32NT) return;
            var locations = Resolve();
            string directory = locations.LogDirectory("21");
            for (int index = 0; index < 40; index++) OldLog(directory, "TiaMcpServer", index);
            string denied = Directory.GetFiles(directory, "TiaMcpServer-*.log").OrderBy(File.GetLastWriteTimeUtc).First();
            var previous = Console.Error; var stderr = new StringWriter();
            File.SetAttributes(denied, FileAttributes.ReadOnly); Console.SetError(stderr);
            try { locations.PruneLogs(); locations.PruneLogs(); }
            finally { Console.SetError(previous); File.SetAttributes(denied, FileAttributes.Normal); }
            Assert.Single(stderr.ToString().Split('\n'), line => line.Contains("IO_FAILED"));
            Assert.Contains(denied, stderr.ToString()); Assert.True(File.Exists(denied));
            Assert.Equal(33, Directory.GetFiles(directory, "TiaMcpServer-*.log").Length);
        }

        [Fact]
        public void Explicit_audit_root_never_falls_back_but_implicit_readers_find_both_chains()
        {
            var locations = Resolve();
            string primary = Path.Combine(Bundle, "data", "logs", "audit"), fallback = Path.Combine(Local, "TiaMcp", "logs", "audit");
            Directory.CreateDirectory(fallback);
            Assert.Equal(new[] { primary, fallback }, locations.AuditReadRoots);
            Assert.Equal(primary, locations.WritableAuditDirectory);
            Directory.Delete(primary); File.WriteAllText(primary, "blocked");
            Assert.Equal(fallback, locations.WritableAuditDirectory);
            var configured = Resolve(locations.Root);
            Assert.Single(configured.AuditReadRoots);
            Assert.Contains("DIAGNOSTIC_WRITE_FAILED", Assert.Throws<IOException>(() => _ = configured.WritableAuditDirectory).Message);
        }

        [Fact]
        public void Failed_plain_log_writes_report_once_per_purpose_on_stderr_and_trace()
        {
            string blocked = Path.Combine(scratch, "blocked"); File.WriteAllText(blocked, "file");
            var locations = Resolve(blocked);
            string marker = Guid.NewGuid().ToString("N");
            var stderr = new StringWriter(); var trace = new StringWriter();
            var previous = Console.Error;
            using (var listener = new System.Diagnostics.TextWriterTraceListener(trace))
            {
                System.Diagnostics.Trace.Listeners.Add(listener); Console.SetError(stderr);
                try
                {
                    foreach (string purpose in new[] { "host-" + marker, "crash-" + marker })
                        for (int attempt = 0; attempt < 3; attempt++)
                            try { locations.AppendLog(purpose + ".log", "21", "message"); }
                            catch (IOException error) { DataLocations.ReportLogFailure(purpose, new IOException(marker + ": " + error.Message, error)); }
                    listener.Flush();
                }
                finally { Console.SetError(previous); System.Diagnostics.Trace.Listeners.Remove(listener); }
            }
            Assert.Equal(2, stderr.ToString().Split('\n').Count(line => line.Contains(marker)));
            Assert.Equal(2, trace.ToString().Split('\n').Count(line => line.Contains(marker)));
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
