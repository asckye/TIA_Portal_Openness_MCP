using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using TiaMcp.Engine.Tests.Shared;
using TiaMcp.Updater;
using Xunit;

namespace TiaMcp.Updater.Tests;

public sealed class DirectorySwitchTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Restart_restores_the_original_install_at_every_rename_boundary(bool holdData, bool afterRename)
    {
        // First observe the actual sequence on this filesystem, including the archive and its receipt.
        int count = 0;
        using (var completed = new Install())
        {
            completed.Prepare();
            using (completed.PinData(holdData))
            {
                UpdaterEngine.ActivateSwitch(completed.Root, (step, after) => { if (after) count = step; });
                Assert.Equal(holdData, File.Exists(Path.Combine(completed.Slot, "parts")));
            }
            UpdaterEngine.FinishSwitch(completed.Root, _ => { });
            completed.Restart();
            Assert.False(UpdaterEngine.HasPendingSwitch(completed.Root));
            Assert.Equal("new", completed.Read("runtime/engine.exe"));
            Assert.False(File.Exists(Path.Combine(completed.Root, "retired.txt")));
            Assert.Equal("user setting", completed.Read("data/settings"));
            Assert.Equal("site override", completed.Read("runtime/local.ini"));
            Assert.Equal("previous release", completed.Read(".previous/v1/version"));
            Assert.Equal("receipt", completed.Read(".previous/v1.update-receipt.json"));
        }
        Assert.True(count >= 5);
        for (int cut = 1; cut <= count; cut++)
        {
            using var install = new Install();
            var original = install.Inventory();
            install.Prepare();
            using (install.PinData(holdData))
                Assert.Throws<PowerLoss>(() => UpdaterEngine.ActivateSwitch(install.Root, (step, after) =>
                {
                    if (step != cut || after != afterRename || holdData != File.Exists(Path.Combine(install.Slot, "parts"))) return;
                    if (!holdData && step == 1 && after) Assert.False(Directory.Exists(install.Root));
                    throw new PowerLoss();
                }));
            install.Restart();
            Assert.Equal(original, install.Inventory());
            Assert.False(UpdaterEngine.HasPendingSwitch(install.Root));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Recovery_handles_a_missing_or_incomplete_current_and_can_be_run_again(bool recreatedDuringGap)
    {
        using var install = new Install();
        var original = install.Inventory();
        install.Prepare();
        Assert.Throws<PowerLoss>(() => UpdaterEngine.ActivateSwitch(install.Root, (step, after) =>
        {
            if (!after || step != (recreatedDuringGap ? 1 : 2)) return;
            if (recreatedDuringGap)
            {
                Directory.CreateDirectory(install.Root);
                File.WriteAllText(Path.Combine(install.Root, "unrelated.txt"), "preserve this directory");
            }
            else File.Delete(Path.Combine(install.Root, "manifest", "delivery.json"));
            throw new PowerLoss();
        }));
        install.Restart();
        install.Restart();
        Assert.Equal(original, install.Inventory());
        if (recreatedDuringGap)
        {
            string parked = Assert.Single(Directory.GetDirectories(Path.GetDirectoryName(install.Root)!, "install.incomplete-*"));
            Assert.Equal("preserve this directory", File.ReadAllText(Path.Combine(parked, "unrelated.txt")));
        }
    }

    [Fact]
    public void A_recreated_component_is_preserved_and_its_backup_is_restored_when_data_is_pinned()
    {
        using var install = new Install();
        var original = install.Inventory();
        install.Prepare();
        using (install.PinData(true))
            Assert.Throws<PowerLoss>(() => UpdaterEngine.ActivateSwitch(install.Root, (_, after) =>
            {
                string runtime = Path.Combine(install.Root, "runtime");
                if (!after || Directory.Exists(runtime)) return;
                Directory.CreateDirectory(runtime);
                File.WriteAllText(Path.Combine(runtime, "user-file"), "keep");
                throw new PowerLoss();
            }));
        install.Restart();
        Assert.Equal(original, install.Inventory());
        string parked = Assert.Single(Directory.GetDirectories(Path.GetDirectoryName(install.Root)!, "install.incomplete-*"));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(parked, "runtime", "user-file")));
    }

    [Fact]
    public void A_prepared_but_unstarted_switch_keeps_the_current_install()
    {
        using var install = new Install();
        var original = install.Inventory();
        install.Prepare();
        install.Restart();
        Assert.Equal(original, install.Inventory());
        Assert.False(UpdaterEngine.HasPendingSwitch(install.Root));
    }

    [WindowsFact]
    public void A_second_restart_finishes_an_interrupted_restore()
    {
        using var install = new Install();
        var original = install.Inventory();
        install.Prepare();
        UpdaterEngine.ActivateSwitch(install.Root);
        using (File.Open(Path.Combine(install.Root, "runtime/engine.exe"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var error = Record.Exception(() => UpdaterEngine.RecoverSwitch(install.Root, _ => { }));
            Assert.True(error is IOException or UnauthorizedAccessException, error?.ToString());
            Assert.True(UpdaterEngine.HasPendingSwitch(install.Root));
            Assert.False(Directory.Exists(Path.Combine(install.Root, ".previous")));
        }
        install.Restart();
        Assert.Equal(original, install.Inventory());
        Assert.False(UpdaterEngine.HasPendingSwitch(install.Root));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_failed_rename_or_verification_immediately_undoes_the_switch(bool blockRename)
    {
        using var install = new Install();
        if (blockRename) Directory.CreateDirectory(Path.Combine(install.Root, ".previous", "v1"));
        var original = install.Inventory();
        install.Prepare();
        var hashes = new Dictionary<string, string> { ["runtime/engine.exe"] = new string('0', 64) };
        var error = Record.Exception(() => UpdaterEngine.PublishPrepared(install.Root, hashes, new(), _ => { }));
        if (blockRename) Assert.IsType<IOException>(error);
        else Assert.IsType<InvalidDataException>(error);
        Assert.False(UpdaterEngine.HasPendingSwitch(install.Root));
        Assert.Equal(original, install.Inventory());
        if (blockRename) Assert.True(Directory.Exists(Path.Combine(install.Root, ".previous", "v1")));
    }

    [Fact]
    public void A_file_added_after_staging_returns_with_the_original_directory()
    {
        using var install = new Install();
        var expected = install.StagedHashes();
        var retired = new Dictionary<string, string> { ["retired.txt"] = Install.Hash(Path.Combine(install.Root, "retired.txt")) };
        install.Prepare();
        string added = Path.Combine(install.Root, "runtime", "user-added.ini");
        File.WriteAllText(added, "keep");
        var original = install.Inventory();
        var error = Assert.Throws<IOException>(() => UpdaterEngine.PublishPrepared(install.Root, expected, retired, _ => { }));
        Assert.EndsWith("runtime/user-added.ini", error.Message);
        Assert.Equal(original, install.Inventory());
    }

    [WindowsFact]
    public void Recovery_updater_starts_from_the_sibling_when_the_install_root_is_missing()
    {
        using var install = new Install();
        string repo = Fixture.FindRepositoryRoot();
        Fixture.BuildUpdater(repo, true);
        install.Prepare(Path.Combine(repo, "bin-build/updater-test/TiaMcp.Updater.exe"));
        Assert.Throws<PowerLoss>(() => UpdaterEngine.ActivateSwitch(install.Root, (step, after) =>
        {
            if (step == 1 && after) throw new PowerLoss();
        }));
        Assert.False(Directory.Exists(install.Root));
        string source = Path.GetDirectoryName(install.Root)!;
        const string package = "TIA_MCP_Delivery_v4.0.0_20261006";
        var assets = new[] { ".zip", ".sha256" }.Select(ext => new { name = package + ext, browser_download_url = "fake://release/" + package + ext });
        File.WriteAllText(Path.Combine(source, "latest.json"), JsonSerializer.Serialize(new { tag_name = "v4.0.0", assets }));
        var start = new ProcessStartInfo(Path.Combine(install.Slot, "recover", "TiaMcp.Updater.exe"))
        {
            WorkingDirectory = source, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var arg in new[] { "-InstallRoot", install.Root, "-Check" }) start.ArgumentList.Add(arg);
        start.Environment["TIA_MCP_UPDATER_TEST_SOURCE"] = source;
        string logs = Path.Combine(source, "logs", "updater");
        start.Environment["TIA_MCP_DATA_DIRECTORY"] = source;
        using var launcher = Process.Start(start);
        Assert.True(launcher.WaitForExit(TestDeadlines.HangMilliseconds));
        Assert.Equal(0, launcher.ExitCode);
        Assert.Contains("staged outside", launcher.StandardOutput.ReadToEnd());
        bool CompletedCheck()
        {
            if (!Directory.Exists(logs)) return false;
            return Directory.GetFiles(logs).Any(file =>
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                string log = reader.ReadToEnd();
                return log.Contains("previous install restored") && log.Contains("release asset");
            });
        }
        Assert.True(SpinWait.SpinUntil(CompletedCheck, TestDeadlines.Hang));
        Assert.False(UpdaterEngine.HasPendingSwitch(install.Root));
        Assert.Equal("old", install.Read("runtime/engine.exe"));
        Assert.Equal("user setting", install.Read("data/settings"));
    }

    private sealed class PowerLoss : IOException { }

    private sealed class Install : IDisposable
    {
        private readonly string parent = Directory.CreateTempSubdirectory("workbench-switch-").FullName;
        public string Root => Path.Combine(parent, "install");
        public string Slot => UpdaterEngine.SwitchDirectory(Root);
        private string Next => Path.Combine(parent, "stage");
        private string History => Path.Combine(parent, "snapshot");
        public Install()
        {
            Write(Root, "manifest/delivery.json", """{"release":"3.3.0","package":"fixture-v1"}""");
            Write(Root, "runtime/engine.exe", "old");
            Write(Root, "runtime/local.ini", "site override");
            Write(Root, "data/settings", "user setting");
            Write(Root, "retired.txt", "obsolete");
            Write(Root, ".previous/older/version", "older");
            Write(Next, "manifest/delivery.json", """{"release":"4.0.0","package":"fixture-v2"}""");
            Write(Next, "runtime/engine.exe", "new");
            Write(Next, "new-tool.exe", "new tool");
            Write(History, "version", "previous release");
            File.WriteAllText(History + ".update-receipt.json", "receipt");
        }
        public void Prepare(string image = null) => UpdaterEngine.PrepareSwitch(Root, Next, new HashSet<string> { "retired.txt" }, History, "v1", image);

        public IDisposable PinData(bool pin) => pin ? File.Open(Path.Combine(Root, "data/settings"), FileMode.Open, FileAccess.Read, FileShare.None) : null;
        public void Restart()
        {
            // The normal updater entry point must recover before it can validate this deliberately invalid version.
            var options = new UpdaterOptions { InstallRoot = Root, Version = "invalid" };
            var error = Assert.Throws<InvalidOperationException>(() => UpdaterEngine.Run(options, _ => { }));
            Assert.Contains("-Version", error.Message);
        }
        public static string Hash(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
        public Dictionary<string, string> StagedHashes() => Directory.GetFiles(Next, "*", SearchOption.AllDirectories)
            .ToDictionary(file => Path.GetRelativePath(Next, file).Replace('\\', '/'), Hash);
        public string Read(string path) => File.ReadAllText(Path.Combine(Root, path));
        public KeyValuePair<string, string>[] Inventory() => Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories)
            .Select(file => KeyValuePair.Create(Path.GetRelativePath(Root, file), File.ReadAllText(file))).OrderBy(pair => pair.Key).ToArray();
        private static void Write(string root, string relative, string text)
        {
            string path = Path.Combine(root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
        }
        public void Dispose() => Directory.Delete(parent, true);
    }
}
