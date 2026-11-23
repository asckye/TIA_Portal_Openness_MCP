using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TiaMcp.Engine.Tests.Shared;
using Xunit;

namespace TiaMcp.Updater.Tests;

public sealed class StagedReplacementTests
{
    [Theory]
    [InlineData(false, false)] [InlineData(false, true)]
    [InlineData(true, false)] [InlineData(true, true)]
    public void Failed_replacement_restores_all_bytes_at_each_file_boundary(bool holdData, bool afterCopy)
    {
        using var f = new Fixture();
        var options = SmallDelivery(f);
        var original = f.CurrentInventory();
        var sequence = new List<string>();
        options.BeforeDeployFile = sequence.Add;
        Assert.Equal(0, Run(options));
        Assert.True(sequence.Count >= 5);
        Assert.Equal("new", f.Text("TiaOpenness.exe"));
        Assert.False(f.Exists("runtime/v21/old.dll"));
        Assert.Equal("kept", f.Text("data/config/user.json"));
        Assert.Equal("user-runtime", f.Text("runtime/v21/user.dll"));
        string backup = Assert.Single(Directory.GetDirectories(Path.Combine(f.Root, ".previous")));
        Assert.Equal("old-launcher", File.ReadAllText(Path.Combine(backup, "TiaOpenness.exe")));
        Assert.Contains("TiaOpenness.exe", File.ReadAllText(backup + ".update-receipt.json"));
        Assert.Equal(0, f.Run(rollback: true));
        for (int index = 0; index < sequence.Count; index++)
        {
            int visited = 0;
            using (holdData ? File.OpenRead(Path.Combine(f.Root, "data/config/user.json")) : null)
            {
                options.BeforeDeployFile = path =>
                {
                    Assert.Equal(sequence[visited], path);
                    Assert.True(f.Exists("TiaOpenness.exe"));
                    Assert.Equal("kept", f.Text("data/config/user.json"));
                    if (visited++ == index + (afterCopy ? 1 : 0)) throw new IOException("boundary");
                };
                var error = Assert.Throws<IOException>(() => Run(options, message =>
                {
                    if (afterCopy && index == sequence.Count - 1 && message.Contains(" / DONE")) throw new IOException("boundary");
                }));
                Assert.Equal("boundary", error.Message);
            }
            Assert.Equal(original.OrderBy(p => p.Key), f.CurrentInventory().OrderBy(p => p.Key));
        }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void A_missing_required_file_or_wrong_version_is_rejected_before_backup(bool wrongVersion)
    {
        using var f = new Fixture();
        var original = f.Inventory();
        var options = SmallDelivery(f, wrongVersion ? "3.9.0" : "4.0.0", missing: !wrongVersion);
        for (int attempt = 0; attempt < 2; attempt++)
        {
            var error = Assert.Throws<InvalidOperationException>(() => Run(options));
            Assert.Contains(wrongVersion ? "selected release" : "missing", error.Message);
            Assert.False(Directory.Exists(Path.Combine(f.Root, ".previous")));
            Assert.Equal(original.OrderBy(p => p.Key), f.Inventory().OrderBy(p => p.Key));
        }
    }

    [Fact]
    public void New_user_files_in_a_runtime_directory_survive_a_failed_replacement_with_data_open()
    {
        using var f = new Fixture();
        var original = f.CurrentInventory();
        var options = SmallDelivery(f);
        using (File.OpenRead(Path.Combine(f.Root, "data/config/user.json")))
        {
            options.BeforeDeployFile = _ => { f.Write("runtime/local.ini", "user addition"); throw new IOException("interrupted"); };
            Assert.Throws<IOException>(() => Run(options));
        }
        Assert.Equal("user addition", f.Text("runtime/local.ini"));
        original["runtime/local.ini"] = f.Hash("runtime/local.ini");
        Assert.Equal(original.OrderBy(p => p.Key), f.CurrentInventory().OrderBy(p => p.Key));
    }

    [Fact]
    public void Verified_staging_uses_TEMP_and_an_interruption_there_leaves_the_install_untouched()
    {
        using var f = new Fixture(longPath: true);
        var original = f.Inventory();
        string staged = null;
        Assert.Throws<IOException>(() => Run(SmallDelivery(f), message =>
        {
            if (!Extracted(message, out var package)) return;
            staged = package;
            Assert.StartsWith(Path.GetTempPath(), Path.GetFullPath(package), StringComparison.OrdinalIgnoreCase);
            Assert.False(Path.GetFullPath(package).StartsWith(Directory.GetParent(f.Root)!.FullName, StringComparison.OrdinalIgnoreCase));
            Assert.False(Directory.Exists(Path.Combine(f.Root, ".update")));
            Assert.False(Directory.Exists(Path.Combine(f.Root, ".previous")));
            throw new IOException("staging interrupted");
        }));
        Assert.NotNull(staged);
        Assert.False(Directory.Exists(staged));
        Assert.Equal(original.OrderBy(p => p.Key), f.Inventory().OrderBy(p => p.Key));
    }

    [WindowsFact]
    public void A_failed_rollback_keeps_its_backup_and_can_be_retried_after_unlock()
    {
        using var f = new Fixture();
        var original = f.CurrentInventory();
        var options = SmallDelivery(f);
        Assert.Equal(0, Run(options));
        options.Rollback = true;
        using (File.Open(Path.Combine(f.Root, "TiaOpenness.exe"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.ThrowsAny<IOException>(() => Run(options));
            Assert.Single(Directory.GetDirectories(Path.Combine(f.Root, ".previous")));
            Assert.True(f.Exists("data/config/user.json"));
        }
        Assert.Equal(0, Run(options));
        Assert.Equal(original.OrderBy(p => p.Key), f.CurrentInventory().OrderBy(p => p.Key));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Damaged_staged_bytes_or_a_locked_install_file_leave_the_original_install(bool locked)
    {
        using var f = new Fixture();
        var original = f.CurrentInventory();
        var options = SmallDelivery(f);
        using (locked ? File.Open(Path.Combine(f.Root, "TiaOpenness.exe"), FileMode.Open, FileAccess.Read, FileShare.Read) : null)
        {
            var error = Record.Exception(() => Run(options, message =>
            {
                if (!locked && Extracted(message, out var package)) File.WriteAllText(Path.Combine(package, "TiaOpenness.exe"), "corrupted");
            }));
            if (locked)
            {
                Assert.IsType<InvalidOperationException>(error);
                Assert.Contains("rollback also failed", error.Message);
                Assert.Single(Directory.GetDirectories(Path.Combine(f.Root, ".previous")));
            }
            else
            {
                Assert.IsType<InvalidDataException>(error);
                Assert.Contains("Staged file verification failed", error.Message);
                Assert.False(Directory.Exists(Path.Combine(f.Root, ".previous")));
            }
        }
        if (locked) { options.Rollback = true; Assert.Equal(0, Run(options)); }
        Assert.Equal(original.OrderBy(p => p.Key), f.CurrentInventory().OrderBy(p => p.Key));
    }

    [Theory]
    [InlineData("inventory")] [InlineData("unexpected")] [InlineData("missing")]
    public void Archive_inventory_and_staged_file_set_are_verified_before_any_install_write(string damage)
    {
        using var f = new Fixture();
        var original = f.Inventory();
        var options = SmallDelivery(f, badInventory: damage == "inventory");
        var error = Record.Exception(() => Run(options, message =>
        {
            if (!Extracted(message, out var package)) return;
            if (damage == "unexpected") File.WriteAllText(Path.Combine(package, "runtime/v21/extra.dll"), "unverified");
            if (damage == "missing") File.Delete(Path.Combine(package, "runtime/v21/optional.dll"));
        }));
        if (damage == "unexpected") Assert.Contains("outside delivery set", Assert.IsType<InvalidOperationException>(error).Message);
        else Assert.Contains(damage == "missing" ? "missing files" : "inventory mismatch", Assert.IsType<InvalidDataException>(error).Message);
        Assert.False(Directory.Exists(Path.Combine(f.Root, ".previous")));
        Assert.Equal(original.OrderBy(p => p.Key), f.Inventory().OrderBy(p => p.Key));
    }

    [Fact]
    public void A_file_added_to_the_install_after_staging_stays_at_its_original_path()
    {
        using var f = new Fixture();
        var original = f.CurrentInventory();
        var options = SmallDelivery(f);
        Assert.Equal(0, Run(options, message =>
        {
            if (Extracted(message, out _)) f.Write("runtime/user-added.ini", "keep");
        }));
        Assert.Equal("keep", f.Text("runtime/user-added.ini"));
        options.Rollback = true;
        Assert.Equal(0, Run(options));
        original["runtime/user-added.ini"] = f.Hash("runtime/user-added.ini");
        Assert.Equal(original.OrderBy(p => p.Key), f.CurrentInventory().OrderBy(p => p.Key));
    }

    [WindowsFact]
    public void Installed_updater_runs_a_check_at_its_original_path_after_an_interrupted_update()
    {
        using var f = new Fixture();
        string repo = Fixture.FindRepositoryRoot();
        Fixture.BuildUpdater(repo, true);
        var image = Path.Combine(f.Root, "runtime/tools/TiaMcp.Updater.exe");
        var built = Path.Combine(repo, "bin-build/updater-test/TiaMcp.Updater.exe");
        File.Copy(built, image, true);
        File.Copy(built + ".config", image + ".config", true);
        var original = f.CurrentInventory();
        var options = SmallDelivery(f);
        options.BeforeDeployFile = path => { if (path == "runtime/v21/engine.dll") throw new IOException("interrupted"); };
        Assert.Throws<IOException>(() => Run(options));
        Assert.Equal(original.OrderBy(p => p.Key), f.CurrentInventory().OrderBy(p => p.Key));
        string source = Path.Combine(f.Root, "data");
        const string package = "TIA_MCP_Delivery_v4.0.0_20261006";
        var assets = new[] { ".zip", ".sha256" }.Select(ext => new { name = package + ext, browser_download_url = "fake://release/" + package + ext });
        File.WriteAllText(Path.Combine(source, "latest.json"), JsonSerializer.Serialize(new { tag_name = "v4.0.0", assets }));
        var start = new ProcessStartInfo(image)
        {
            WorkingDirectory = f.Root, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add("-Check");
        start.Environment["TIA_MCP_UPDATER_TEST_SOURCE"] = source;
        start.Environment["TIA_MCP_DATA_DIRECTORY"] = source;
        using var launcher = Process.Start(start);
        Assert.True(launcher.WaitForExit(TestDeadlines.HangMilliseconds));
        Assert.Equal(0, launcher.ExitCode);
        Assert.Contains("release asset", launcher.StandardOutput.ReadToEnd());
        Assert.Contains("release asset", File.ReadAllText(Assert.Single(Directory.GetFiles(Path.Combine(source, "logs", "updater")))));
        Assert.Equal("old-launcher", f.Text("TiaOpenness.exe"));
        Assert.Equal("kept", f.Text("data/config/user.json"));
    }

    private static bool Extracted(string message, out string package)
    {
        const string marker = "extracted package to: ";
        int index = message.IndexOf(marker, StringComparison.Ordinal);
        package = index < 0 ? "" : message.Substring(index + marker.Length);
        return index >= 0;
    }

    private static int Run(UpdaterOptions options, Action<string> observe = null) =>
        UpdaterEngine.Run(options, observe ?? (_ => { }), _ => Array.Empty<string>());

    // Use the real release and rules formats with a small ZIP for exhaustive failure injection.
    private static UpdaterOptions SmallDelivery(Fixture f, string release = "4.0.0", bool missing = false, bool badInventory = false)
    {
        var files = new Dictionary<string, string> {
            ["TiaOpenness.exe"] = "new", ["runtime/v21/engine.dll"] = "new-engine", ["runtime/tools/TiaMcp.Updater.exe"] = "new-updater",
            ["manifest/delivery.json"] = JsonSerializer.Serialize(new { release, package = "fixture-v2" }) };
        if (badInventory)
            files["manifest/release-file-hashes.json"] = JsonSerializer.Serialize(new { files = new Dictionary<string, string>
                { ["TiaOpenness.exe"] = new string('0', 64) } });
        const string rules = "scripts/operations/delivery-files.json";
        var required = files.Keys.Append(rules).ToArray();
        files["runtime/v21/optional.dll"] = "optional";
        files[rules] = JsonSerializer.Serialize(new { schemaVersion = 1, requiredFiles = required,
            include = new { files = required, prefixes = new[] { "runtime/v21/" } }, exclude = new { }, legacyCleanup = new { } });
        if (missing) files.Remove("TiaOpenness.exe");
        using var memory = new MemoryStream();
        using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, true))
            foreach (var pair in files)
            {
                using var writer = new StreamWriter(archive.CreateEntry(pair.Key).Open(), new UTF8Encoding(false));
                writer.Write(pair.Value);
            }
        var zip = memory.ToArray();
        var hash = Encoding.UTF8.GetBytes(Convert.ToHexString(SHA256.HashData(zip)) + "  fixture.zip");
        var options = f.Options();
        options.ReadBytesForTest = (url, _) => url.EndsWith("/bundle", StringComparison.Ordinal) ? zip : hash;
        return options;
    }
}
