using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using TiaMcp.Versioning;
using TiaMcpConfigurator;
using TiaOpenness.Shared;
using Xunit;

namespace TiaOpenness.Gui.Tests;

[Collection(WpfCollection.Name)]
public sealed class StudioBundleLayoutTests(WpfContext wpf) : IDisposable
{
    private readonly string scratch = Path.Combine(Path.GetTempPath(), "studio gui 中文 " + Guid.NewGuid().ToString("N"));
    private static string At(string root, string relative) => Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
    private static void Put(string root, string relative, string content = "fixture")
    {
        string path = At(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }
    private void Bundle()
    {
        Put(scratch, "manifest/package-manifest.json", "{}");
        Put(scratch, "manifest/delivery.json", "{\"release\":\"4.0.0\",\"package\":\"fixture.zip\"}");
        Put(scratch, "runtime/tools/TiaMcp.Updater.exe");
        Put(scratch, "runtime/tools/TiaMcp.Updater.exe.config");
        foreach (var release in TiaVersionCatalog.Runnable)
            Put(scratch, "runtime/v" + release.Key + "/" + BundleLayout.GetProduct(release.Key).Executable);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    [InlineData(4)] [InlineData(5)] [InlineData(6)] [InlineData(7)]
    public void Gui_root_resolution_uses_only_the_selected_inputs(int inputs)
    {
        string anchor = At(scratch, "anchor"), cli = At(scratch, "CLI 中文"), environment = At(scratch, "environment");
        Directory.CreateDirectory(anchor);
        if ((inputs & 1) != 0) Put(anchor, "manifest/package-manifest.json");
        if ((inputs & 2) != 0) Put(environment, "manifest/package-manifest.json");
        if ((inputs & 4) != 0) Put(cli, "manifest/package-manifest.json");
        string? expected = (inputs & 4) != 0 ? cli : (inputs & 2) != 0 ? environment : (inputs & 1) != 0 ? anchor : null;
        Assert.Equal(expected, BundleLayout.ResolveWorkbenchRoot(anchor, (inputs & 4) != 0 ? cli : null!, (inputs & 2) != 0 ? environment : null!));
        Assert.Throws<BundleResourceUnavailableException>(() => BundleLayout.ResolveWorkbenchRoot(anchor, At(scratch, "bad"), environment));
        Assert.Throws<BundleResourceUnavailableException>(() => BundleLayout.ResolveWorkbenchRoot(anchor, null!, At(scratch, "bad")));
    }

    [Theory]
    [InlineData("Release")] [InlineData("Debug")]
    public void Gui_engine_selection_uses_exact_product_and_layout_without_fallback(string configuration)
    {
        Bundle();
        string gui = At(scratch, "src/Studio/Gui/bin/" + configuration + "/net10.0-windows");
        Directory.CreateDirectory(gui);
        foreach (var release in TiaVersionCatalog.Runnable)
        {
            string installed = At(scratch, "runtime/v" + release.Key + "/" + BundleLayout.GetProduct(release.Key).Executable);
            Assert.Equal(installed, ConfigCore.Engine(scratch, release.Key, scratch));
            string source = At(scratch, "src/FoundationHost/bin/" + configuration + "/net10.0/" + BundleLayout.GetProduct(release.Key).Executable);
            Assert.Throws<FileNotFoundException>(() => ConfigCore.Engine(scratch, release.Key, gui));
            Put(scratch, Path.GetRelativePath(scratch, source));
            Assert.Equal(source, ConfigCore.Engine(scratch, release.Key, gui));
            File.Delete(installed);
            var missing = Assert.Throws<FileNotFoundException>(() => ConfigCore.Engine(scratch, release.Key, scratch));
            Assert.Equal(installed, missing.FileName);
            File.Delete(source);
        }
        Assert.Throws<BundleResourceUnavailableException>(() => ConfigCore.Engine(At(scratch, "bad"), "21", gui));
        Assert.Throws<ArgumentException>(() => ConfigCore.Engine("relative", "21", gui));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Every_client_and_release_preserves_server_url_auth_and_schema_during_migration(bool atomicWriter)
    {
        Bundle();
        wpf.Run(() =>
        {
            foreach (var profile in ClientProfiles.All())
            foreach (var release in TiaVersionCatalog.Runnable)
            {
                string engine = ConfigCore.Engine(scratch, release.Key, scratch);
                string oldEngine = At(scratch, "runtime/v" + release.Key + "/" + "TiaMcp" + "Server.exe");
                string path = At(scratch, "clients/" + profile.Id + "-" + release.Key + (profile.Client == "codex" ? ".toml" : ".json"));
                var target = new ClientProfile(profile.Id, profile.Name, path, "", profile.Client);
                var remote = ClientProfiles.Entry(target, true, "127.0.0.1", 8123, "fixture-key", null!, release.Key, null!);
                var local = ClientProfiles.Entry(target, false, null!, 0, null!, engine, release.Key, "TIA 中文");
                Assert.Equal("tia-portal", ClientProfiles.ServerName(target, false));
                Assert.Equal("tia-portal-vm", ClientProfiles.ServerName(target, true));
                Assert.Equal("http://127.0.0.1:8123/mcp", remote[ClientProfiles.UrlKey(target)]);
                Assert.Equal("Bearer fixture-key", ((Dictionary<string, object>)remote["headers"])["Authorization"]);
                string[] arguments = target.Client == "opencode" ? ((string[])local["command"]).Skip(1).ToArray() : (string[])local["args"];
                Assert.Equal(new[] { "--bundle-root", scratch, BundleLayout.GetProduct(release.Key).VersionOption, release.Key, "--tia-portal-location", "TIA 中文" }, arguments);
                Assert.Equal(engine, target.Client == "opencode" ? ((string[])local["command"])[0] : local["command"]);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                string original;
                if (target.Client == "codex")
                {
                    original = "# user settings\r\nmodel = \"keep\"\r\n[mcp_servers.tia-portal]\r\ncommand = " + ConfigCore.Json().Serialize(oldEngine)
                        + "\r\nargs = [\"--tia-major-version\",\"21\"]\r\nstartup_timeout_sec = 777\r\n[mcp_servers.tia-portal.env]\r\nTOKEN = \"keep-auth\"\r\n"
                        + "[mcp_servers.tia-portal-vm]\r\nurl = \"http://127.0.0.1:8123/mcp\"\r\nhttp_headers = { Authorization = \"Bearer fixture-key\" }\r\n[mcp_servers.other]\r\ncommand = \"other.exe\"\r\n";
                }
                else
                {
                    var legacy = new Dictionary<string, object>(local);
                    legacy["command"] = target.Client == "opencode" ? new[] { oldEngine, "--old" } : (object)oldEngine;
                    legacy["env"] = new Dictionary<string, object> { { "TOKEN", "keep-auth" } };
                    original = ConfigCore.Json().Serialize(new Dictionary<string, object> { { "keep", true }, { ClientProfiles.RootKey(target),
                        new Dictionary<string, object> { { "tia-portal", legacy }, { "tia-portal-vm", remote }, { "other", new { command = "other.exe" } } } } });
                }
                File.WriteAllText(path, original, new UTF8Encoding(true));
                byte[] before = File.ReadAllBytes(path);
                var change = ClientProfiles.PrepareSave(target, false, null!, 0, null!, engine, release.Key, "TIA 中文");
                Assert.True(change.RequiresMigration);
                Assert.Equal(before, File.ReadAllBytes(path));
                Assert.Throws<InvalidOperationException>(() => change.Apply(false));
                Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + ".bak_*"));
                if (atomicWriter) change.Apply(true);
                else change.Apply(true, File.WriteAllText); // exercise every schema even where Windows denies File.Replace
                Assert.Equal(before, File.ReadAllBytes(change.BackupPath));
                Assert.Matches(@"\.bak_\d{8}T\d{13}Z$", change.BackupPath);
                string after = File.ReadAllText(path);
                Assert.Contains("keep-auth", after);
                Assert.Contains("other.exe", after);
                if (target.Client == "codex")
                {
                    Assert.StartsWith("# user settings\r\nmodel = \"keep\"\r\n", after);
                    Assert.Contains("startup_timeout_sec = 777\r\n", after);
                    Assert.Contains("url = \"http://127.0.0.1:8123/mcp\"\r\nhttp_headers = { Authorization = \"Bearer fixture-key\" }", after);
                    Assert.Contains("command = " + ConfigCore.Json().Serialize(engine), after);
                }
                else
                {
                    var document = ConfigCore.Json().Deserialize<Dictionary<string, object>>(after);
                    var servers = (Dictionary<string, object>)document[ClientProfiles.RootKey(target)];
                    Assert.Equal(ConfigCore.Json().Serialize(remote), ConfigCore.Json().Serialize(servers["tia-portal-vm"]));
                    var updated = (Dictionary<string, object>)servers["tia-portal"];
                    Assert.Equal(ConfigCore.Json().Serialize(local["command"]), ConfigCore.Json().Serialize(updated["command"]));
                    if (target.Client != "opencode") Assert.Equal(ConfigCore.Json().Serialize(arguments), ConfigCore.Json().Serialize(updated["args"]));
                }
            }
        });
    }

    [Fact]
    public void Bridge_start_uses_the_explicit_bundle_before_launching_a_child()
    {
        Bundle();
        using var client = new TiaOpenness.Client.BridgeClient(new[] { "--bundle-root", scratch });
        // Client links its own BundleLayout; exception identity differs across assemblies.
        var error = Assert.ThrowsAny<IOException>(() => client.Start(forceMock: true));
        Assert.Equal(typeof(BundleResourceUnavailableException).FullName, error.GetType().FullName);
        Assert.Contains(At(scratch, "runtime/studio/bridge/TiaOpenness.Bridge.exe"), error.Message);
        Assert.False(client.IsRunning);
    }

    [Fact]
    public void Migration_refuses_missing_target_or_stale_preview_and_rolls_back_write_failure()
    {
        Bundle();
        wpf.Run(() =>
        {
            var profile = new ClientProfile("claude-code", "Claude Code", At(scratch, "client.json"), "");
            const string original = "{\"mcpServers\":{\"tia-portal\":{\"command\":\"TiaMcp" + "Server.exe\"}}}";
            File.WriteAllText(profile.Path, original);
            string engine = ConfigCore.Engine(scratch, "21", scratch);
            var change = ClientProfiles.PrepareSave(profile, false, null!, 0, null!, engine, "21", "TIA");
            Assert.Throws<IOException>(() => change.Apply(true, (path, value) =>
            {
                Assert.Equal(original, File.ReadAllText(change.BackupPath));
                File.WriteAllText(path, "partial");
                throw new IOException("injected after write");
            }));
            Assert.Equal(original, File.ReadAllText(profile.Path));
            var stale = ClientProfiles.PrepareSave(profile, false, null!, 0, null!, engine, "21", "TIA");
            File.WriteAllText(profile.Path, "new user content");
            Assert.Throws<IOException>(() => stale.Apply(true));
            Assert.Null(stale.BackupPath);
            Assert.Equal("new user content", File.ReadAllText(profile.Path));
            File.Delete(engine);
            Assert.Throws<FileNotFoundException>(() => change.Apply(true));
            Assert.Throws<FileNotFoundException>(() => ClientProfiles.PrepareSave(profile, false, null!, 0, null!, engine, "21", "TIA"));
            Assert.Equal("new user content", File.ReadAllText(profile.Path));
        });
    }

    [Theory]
    [InlineData("file")] [InlineData("directory")] [InlineData("source-archive")] [InlineData("ancestor-worktree")]
    public void Update_launch_refuses_worktrees_and_source_checkouts_at_the_boundary(string kind)
    {
        Bundle();
        if (kind == "file") Put(scratch, ".git", "gitdir: elsewhere");
        if (kind == "directory") Directory.CreateDirectory(At(scratch, ".git"));
        if (kind == "source-archive")
        {
            Put(scratch, "CLAUDE.md"); Put(scratch, "Version.props"); Directory.CreateDirectory(At(scratch, "src"));
        }
        string root = scratch;
        if (kind == "ancestor-worktree")
        {
            Put(scratch, ".git", "gitdir: elsewhere");
            root = At(scratch, "staging");
            Put(root, "manifest/package-manifest.json");
        }
        Assert.True(UpdateCheck.IsSourceRepository(root));
        Assert.Throws<InvalidOperationException>(() => UpdateCheck.Launch(root, 123));
    }

    [Fact]
    public void Update_resources_stay_under_selected_root_and_read_only_install_is_readable()
    {
        Bundle();
        File.SetAttributes(At(scratch, "manifest/package-manifest.json"), FileAttributes.ReadOnly);
        Assert.Equal("4.0.0", UpdateCheck.Installed(scratch));
        Assert.Equal("fixture.zip", UpdateCheck.InstalledPackage(scratch));
        Assert.Equal(At(scratch, "runtime/tools/TiaMcp.Updater.exe"), UpdateCheck.UpdaterPath(scratch));
        Assert.False(UpdateCheck.IsSourceRepository(scratch));
        Assert.True(UpdateCheck.Launch(scratch, 123).UseShellExecute);
        string nested = At(scratch, "staging");
        Put(nested, "manifest/package-manifest.json");
        Assert.Throws<BundleResourceUnavailableException>(() => UpdateCheck.Installed(nested));
        Assert.Throws<BundleResourceUnavailableException>(() => UpdateCheck.UpdaterPath(nested));
        Assert.Throws<BundleResourceUnavailableException>(() => UpdateCheck.Installed(At(scratch, "absent")));
    }

    public void Dispose()
    {
        if (!Directory.Exists(scratch)) return;
        foreach (string path in Directory.GetFiles(scratch, "*", SearchOption.AllDirectories)) File.SetAttributes(path, FileAttributes.Normal);
        Directory.Delete(scratch, true);
    }
}
