using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using TiaMcpConfigurator;
using TiaOpenness.Contracts.Models;
using TiaOpenness.Gui.Controls;
using TiaOpenness.Gui.ViewModels;
using TiaOpenness.Gui.Themes;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using TiaOpenness.Gui.Localization;
using Xunit;

namespace TiaOpenness.Gui.Tests;

/// <summary>
/// The catalogue holds both languages as one table of triples, so "a key exists in English but
/// not in Chinese" is impossible by construction. What is still possible - and what these tests
/// are for - is a duplicated key silently overwriting an earlier one, a blank translation, or a
/// Chinese entry whose {0} placeholders do not match the English one, which throws a
/// FormatException at the moment the operation it describes finishes.
/// </summary>
[Collection(WpfCollection.Name)]
public class StringsTests(WpfContext wpf)
{
    private static readonly Regex Placeholder = new(@"\{(\d+)[^}]*\}", RegexOptions.Compiled);

    public static TheoryData<string, string, string> Entries()
    {
        var data = new TheoryData<string, string, string>();
        foreach (var (key, en, zh) in Strings.Catalogue) data.Add(key, en, zh);
        return data;
    }

    [Fact]
    public void The_catalogue_is_not_empty()
    {
        Assert.NotEmpty(Strings.Catalogue);
    }

    [Fact]
    public void Workbench_copy_matches_the_handoff()
    {
        Assert.Equal("一页连接 TIA 与 AI", Strings.Chinese["Config.RemoteTitle"]);
        Assert.Equal("同一台电脑，一次完成", Strings.Chinese["Config.LocalTitle"]);
        Assert.Equal("Engine & MCP settings…", Strings.English["Shell.Settings"]);
        Assert.Equal("写操作审批", Strings.Chinese["Settings.Approval"]);
    }

