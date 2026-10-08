using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TiaMcp.Updater;
using Xunit;

namespace TiaMcp.Updater.Tests
{
    public sealed class UpdaterTests
    {
        [Fact] public void Check_reports_release_without_changing_install() { using (var f = new Fixture()) { byte[] before = f.Read("TiaOpenness.exe"); Assert.Equal(0, f.Run(check: true)); Assert.Equal(before, f.Read("TiaOpenness.exe")); } }
        [Fact] public void Self_and_root_launcher_are_replaced_and_user_data_is_preserved() { using (var f = new Fixture()) { Assert.Equal(0, f.Run()); Assert.Equal("new-launcher", f.Text("TiaOpenness.exe")); Assert.Equal("new-updater", f.Text("runtime/tools/TiaMcp.Updater.exe")); Assert.Equal("kept", f.Text("data/config/user.json")); Assert.Equal("unknown", f.Text("notes.txt")); Assert.False(f.Exists("TiaMcp.Updater.exe")); Assert.False(f.Exists("TiaMcp.Updater.exe.config")); Assert.False(f.Exists("scripts/operations/Update-Engine.ps1")); } }
        [Fact] public void Upgrade_moves_engine_workers_and_removes_owned_legacy_entries()
        {
            using var f = new Fixture();
            Assert.Equal(0, f.Run());
            foreach (string key in new[] { "20", "21" })
            {
                Assert.False(f.Exists("runtime/v" + key + "/TiaMcp.Engine.V" + key + ".exe"));
                Assert.True(f.Exists("runtime/v" + key + "/TiaMcp.FoundationHost.exe"));
                Assert.True(f.Exists("runtime/v" + key + "/worker/tool-catalog.json"));
                Assert.Equal("new-engine" + key, f.Text("runtime/v" + key + "/worker/TiaMcp.Engine.V" + key + ".exe"));
            }
            Assert.Equal("kept", f.Text("data/config/user.json"));
            Assert.Equal(0, f.Run(rollback: true));
            Assert.Equal("old-engine21", f.Text("runtime/v21/TiaMcp.Engine.V21.exe"));
        }
        [Fact] public void Self_replacement_uses_a_staged_copy_outside_the_install() { using (var f = new Fixture()) { string source = Path.Combine(f.Root, "runtime/tools/TiaMcp.Updater.exe"); string staged = UpdaterFileStaging.CopyTo(source, Path.Combine(f.Root, "..", "staged-updater")); Assert.NotEqual(source, staged); Assert.Equal("old-updater", File.ReadAllText(staged)); Assert.Equal("old-config", File.ReadAllText(staged + ".config")); Assert.Equal(0, f.Run()); Assert.Equal("old-updater", File.ReadAllText(staged)); Assert.Equal("new-updater", f.Text("runtime/tools/TiaMcp.Updater.exe")); } }
        [WindowsFact] public void Built_updater_applies_a_local_package_and_rolls_it_back()
        {
            using (var f = new Fixture())
            {
                string repository = Fixture.FindRepositoryRoot();
                Fixture.BuildUpdater(repository, false);
                Fixture.BuildUpdater(repository, true);
                string oldUpdater = Path.Combine(repository, "bin-build/updater-test/TiaMcp.Updater.exe");
                string oldConfig = oldUpdater + ".config";
                string nextUpdater = Path.Combine(repository, "bin-build/updater/TiaMcp.Updater.exe");
                byte[] oldBytes = File.ReadAllBytes(oldUpdater), nextBytes = File.ReadAllBytes(nextUpdater);
                Directory.CreateDirectory(Path.Combine(f.Root, "runtime", "tools"));
                File.Copy(oldUpdater, Path.Combine(f.Root, "runtime/tools/TiaMcp.Updater.exe"), true);
                File.Copy(oldConfig, Path.Combine(f.Root, "runtime/tools/TiaMcp.Updater.exe.config"), true);

                string source = Path.Combine(f.Root, "fake-release");
                Directory.CreateDirectory(source);
                Fixture.WriteLocalPackage(source, nextUpdater, Path.Combine(repository, "bin-build/updater/TiaMcp.Updater.exe.config"));

                Fixture.RunBuiltUpdater(Path.Combine(f.Root, "runtime/tools/TiaMcp.Updater.exe"), f.Root, source, omitInstallRoot: true);
                Fixture.WaitForInstall(f.Root, "4.0.0", "new-launcher", nextBytes);
                Assert.Equal("kept", f.Text("data/config/user.json"));
                Assert.Equal("unknown", f.Text("notes.txt"));
                Assert.False(f.Exists("scripts/operations/Update-Engine.ps1"));

                Fixture.RunBuiltUpdater(Path.Combine(f.Root, "runtime/tools/TiaMcp.Updater.exe"), f.Root, source, "-Rollback");
                Fixture.WaitForInstall(f.Root, "3.3.0", "old-launcher", oldBytes);
                Assert.Equal("kept", f.Text("data/config/user.json"));
            }
        }
        [Fact] public void Checksum_mismatch_leaves_install_untouched() { using (var f = new Fixture()) { f.Source.BadChecksum = true; Assert.Throws<InvalidOperationException>(() => f.Run()); Assert.Equal("old-launcher", f.Text("TiaOpenness.exe")); } }
        [Fact] public void Api_failure_uses_release_page_fallback() { using (var f = new Fixture()) { f.Source.FailApi = true; Assert.Equal(0, f.Run(check: true, releasePageFallback: true)); Assert.True(f.Source.SawExpandedAssets); } }
        [Fact] public void Running_bundle_engine_is_listed_and_refused() { using (var f = new Fixture()) { Assert.Throws<InvalidOperationException>(() => f.Run(running: _ => new[] { "TiaMcp.Engine.V21.exe PID 4242" })); Assert.Equal("old-launcher", f.Text("TiaOpenness.exe")); } }
        [Fact] public void Source_checkout_is_refused_before_network() { using (var f = new Fixture(sourceCheckout: true)) { Assert.Throws<InvalidOperationException>(() => f.Run()); Assert.False(f.Source.SawRequest); } }
        [Fact] public void Long_install_path_over_260_characters_updates_successfully() { using (var f = new Fixture(longPath: true)) { Assert.True(f.Root.Length > 260); Assert.Equal(0, f.Run()); Assert.Equal("new-launcher", f.Text("TiaOpenness.exe")); } }
        [Fact] public void Temporary_path_beyond_windows_limit_is_refused_before_install_changes() { using (var f = new Fixture()) { var options = f.Options(); options.TemporaryDirectoryForTest = Path.Combine(Path.GetTempPath(), new string('x', 32768)); Assert.Throws<InvalidOperationException>(() => UpdaterEngine.Run(options, _ => { })); Assert.Equal("old-launcher", f.Text("TiaOpenness.exe")); Assert.False(f.Exists(".update/TIA_MCP_Delivery_v4.0.0_20261006.zip")); } }
        [Fact] public void Interrupted_replace_restores_backup_and_removes_new_files() { using (var f = new Fixture()) { var before = f.CurrentInventory(); var calls = 0; Assert.Throws<IOException>(() => f.Run(beforeDeployFile: _ => { if (++calls == 2) throw new IOException("synthetic interruption"); })); Assert.Equal(before.Keys.OrderBy(x => x), f.CurrentInventory().Keys.OrderBy(x => x)); foreach (string path in before.Keys) Assert.Equal(before[path], f.Hash(path)); } }
        [Fact] public void Explicit_rollback_restores_the_previous_release() { using (var f = new Fixture()) { Assert.Equal(0, f.Run()); Assert.Equal(0, f.Run(rollback: true)); Assert.Equal("3.3.0", f.Json("manifest/delivery.json").GetProperty("release").GetString()); Assert.Equal("old-launcher", f.Text("TiaOpenness.exe")); } }
        [Fact] public void New_package_legacy_cleanup_removes_only_hash_owned_script() { using (var f = new Fixture()) { f.Write("scripts/operations/Update-Engine.ps1", "user edit"); Assert.Equal(0, f.Run()); Assert.Equal("user edit", f.Text("scripts/operations/Update-Engine.ps1")); } }
        [Fact] public void New_package_legacy_cleanup_removes_hash_owned_root_updater_files() { using (var f = new Fixture()) { f.Write("TiaMcp.Updater.exe", "old-root-updater"); f.Write("TiaMcp.Updater.exe.config", "old-root-config"); var inventory = new Dictionary<string, string> { ["TiaMcp.Updater.exe"] = f.Hash("TiaMcp.Updater.exe"), ["TiaMcp.Updater.exe.config"] = f.Hash("TiaMcp.Updater.exe.config") }; f.Write("manifest/release-file-hashes.json", JsonSerializer.Serialize(new { files = inventory })); Assert.Equal(0, f.Run()); Assert.False(f.Exists("TiaMcp.Updater.exe")); Assert.False(f.Exists("TiaMcp.Updater.exe.config")); Assert.Equal("new-updater", f.Text("runtime/tools/TiaMcp.Updater.exe")); } }
        [Fact] public void Rollback_keeps_unknown_files_added_after_update() { using (var f = new Fixture()) { Assert.Equal(0, f.Run()); f.Write("user-added.txt", "keep"); Assert.Equal(0, f.Run(rollback: true)); Assert.Equal("keep", f.Text("user-added.txt")); } }
        [Fact] public void Wait_for_workbench_pid_times_out_after_30_seconds() { using (var f = new Fixture()) { var options = f.Options(); options.WaitForPid = 1212; options.ProcessExists = _ => true; options.WaitForPidTimeoutSeconds = 1; Assert.Throws<InvalidOperationException>(() => UpdaterEngine.Run(options, _ => { })); } }
        [Fact] public void Self_test_mode_keeps_traversal_and_version_checks_available() { Assert.True(UpdaterEngine.IsSafeRelativePath("runtime/v21/TiaMcp.Engine.V21.exe")); Assert.False(UpdaterEngine.IsSafeRelativePath("../outside")); Assert.Equal(1, UpdaterEngine.CompareVersions("4.0.0", "3.3.0")); }
        [WindowsFact] public void Legacy_33_package_preflight_refuses_v4_layout_before_mutating_install()
        {
            using (var f = new Fixture())
            {
                string repository = Fixture.FindRepositoryRoot();
                var gitStart = new ProcessStartInfo("git", "show v3.3.0:scripts/operations/Update-Engine.ps1")
                { WorkingDirectory = repository, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
                string legacyScript;
                using (Process git = Process.Start(gitStart))
                {
                    legacyScript = git.StandardOutput.ReadToEnd();
                    string error = git.StandardError.ReadToEnd(); git.WaitForExit();
                    Assert.True(git.ExitCode == 0, "Could not read the tagged updater source: " + error);
                }
                int checkStart = legacyScript.IndexOf("foreach ($must in ", StringComparison.Ordinal);
                int checkEnd = legacyScript.IndexOf("\n$newDelivery", checkStart, StringComparison.Ordinal);
                int backup = legacyScript.IndexOf("# ---------------------------------------------------------------- backup, then replace", StringComparison.Ordinal);
                Assert.True(checkStart >= 0 && checkEnd > checkStart && backup > checkEnd, "The legacy required-file check must precede backup/replacement.");
                string checkBlock = legacyScript.Substring(checkStart, checkEnd - checkStart).Trim();

                string package = Path.Combine(f.Root, "v4-package");
                foreach (string path in new[] { "manifest/delivery.json", "manifest/package-manifest.json", "TiaOpenness.exe", "runtime/tools/TiaMcp.Updater.exe", "runtime/tools/TiaMcp.Updater.exe.config", "runtime/v20/TiaMcp.Engine.V20.exe", "runtime/v21/TiaMcp.Engine.V21.exe" })
                {
                    string file = Path.Combine(package, path.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(file)); File.WriteAllText(file, "v4-package-file");
                }
                f.Write("legacy-install-marker.txt", "must remain unchanged");
                string harness = "$package = '" + package.Replace("'", "''") + "'\n"
                    + "function Fail([string]$text) { [Console]::Error.WriteLine($text); exit 41 }\n"
                    + checkBlock + "\nexit 0\n";
                string harnessPath = Path.Combine(f.Root, "legacy-preflight.ps1");
                File.WriteAllText(harnessPath, harness, new UTF8Encoding(true));
                var before = f.CurrentInventory();
                var powershell = new ProcessStartInfo("powershell.exe", "-NoProfile -ExecutionPolicy Bypass -File \"" + harnessPath + "\"")
                { WorkingDirectory = repository, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
                using (Process check = Process.Start(powershell))
                {
                    bool exited = check.WaitForExit(15000);
                    if (!exited) { try { check.Kill(); } catch { } }
                    string output = check.StandardOutput.ReadToEnd(), error = check.StandardError.ReadToEnd();
                    Assert.True(exited, "The extracted offline preflight did not finish.");
                    Assert.Equal(41, check.ExitCode);
                    Assert.Contains("package is incomplete", output + error, StringComparison.OrdinalIgnoreCase);
                }
                Assert.Equal(before.OrderBy(file => file.Key), f.CurrentInventory().OrderBy(file => file.Key));
                Assert.Equal("must remain unchanged", f.Text("legacy-install-marker.txt"));
            }
        }
        [Fact] public void Two_previous_backups_are_retained() { using (var f = new Fixture()) { string previous = Path.Combine(f.Root, ".previous"); Directory.CreateDirectory(Path.Combine(previous, "older1")); Directory.CreateDirectory(Path.Combine(previous, "older2")); Directory.SetLastWriteTimeUtc(Path.Combine(previous, "older1"), DateTime.UtcNow.AddDays(-4)); Directory.SetLastWriteTimeUtc(Path.Combine(previous, "older2"), DateTime.UtcNow.AddDays(-2)); Assert.Equal(0, f.Run()); Assert.Equal(2, Directory.GetDirectories(previous).Length); } }
        [Fact] public void Invalid_repository_is_rejected() { using (var f = new Fixture()) { var options = f.Options(); options.Repository = "owner/name?x=1"; Assert.Throws<InvalidOperationException>(() => UpdaterEngine.Run(options, _ => { })); } }
        [Fact] public void Non_release_archive_is_rejected_without_install_changes() { using (var f = new Fixture()) { f.Source.InvalidArchive = true; Assert.ThrowsAny<Exception>(() => f.Run()); Assert.Equal("old-launcher", f.Text("TiaOpenness.exe")); } }
        [Fact] public void Stale_recorded_runtime_file_is_removed() { using (var f = new Fixture()) { Assert.Equal(0, f.Run()); Assert.False(f.Exists("runtime/v21/old.dll")); } }
        [Fact] public void Unknown_runtime_file_is_preserved() { using (var f = new Fixture()) { f.Write("runtime/v21/user.dll", "custom"); Assert.Equal(0, f.Run()); Assert.Equal("custom", f.Text("runtime/v21/user.dll")); } }
    }

    internal sealed class Fixture : IDisposable
    {
        private readonly string _parent;
        public string Root { get; }
        public FakeReleaseSource Source { get; }

        public Fixture(bool sourceCheckout = false, bool longPath = false)
        {
            _parent = Path.Combine(Path.GetTempPath(), "p651-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(_parent);
            Root = _parent;
            if (longPath)
                foreach (int n in new[] { 48, 48, 48, 48, 48 }) { Root = Path.Combine(Root, new string('x', n)); Directory.CreateDirectory(Root); }
            Directory.CreateDirectory(Path.Combine(Root, "manifest")); Directory.CreateDirectory(Path.Combine(Root, "runtime/tools")); Directory.CreateDirectory(Path.Combine(Root, "runtime/v21")); Directory.CreateDirectory(Path.Combine(Root, "runtime/v20")); Directory.CreateDirectory(Path.Combine(Root, "scripts/operations")); Directory.CreateDirectory(Path.Combine(Root, "data/config"));
            Write("manifest/delivery.json", "{\"release\":\"3.3.0\",\"package\":\"TIA_MCP_Delivery_v3.3.0_20260101\",\"engineRelease\":\"3.3.0\"}");
            Write("manifest/package-manifest.json", "{}"); Write("TiaOpenness.exe", "old-launcher"); Write("runtime/tools/TiaMcp.Updater.exe", "old-updater"); Write("runtime/tools/TiaMcp.Updater.exe.config", "old-config");
            Write("runtime/v20/TiaMcp.Engine.V20.exe", "old-engine20"); Write("runtime/v21/TiaMcp.Engine.V21.exe", "old-engine21");
            Write("runtime/v21/old.dll", "owned-old-runtime"); Write("runtime/v21/user.dll", "user-runtime");
            Write("scripts/operations/Update-Engine.ps1", "old updater script"); Write("data/config/user.json", "kept"); Write("notes.txt", "unknown");
            var inventory = new Dictionary<string, string> { ["scripts/operations/Update-Engine.ps1"] = Hash("scripts/operations/Update-Engine.ps1"), ["runtime/v21/old.dll"] = Hash("runtime/v21/old.dll"), ["runtime/v20/TiaMcp.Engine.V20.exe"] = Hash("runtime/v20/TiaMcp.Engine.V20.exe"), ["runtime/v21/TiaMcp.Engine.V21.exe"] = Hash("runtime/v21/TiaMcp.Engine.V21.exe") };
            Write("manifest/release-file-hashes.json", JsonSerializer.Serialize(new { files = inventory }));
            if (sourceCheckout) { Write("Version.props", "<Project />"); Directory.CreateDirectory(Path.Combine(Root, "src/Studio/Gui")); Write("src/Studio/Gui/TiaOpenness.Gui.csproj", "<Project />"); }
            Source = new FakeReleaseSource(this);
        }

        public UpdaterOptions Options()
        {
            return new UpdaterOptions { InstallRoot = Root, Repository = "owner/repo", TimeoutSeconds = 5, ApiBaseUrl = Source.BaseUrl + "/api", ReleasePageBaseUrl = Source.BaseUrl + "/github", ReadTextForTest = Source.ReadText, ReadRedirectForTest = Source.ReadRedirect, ReadBytesForTest = Source.ReadBytes };
        }
        public int Run(bool check = false, bool rollback = false, bool force = false, bool releasePageFallback = false, Action<string> beforeDeployFile = null, Func<string, IList<string>> running = null)
        {
            if (releasePageFallback) Source.FailApi = true;
            var options = Options(); options.Check = check; options.Rollback = rollback; options.Force = force; options.BeforeDeployFile = beforeDeployFile;
            return UpdaterEngine.Run(options, _ => { }, running);
        }
        public byte[] Read(string relative) { return File.ReadAllBytes(Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar))); }
        public string Text(string relative) { return File.ReadAllText(Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar))); }
        public JsonElement Json(string relative) => JsonDocument.Parse(Read(relative)).RootElement.Clone();
        public void Write(string relative, string value) { string path = Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar)); Directory.CreateDirectory(Path.GetDirectoryName(path)); File.WriteAllText(path, value, new UTF8Encoding(false)); }
        public bool Exists(string relative) => File.Exists(Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar)));
        public string Hash(string relative) { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(Read(relative))).Replace("-", "").ToLowerInvariant(); }
        public Dictionary<string, string> Inventory() => Directory.GetFiles(Root, "*", SearchOption.AllDirectories).ToDictionary(p => p.Substring(Root.Length + 1).Replace('\\', '/'), p => Hash(p.Substring(Root.Length + 1).Replace('\\', '/')));
        public Dictionary<string, string> CurrentInventory() => Inventory().Where(p => !p.Key.StartsWith(".previous/", StringComparison.Ordinal) && !p.Key.StartsWith(".update/", StringComparison.Ordinal)).ToDictionary(p => p.Key, p => p.Value);
        public void Dispose() { Source.Dispose(); try { Directory.Delete(_parent, true); } catch { } }

        internal static string FindRepositoryRoot()
        {
            for (DirectoryInfo directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
                if (File.Exists(Path.Combine(directory.FullName, "src", "Updater", "TiaMcp.Updater.csproj"))) return directory.FullName;
            throw new DirectoryNotFoundException("Could not find the repository root from the test output directory.");
        }

        internal static void BuildUpdater(string repository, bool testSource)
        {
            string project = Path.Combine(repository, "src/Updater/TiaMcp.Updater.csproj");
            string arguments = "build \"" + project + "\" -c Release -f net48 -p:NuGetAudit=false" + (testSource ? " -p:UpdaterTestSource=true" : "") + " -v:q";
            var start = new ProcessStartInfo("dotnet", arguments) { WorkingDirectory = repository, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            using (Process process = Process.Start(start))
            {
                string output = process.StandardOutput.ReadToEnd(), error = process.StandardError.ReadToEnd();
                process.WaitForExit();
                Assert.True(process.ExitCode == 0, "Updater build failed: " + output + Environment.NewLine + error);
            }
        }

        internal static void AddDeliveryLayout(Action<string, string> add)
        {
            string rules = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "scripts/operations/delivery-files.json"));
            add("scripts/operations/delivery-files.json", rules);
            using var document = JsonDocument.Parse(rules);
            foreach (var item in document.RootElement.GetProperty("requiredFiles").EnumerateArray())
            {
                string path = item.GetString();
                if (path.StartsWith("runtime/v", StringComparison.Ordinal) && !path.EndsWith(".exe", StringComparison.Ordinal)) add(path, "fixture");
                else if (path.StartsWith("runtime/v", StringComparison.Ordinal) && path.Contains("FoundationHost")) add(path, "fixture-host");
                else if (path.Contains("PlcWorker")) add(path, "fixture-worker");
                else if (path.StartsWith("runtime/studio/", StringComparison.Ordinal)) add(path, "fixture-studio");
            }
        }

        internal static void WriteLocalPackage(string source, string updater, string updaterConfig)
        {
            const string package = "TIA_MCP_Delivery_v4.0.0_20261006";
            string zipPath = Path.Combine(source, package + ".zip");
            using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                AddText(archive, package + "/manifest/delivery.json", "{\"release\":\"4.0.0\",\"package\":\"TIA_MCP_Delivery_v4.0.0_20261006\",\"engineRelease\":\"4.0.0\"}");
                AddText(archive, package + "/manifest/package-manifest.json", "{}");
                AddText(archive, package + "/TiaOpenness.exe", "new-launcher");
                AddFile(archive, package + "/runtime/tools/TiaMcp.Updater.exe", updater);
                AddFile(archive, package + "/runtime/tools/TiaMcp.Updater.exe.config", updaterConfig);
                AddText(archive, package + "/runtime/v20/worker/TiaMcp.Engine.V20.exe", "new-engine20");
                AddText(archive, package + "/runtime/v21/worker/TiaMcp.Engine.V21.exe", "new-engine21");
                AddDeliveryLayout((path, content) => AddText(archive, package + "/" + path, content));
            }
            byte[] bytes = File.ReadAllBytes(zipPath);
            using (var sha = SHA256.Create()) File.WriteAllText(Path.Combine(source, package + ".sha256"), BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant() + "  " + package + ".zip\n", new UTF8Encoding(false));
            string metadata = System.Text.Json.JsonSerializer.Serialize(new
            {
                tag_name = "v4.0.0",
                published_at = "2026-10-06T00:00:00Z",
                assets = new[]
                {
                    new { name = package + ".zip", browser_download_url = "fake://release/" + package + ".zip" },
                    new { name = package + ".sha256", browser_download_url = "fake://release/" + package + ".sha256" }
                }
            });
            File.WriteAllText(Path.Combine(source, "latest.json"), metadata, new UTF8Encoding(false));
        }

        private static void AddText(ZipArchive archive, string name, string value)
        {
            using (var writer = new StreamWriter(archive.CreateEntry(name).Open(), new UTF8Encoding(false))) writer.Write(value);
        }

        private static void AddFile(ZipArchive archive, string name, string path)
        {
            using (Stream input = File.OpenRead(path)) using (Stream output = archive.CreateEntry(name).Open()) input.CopyTo(output);
        }

        internal static void RunBuiltUpdater(string executable, string installRoot, string source, string extraArguments = "", bool omitInstallRoot = false)
        {
            string arguments = (omitInstallRoot ? "" : "-InstallRoot \"" + installRoot + "\" ") + extraArguments;
            var start = new ProcessStartInfo(executable, arguments) { WorkingDirectory = installRoot, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            start.Environment["TIA_MCP_UPDATER_TEST_SOURCE"] = source;
            using (Process process = Process.Start(start))
            {
                if (!process.WaitForExit(30000)) { try { process.Kill(); } catch { } Assert.Fail("Updater launcher process did not exit after staging."); }
                string output = process.StandardOutput.ReadToEnd(), error = process.StandardError.ReadToEnd();
                Assert.Equal(0, process.ExitCode);
                Assert.Contains("staged outside", output + error, StringComparison.OrdinalIgnoreCase);
            }
        }

        internal static void WaitForInstall(string root, string release, string launcher, byte[] updater)
        {
            var deadline = DateTime.UtcNow.AddSeconds(30);
            Exception lastError = null;
            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    using (JsonDocument manifest = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, "manifest/delivery.json"))))
                    {
                        if (manifest.RootElement.GetProperty("release").GetString() == release
                            && File.ReadAllText(Path.Combine(root, "TiaOpenness.exe")) == launcher
                            && File.ReadAllBytes(Path.Combine(root, "runtime/tools/TiaMcp.Updater.exe")).SequenceEqual(updater)) return;
                    }
                }
                catch (Exception error) { lastError = error; }
                System.Threading.Thread.Sleep(200);
            }
            throw new TimeoutException("Built updater did not finish installing " + release + ".", lastError);
        }
    }

    internal sealed class WindowsFactAttribute : FactAttribute
    {
        public WindowsFactAttribute() { if (!OperatingSystem.IsWindows()) Skip = "The shipped updater targets Windows and .NET Framework 4.8."; }
    }

    internal sealed class FakeReleaseSource : IDisposable
    {
        private readonly byte[] _zip; private readonly string _hash;
        public string BaseUrl { get; } = "https://fixture.invalid"; public bool BadChecksum { get; set; } public bool FailApi { get; set; } public bool InvalidArchive { get; set; } public bool SawRequest { get; private set; } public bool SawExpandedAssets { get; private set; }
        public FakeReleaseSource(Fixture fixture)
        {
            _zip = MakeZip(); using (var sha = SHA256.Create()) _hash = BitConverter.ToString(sha.ComputeHash(_zip)).Replace("-", "").ToLowerInvariant();
        }
        private byte[] MakeZip()
        {
            using (var memory = new MemoryStream())
            {
                using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, true))
                {
                    Add(zip, "TIA_MCP_Delivery_v4.0.0_20261006/manifest/delivery.json", "{\"release\":\"4.0.0\",\"package\":\"TIA_MCP_Delivery_v4.0.0_20261006\",\"engineRelease\":\"4.0.0\"}");
                    Add(zip, "TIA_MCP_Delivery_v4.0.0_20261006/manifest/package-manifest.json", "{}");
                    Add(zip, "TIA_MCP_Delivery_v4.0.0_20261006/TiaOpenness.exe", "new-launcher"); Add(zip, "TIA_MCP_Delivery_v4.0.0_20261006/runtime/tools/TiaMcp.Updater.exe", "new-updater"); Add(zip, "TIA_MCP_Delivery_v4.0.0_20261006/runtime/tools/TiaMcp.Updater.exe.config", "new-config");
                    Add(zip, "TIA_MCP_Delivery_v4.0.0_20261006/runtime/v20/worker/TiaMcp.Engine.V20.exe", "new-engine20"); Add(zip, "TIA_MCP_Delivery_v4.0.0_20261006/runtime/v21/worker/TiaMcp.Engine.V21.exe", "new-engine21");
                    Fixture.AddDeliveryLayout((path, content) => Add(zip, "TIA_MCP_Delivery_v4.0.0_20261006/" + path, content));
                }
                return memory.ToArray();
            }
        }
        private static void Add(ZipArchive zip, string path, string value) { using (var writer = new StreamWriter(zip.CreateEntry(path).Open(), new UTF8Encoding(false))) writer.Write(value); }
        public string ReadText(string url, int timeoutSeconds)
        {
            SawRequest = true;
            if (url.Contains("/api/"))
            {
                if (FailApi) throw new IOException("synthetic API failure");
                return JsonSerializer.Serialize(new { tag_name = "v4.0.0", published_at = "2026-10-06T00:00:00Z", assets = new[] { new { name = "TIA_MCP_Delivery_v4.0.0_20261006.zip", browser_download_url = BaseUrl + "/asset/bundle" }, new { name = "TIA_MCP_Delivery_v4.0.0_20261006.sha256", browser_download_url = BaseUrl + "/asset/hash" } } });
            }
            if (url.Contains("/expanded_assets/"))
            {
                SawExpandedAssets = true;
                return "<a href=\"/releases/download/v4.0.0/TIA_MCP_Delivery_v4.0.0_20261006.zip\">zip</a><a href=\"/releases/download/v4.0.0/TIA_MCP_Delivery_v4.0.0_20261006.sha256\">sha</a>";
            }
            throw new IOException("unexpected text request: " + url);
        }
        public string ReadRedirect(string url, int timeoutSeconds) { SawRequest = true; return BaseUrl + "/releases/tag/v4.0.0"; }
        public byte[] ReadBytes(string url, int timeoutSeconds)
        {
            SawRequest = true;
            if (url.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || url.EndsWith("/bundle", StringComparison.OrdinalIgnoreCase)) return InvalidArchive ? Encoding.UTF8.GetBytes("not a zip") : _zip;
            if (url.EndsWith(".sha256", StringComparison.OrdinalIgnoreCase) || url.EndsWith("/hash", StringComparison.OrdinalIgnoreCase)) return Encoding.UTF8.GetBytes((BadChecksum ? new string('0', 64) : _hash) + "  TIA_MCP_Delivery_v4.0.0_20261006.zip\n");
            throw new IOException("unexpected binary request: " + url);
        }
        public void Dispose() { }
    }

}
