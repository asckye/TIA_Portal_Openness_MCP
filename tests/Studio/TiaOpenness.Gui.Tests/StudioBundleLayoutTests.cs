using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using TiaMcp.Versioning;
using TiaMcpConfigurator;
using TiaOpenness.Gui.Localization;
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

    private static void Bundle(string root, bool marker, bool installed, bool development)
    {
        Directory.CreateDirectory(root);
        if (marker) Put(root, "manifest/package-manifest.json");
        Put(root, "manifest/delivery.json", "{\"release\":\"3.2.0\",\"package\":\"fixture.zip\"}");
        Put(root, "scripts/operations/Update-Engine.ps1");
        foreach (var version in TiaVersionCatalog.Runnable)
        {
            if (installed) Put(root, "runtime/" + version.RuntimeDirectory + "/TiaMcpServer.exe");
            if (development && version.IsFullEngine)
                Put(root, "src/Engine/" + version.EngineOutputDirectory + "/Release/net48/TiaMcpServer.exe");
        }
    }

    private static void EqualLookup(Func<string?> before, Func<string?> after)
    {
        string? expected = null, actual = null;
        var oldError = Record.Exception(() => expected = before());
        var newError = Record.Exception(() => actual = after());
        Assert.Equal(oldError?.GetType(), newError?.GetType());
        Assert.Equal(oldError?.Message.Split('\n')[0], newError?.Message.Split('\n')[0]);
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("runtime/studio")]
    [InlineData("runtime/studio/bridge")]
    [InlineData("src/Studio/Gui/bin/Release/net10.0-windows")]
    [InlineData("src/Studio/Gui/bin/Debug/net10.0-windows")]
    [InlineData("src/Studio/Gui/bin/Release/net10.0-windows/bridge")]
    [InlineData("src/Studio/Gui/bin/Debug/net10.0-windows/bridge")]
    [InlineData("src/Studio/Bridge/bin/Release/net48")]
    [InlineData("src/Studio/Bridge/bin/Debug/net48")]
    [InlineData("custom/a/b")]
    [InlineData("custom/a/b/c/d/e/f/g/h/i/j/k/l")]
    public void Bundle_engine_and_update_lookups_match_originals(string anchor)
    {
        foreach (bool marker in new[] { false, true })
        foreach (string layout in new[] { "bundle", "repository", "worktree", "ci", "runtime-only", "repository/bin-build/staging" })
        {
            string root = At(scratch, marker + "/" + layout);
            Bundle(root, marker, layout != "ci", layout != "runtime-only");
            if (layout == "repository") Directory.CreateDirectory(At(root, ".git"));
            if (layout == "worktree") Put(root, ".git", "gitdir: elsewhere");
            if (layout.Contains("/")) Bundle(At(scratch, marker + "/repository"), true, true, true);
            string output = Directory.CreateDirectory(At(root, anchor)).FullName;
            foreach (string spelling in new[] { output, output.Replace('\\', '/'), output.ToUpperInvariant() })
            foreach (string suffix in new[] { "", "\\", "/" })
                EqualLookup(() => StudioLookupBaseline.FindBundleRoot(spelling + suffix), () => MainWindow.FindBundleRoot(spelling + suffix));
            foreach (string spelling in new[] { root, root.Replace('\\', '/'), root.ToUpperInvariant() })
            foreach (string suffix in new[] { "", "\\", "/", "\\.\\" })
            {
                string explicitRoot = spelling + suffix;
                foreach (var language in new[] { AppLanguage.Chinese, AppLanguage.English })
                    wpf.RunWithLanguage(language, () =>
                    {
                        foreach (string key in new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21", "22" })
                            EqualLookup(() => StudioLookupBaseline.Engine(explicitRoot, key), () => ConfigCore.Engine(explicitRoot, key));
                    });
                EqualLookup(() => StudioLookupBaseline.DeliveryField(explicitRoot, "release"), () => UpdateCheck.Installed(explicitRoot));
                EqualLookup(() => StudioLookupBaseline.DeliveryField(explicitRoot, "package"), () => UpdateCheck.InstalledPackage(explicitRoot));
                EqualLookup(() => StudioLookupBaseline.UpdaterPath(explicitRoot), () => UpdateCheck.UpdaterPath(explicitRoot));
            }
        }
    }

    [Theory]
    [InlineData("absent", false)]
    [InlineData("directory", true)]
    [InlineData("file", true)]
    public void Update_is_disabled_for_checkouts_and_worktrees(string kind, bool expected)
    {
        Directory.CreateDirectory(scratch);
        if (kind == "directory") Directory.CreateDirectory(At(scratch, ".git"));
        if (kind == "file") Put(scratch, ".git", "gitdir: elsewhere");
        foreach (string suffix in new[] { "", "\\", "/" })
        {
            Assert.Equal(expected, UpdateCheck.IsSourceRepository(scratch + suffix));
            Assert.Equal(kind == "directory", StudioLookupBaseline.IsSourceRepository(scratch + suffix));
        }
    }

    [Theory]
    [InlineData("installed", true, false)]
    [InlineData("development", false, true)]
    [InlineData("repository-runtime", true, true)]
    public void Configuration_files_match_original_engine_paths(string layout, bool installed, bool development)
    {
        string before = At(scratch, layout + "/before root 中文");
        string after = At(scratch, layout + "/after root 中文");
        Bundle(before, true, installed, development);
        Bundle(after, true, installed, development);
        wpf.Run(() =>
        {
            foreach (var release in TiaVersionCatalog.Runnable.Where(v => installed || v.IsFullEngine))
            foreach (string suffix in new[] { "", "\\", "/", "\\.\\" })
            foreach (bool remote in new[] { false, true })
            foreach (string client in new[] { "claude-code", "codex", "gemini", "qwen", "kimi", "codebuddy", "opencode", "qwen-agent", "cursor", "vscode" })
            {
                string oldEngine = StudioLookupBaseline.Engine(before + suffix, release.Key);
                string newEngine = ConfigCore.Engine(after + suffix, release.Key);
                string name = Guid.NewGuid().ToString("N");
                string oldFile = At(scratch, "configuration/" + name + ".old");
                string newFile = At(scratch, "configuration/" + name + ".new");
                foreach (var item in new[] { (File: oldFile, Engine: oldEngine), (File: newFile, Engine: newEngine) })
                {
                    // Both writers are unchanged production code; only Engine differs.
                    var profile = new ClientProfile(client, client, item.File, "");
                    ClientProfiles.Save(profile, remote, "127.0.0.1", 8123, "fixture-key", item.Engine, release.Key, @"C:\TIA 中文");
                }
                Assert.Equal(NormalizedBytes(oldFile, before), NormalizedBytes(newFile, after));
                string oldMerge = oldFile + ".merge", newMerge = newFile + ".merge";
                ConfigCore.MergeServer(oldMerge, "tia-portal", new Dictionary<string, object> { { "command", oldEngine } });
                ConfigCore.MergeServer(newMerge, "tia-portal", new Dictionary<string, object> { { "command", newEngine } });
                Assert.Equal(NormalizedBytes(oldMerge, before), NormalizedBytes(newMerge, after));
            }
        });
    }

    private static byte[] NormalizedBytes(string file, string root)
    {
        string text = Encoding.UTF8.GetString(File.ReadAllBytes(file));
        string escapedRoot = ConfigCore.Json().Serialize(root);
        text = text.Replace(escapedRoot.Substring(1, escapedRoot.Length - 2), "<ROOT>").Replace(root, "<ROOT>");
        return Encoding.UTF8.GetBytes(text);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("malformed")]
    [InlineData("null-fields")]
    public void Missing_and_invalid_resources_keep_results_and_errors(string kind)
    {
        Directory.CreateDirectory(scratch);
        if (kind == "malformed") Put(scratch, "manifest/delivery.json", "broken JSON");
        if (kind == "null-fields") Put(scratch, "manifest/delivery.json", "{\"release\":null,\"package\":null}");
        foreach (string root in new[] { scratch, scratch + "/", "", " ", "relative", null! })
        {
            EqualLookup(() => StudioLookupBaseline.FindBundleRoot(root), () => MainWindow.FindBundleRoot(root));
            EqualLookup(() => StudioLookupBaseline.Engine(root, "21"), () => ConfigCore.Engine(root, "21"));
            EqualLookup(() => StudioLookupBaseline.DeliveryField(root, "release"), () => UpdateCheck.Installed(root));
            EqualLookup(() => StudioLookupBaseline.DeliveryField(root, "package"), () => UpdateCheck.InstalledPackage(root));
            EqualLookup(() => StudioLookupBaseline.UpdaterPath(root), () => UpdateCheck.UpdaterPath(root));
        }
    }

    public void Dispose()
    {
        Assert.Equal(new DirectoryInfo(Path.GetTempPath()).FullName.TrimEnd(Path.DirectorySeparatorChar),
            new DirectoryInfo(scratch).Parent!.FullName.TrimEnd(Path.DirectorySeparatorChar));
        if (Directory.Exists(scratch)) Directory.Delete(scratch, true);
    }
}