    [Fact]
    public void No_key_is_declared_twice()
    {
        var duplicates = Strings.Catalogue
            .GroupBy(e => e.Key, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        Assert.Empty(duplicates);
    }

    [Fact]
    public void Both_languages_expose_exactly_the_same_keys()
    {
        Assert.Equal(
            Strings.English.Keys.OrderBy(k => k, StringComparer.Ordinal),
            Strings.Chinese.Keys.OrderBy(k => k, StringComparer.Ordinal));
    }

    [Theory]
    [MemberData(nameof(Entries))]
    public void Neither_translation_is_blank(string key, string en, string zh)
    {
        Assert.False(string.IsNullOrWhiteSpace(en), key + " has no English text");
        Assert.False(string.IsNullOrWhiteSpace(zh), key + " has no Chinese text");
    }

    /// <summary>
    /// A Chinese entry may reorder its placeholders - "{1} 中共 {0} 个程序块" is better Chinese
    /// than the English order - but it must use the same set, or formatting throws at runtime.
    /// </summary>
    [Theory]
    [MemberData(nameof(Entries))]
    public void Both_translations_use_the_same_placeholders(string key, string en, string zh)
    {
        Assert.True(IndexesIn(en).SequenceEqual(IndexesIn(zh)),
            key + ": English uses {" + string.Join("},{", IndexesIn(en)) +
            "} but Chinese uses {" + string.Join("},{", IndexesIn(zh)) + "}");
    }

    [Theory]
    [MemberData(nameof(Entries))]
    public void Every_entry_formats_without_throwing(string key, string en, string zh)
    {
        var arguments = Enumerable.Range(0, HighestIndex(en) + 1).Cast<object?>().ToArray();

        // The exception this guards against is FormatException from a stray brace.
        Assert.False(string.IsNullOrEmpty(string.Format(CultureInfo.InvariantCulture, en, arguments)), key);
        Assert.False(string.IsNullOrEmpty(string.Format(CultureInfo.InvariantCulture, zh, arguments)), key);
    }

    /// <summary>
    /// An entry that is identical in both languages is usually an untranslated string that was
    /// pasted twice. The exceptions are genuinely language-neutral: the product name, the file
    /// filters that carry glob patterns, and the labels of the language picker itself.
    /// </summary>
    [Fact]
    public void Chinese_entries_are_actually_translated()
    {
        string[] allowedToBeIdentical =
        [
            // A product name, a monogram, a version pill and two format frames: none of these is
            // prose, so translating them would be inventing a difference rather than removing one.
            "App.Title", "App.Monogram", "Badge.NoVersion", "Badge.Version",
            "Lang.English", "Lang.Chinese", "Status.Working", "Settings.English", "Settings.Chinese", "Settings.EngineVersion", "Settings.Releases", "Config.Local",
        ];

        var untranslated = Strings.Catalogue
            .Where(e => !allowedToBeIdentical.Contains(e.Key, StringComparer.Ordinal))
            .Where(e => string.Equals(e.En, e.Zh, StringComparison.Ordinal))
            .Select(e => e.Key)
            .ToList();

        Assert.Empty(untranslated);
    }

    /// <summary>
    /// Keys are typed by hand into XAML and matched there by a regex, so anything outside a
    /// plain dotted identifier - a stray space, a hyphen - would simply never be found.
    /// </summary>
    [Fact]
    public void Keys_are_dotted_identifiers()
    {
        var malformed = Strings.Catalogue
            .Select(e => e.Key)
            .Where(key => !Regex.IsMatch(key, @"^[A-Za-z][A-Za-z0-9]*(\.[A-Za-z][A-Za-z0-9]*)*$"))
            .ToList();

        Assert.Empty(malformed);
    }


    [Fact]
    public void English_catalogue_contains_no_Chinese_prose()
    {
        // The language picker labels each language in its own language.
        Assert.DoesNotContain(Strings.Catalogue, e => e.Key is not ("Lang.Chinese" or "Settings.Chinese") && Regex.IsMatch(e.En, @"[\u4e00-\u9fff]"));
    }

    [Theory]
    [InlineData(AppLanguage.English)]
    [InlineData(AppLanguage.Chinese)]
    public void Client_guidance_and_detection_switch_on_existing_profiles_and_bindings(AppLanguage initial)
    {
        wpf.RunWithLanguage(initial, () =>
        {
            var profiles = ClientProfiles.All();
            var codex = profiles.Single(p => p.Id == "codex");
            var text = new TextBlock();
            text.SetBinding(TextBlock.TextProperty, new Binding(nameof(ClientProfile.Tooltip)) { Source = codex });
            var unknown = new ClientProfile("fixture", "Fixture", Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "config.json"), "fixture hint");
            ClientProfiles.Detect(unknown);
            Assert.False(unknown.Detected);
            foreach (var language in new[] { initial, initial == AppLanguage.English ? AppLanguage.Chinese : AppLanguage.English, initial })
            {
                Loc.Current.Language = language;
                text.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.DataBind);
                Assert.Equal(codex.Tooltip, text.Text);
                Assert.Equal(language == AppLanguage.English
                    ? "After saving, restart the Codex desktop app / CLI and reopen your task to load MCP."
                    : "保存后重启 Codex 桌面应用 / CLI，重新打开任务以加载 MCP。", codex.Hint);
                Assert.Equal(language == AppLanguage.English ? "CLI · Not detected" : "CLI · 未检测到", unknown.Category);
                Assert.Equal(Loc.Current["Config.ClientNotFound"], unknown.Evidence);
                Assert.Contains(language == AppLanguage.English ? "\nWrite to: " : "\n写入：", codex.Tooltip);
                Assert.Contains(language == AppLanguage.English ? "Zhipu GLM" : "智谱 GLM", profiles.Single(p => p.Id == "zhipu").Hint);
                foreach (var profile in profiles)
                    Assert.Equal(language == AppLanguage.Chinese, Regex.IsMatch(profile.Hint, @"[\u4e00-\u9fff]"));
            }

            string file = Path.Combine(Path.GetTempPath(), "studio-detection-" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                File.WriteAllText(file, "{}");
                unknown.Path = file;
                ClientProfiles.Detect(unknown);
                Assert.True(unknown.Detected);
                foreach (var language in new[] { AppLanguage.English, AppLanguage.Chinese })
                {
                    Loc.Current.Language = language;
                    Assert.Equal((language == AppLanguage.English ? "Configuration file " : "配置文件 ") + file, unknown.Evidence);
                }
            }
            finally { File.Delete(file); }
        });
    }

    [Theory]
    [InlineData(AppLanguage.English)]
    [InlineData(AppLanguage.Chinese)]
    public void Configuration_exceptions_keep_types_localize_messages_and_preserve_files(AppLanguage language)
    {
        wpf.RunWithLanguage(language, () =>
        {
            string path = Path.Combine(Path.GetTempPath(), "studio-invalid-" + Guid.NewGuid().ToString("N") + ".json");
            string missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            void Check<T>(Action action, string key, params object[] args) where T : Exception
                => Assert.Equal(Loc.Current.T(key, args), Assert.Throws<T>(action).Message);
            Check<ArgumentException>(() => ConfigCore.Prefix("not-an-ip", 8765), "Config.InvalidEndpoint");
            Check<ArgumentException>(() => ConfigCore.ValidateKey("a\nb"), "Config.InvalidSecret");
            Check<FileNotFoundException>(() => ConfigCore.Engine(missing, "21"), "Config.EngineNotFound", "V21");
            Check<DirectoryNotFoundException>(() => ConfigCore.ValidateTia(missing, "21"), "Config.TiaApiNotFound", "V21", "Siemens.Engineering.Base.dll");
            Check<InvalidDataException>(() => ClientProfiles.StripJsonComments("{/*"), "Config.UnclosedJsonComment");
            foreach (var (input, key) in new[]
            {
                ("mcp_servers = {}", "Config.UnsupportedTomlTable"),
                ("value = \"\"\"unclosed", "Config.UnclosedTomlMultiline"),
                ("value = \"unclosed", "Config.UnclosedTomlString"),
            })
                Check<InvalidDataException>(() => ClientProfiles.MergeToml(input, "tia-portal-vm", true, "192.0.2.10", 8765, "secret", "engine", "21", "tia"), key);
            Check<InvalidOperationException>(() => UpdateCheck.ParseRelease("null", "1.0.0", "fixture"), "Config.InvalidReleaseJson");
            Check<InvalidOperationException>(() => UpdateCheck.ParseRelease("{\"tag_name\":\"invalid\"}", "1.0.0", "fixture"), "Config.InvalidReleaseTag", "invalid");
            try
            {
                foreach (string original in new[] { "null", "{\"mcpServers\":null}" })
                {
                    File.WriteAllText(path, original);
                    Check<InvalidDataException>(() => ConfigCore.MergeServer(path, "test", new()),
                        original == "null" ? "Config.InvalidClaudeJson" : "Config.InvalidServerMap", "mcpServers");
                    Assert.Equal(original, File.ReadAllText(path));
                }
                foreach (var profile in ClientProfiles.All().Where(p => p.Client != "codex"))
                {
                    profile.Path = path;
                    foreach (string original in new[] { "null", "{\"" + ClientProfiles.RootKey(profile) + "\":null}" })
                    {
                        File.WriteAllText(path, original);
                        byte[] before = File.ReadAllBytes(path);
                        Check<InvalidDataException>(() => ClientProfiles.Save(profile, true, "192.0.2.10", 8765, "secret", "engine", "21", "tia"),
                            original == "null" ? "Config.InvalidClientJson" : "Config.InvalidServerMap", ClientProfiles.RootKey(profile));
                        Assert.Equal(before, File.ReadAllBytes(path));
                    }
                }
            }
            finally { File.Delete(path); }
        });
    }

    [Theory]
    [InlineData(AppLanguage.English)]
    [InlineData(AppLanguage.Chinese)]
    public void Glass_results_retranslate_recorded_outcomes_without_changing_the_log(AppLanguage initial)
    {
        wpf.RunWithLanguage(initial, () =>
        {
            using var model = new MainViewModel(new FakeStudioClient(), new FakeDialogService());
            using var results = new GlassResults(model);
            model.Activity.AppendLocalized("Status.CompileResult", "Warning", 0, 2, "1.4");
            model.Activity.AppendLocalized("Log.InspectionHeader", "PLC_1");
            model.Activity.AppendRule("NAMING-001", 1);
            model.Activity.AppendLocalized("Status.InspectResult", 1, 5);
            model.Activity.AppendLocalized("Status.VcMapApplied", 3, 0, 2, 1);
            model.Activity.AppendLocalized("Status.VcSyncApplied", 2, 1, 3);
            string history = model.Activity.Log;
            foreach (var language in new[] { AppLanguage.English, AppLanguage.Chinese })
            {
                Loc.Current.Language = language;
                Assert.Equal(language == AppLanguage.English ? "Warnings" : "警告", results.CompileState);
                Assert.Equal(language == AppLanguage.English ? "Checked 5 blocks, found 1 issues." : "检查 5 个块，发现 1 个问题。", results.InspectionSummary);
                Assert.Contains(language == AppLanguage.English ? "Naming · 1" : "命名 · 1", results.Rules);
                Assert.Equal(Loc.Current.T("Status.VcMapApplied", 3, 0, 2, 1), results.MappingSummary);
                Assert.Equal(Loc.Current.T("Status.VcSyncApplied", 2, 1, 3), results.SyncSummary);
                Assert.Equal(history, model.Activity.Log);
                var converter = new GlassValueConverter();
                Assert.Equal(language == AppLanguage.English ? "Export · Source text" : "导出 · 源文本", converter.Convert(true, typeof(string), "export", CultureInfo.InvariantCulture));
                Assert.Equal(language == AppLanguage.English ? "Differs" : "有差异", converter.Convert(VcCompareState.Unequal, typeof(string), "compare", CultureInfo.InvariantCulture));
            }
        });
    }

    [Theory]
    [InlineData(AppLanguage.English)]
    [InlineData(AppLanguage.Chinese)]
    public void Client_guidance_dialog_renders_in_the_selected_language(AppLanguage language)
    {
        wpf.RunWithLanguage(language, () =>
        {
            var previous = ThemeManager.Current.Theme;
            ThemeManager.Current.Theme = AppTheme.Light;
            var window = new MainWindow(new MainViewModel(new FakeStudioClient(), new FakeDialogService()), false);
            try
            {
                window.ShowConfiguration(false);
                var page = window.Configuration!;
                var choices = (ListBox)page.FindName("ClientChoices");
                choices.SelectedItems.Clear();
                var profile = choices.Items.Cast<ClientProfile>().Single(p => p.Id == "qwen-agent");
                profile.Detected = false;
                profile.Evidence = Loc.Current["Config.ClientNotFound"];
                choices.SelectedItems.Add(profile);
                string instructions = (string)((TextBlock)page.FindName("ClientSelection")).ToolTip;
                if (language == AppLanguage.English) Assert.DoesNotMatch(@"[\u4e00-\u9fff]", instructions);
                var dialog = new GlassMessageBox(instructions, Loc.Current["Config.ClientInstructionsCaption"], MessageBoxButton.OK, MessageBoxImage.Information);
                try
                {
                    var card = (Border)dialog.Content;
                    card.Measure(new Size(720, double.PositiveInfinity));
                    card.Arrange(new Rect(new Point(), card.DesiredSize));
                    card.UpdateLayout();
                    DesktopCapture.Save(card, "client-guidance-" + language + "-Light");
                }
                finally { dialog.Close(); }
            }
            finally { window.Close(); ThemeManager.Current.Theme = previous; }
        });
    }

    private static IReadOnlyList<int> IndexesIn(string format)
        => Placeholder.Matches(format)
            .Select(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture))
            .Distinct()
            .OrderBy(i => i)
            .ToList();

    private static int HighestIndex(string format)
    {
        var indexes = IndexesIn(format);
        return indexes.Count == 0 ? -1 : indexes[^1];
    }
}
