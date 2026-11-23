extern alias PlcRendering;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using TiaMcpConfigurator;
using TiaOpenness.Core.Environment;
using TiaOpenness.Gui.Localization;
using TiaOpenness.Gui.Services;
using TiaOpenness.Gui.Controls;
using TiaOpenness.Gui.Themes;
using TiaOpenness.Shared;
using Xunit;

namespace TiaOpenness.Gui.Tests;

[Collection(WpfCollection.Name)]
public sealed partial class RemoteConfigurationTests(WpfContext wpf) : IDisposable
{
    private readonly string root = Path.Combine(FindRoot(), "bin-build", "relay-config-tests", Guid.NewGuid().ToString("N"));
    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "Version.props"))) directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("worktree");
    }
    private const string Secret = "synthetic-http-access-key";

    private MainWindow ConfigurationWindow(List<ClientProfile>? catalogue = null, bool running = false, string? install = null, string log = "")
    {
        var clients = catalogue ?? new List<ClientProfile> {
            new("vscode", "VS Code", Path.Combine(root, "editor", "mcp.json"), "Editor settings", kind: "IDE") { Detected = true },
            new("opencode", "OpenCode", Path.Combine(root, "opencode.json"), "CLI settings") { Detected = true },
            new("codex", "Codex", Path.Combine(root, "config.toml"), "CLI settings")
        };
        _ = Engine;
        var preview = new ConfigurationPreview("21", install ?? @"C:\fixture\Portal V21", "127.0.0.1", false, running, true, Secret, log, clients);
        var window = new MainWindow(ProjectPageTestSupport.Model(connected: false, release: "21"), false, preview: preview);
        window.ShowConfiguration(false, root);
        return window;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Installation_scan_runs_off_dispatcher_and_cancels_between_probes(bool cancel)
    {
        wpf.Run(() =>
        {
            using var stop = new System.Threading.CancellationTokenSource();
            var dispatcher = Dispatcher.CurrentDispatcher;
            var cards = ClientProfiles.Catalogue();
            int clients = 0, releases = 0;
            var task = ConfigurationView.ScanInstallations(cards, (_, _) =>
            {
                Assert.False(dispatcher.CheckAccess()); clients++;
                if (cancel) stop.Cancel();
            }, key =>
            {
                Assert.False(dispatcher.CheckAccess()); releases++;
                return new(key, "fixture");
            }, stop.Token);
            if (cancel)
            {
                Assert.ThrowsAny<OperationCanceledException>(() => WpfContext.Complete(task));
                Assert.Equal(1, clients); Assert.Equal(0, releases);
            }
            else
            {
                WpfContext.Complete(task);
                Assert.Equal(cards.Count, clients);
                Assert.Equal(8, releases);
                Assert.Equal("21", task.Result["21"].Key);
            }
        });
    }

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, button));

    private static void Navigate(Button button)
    {
        var command = Assert.IsType<RoutedUICommand>(button.Command);
        Assert.Same(WorkbenchCommands.Navigate, command);
        Assert.True(command.CanExecute(button.CommandParameter, button));
        command.Execute(button.CommandParameter, button);
        WpfContext.Drain();
    }

    [Fact]
    public void Mcp_and_clients_share_the_release_lock_transport_and_current_preview() => wpf.Run(() =>
    {
        var window = ConfigurationWindow();
        try
        {
            var mcp = window.Configuration!;
            var host = WorkbenchRenderTests.CreateHost(window);
            void CheckCounts()
            {
                var roster = PlcRendering::TiaOpenness.Shared.ToolUsageCatalog.ProfileEntries(mcp.SelectedReleaseKey);
                Assert.Equal(roster.Count + " tools", mcp.ToolCount.Text);
                Assert.Equal(Loc.Current.T("Config.ToolCount", roster.Count), mcp.ToolCount.ToolTip);
                int lite = roster.Count(row => row!["profiles"]!.AsArray().Any(profile => (string?)profile == "lite"));
                Assert.Equal(lite + " lite", mcp.LiteCount.Text);
                Assert.Equal(Loc.Current.T("Config.LiteCount", lite), mcp.LiteCount.ToolTip);
            }
            var tiles = mcp.ReleaseTiles.Children.Cast<RadioButton>().ToArray();
            CheckCounts();
            Assert.Equal(Loc.Current["Config.HttpStopped"], mcp.TransportStatus.Text);
            Assert.Same(mcp.FindResource("Primer.textFaint"), mcp.Clients.TargetDot.Fill);
            Assert.Equal(Visibility.Visible, mcp.HostsCard.Visibility);
            Assert.Equal(8, tiles.Length);
            Assert.True((bool)Assert.Single(tiles, tile => tile.IsChecked == true).Tag);
            Assert.Single(tiles, tile => (bool)tile.Tag);
            Assert.All(tiles, tile => Assert.True(tile.IsEnabled));
            var undetected = tiles.First(tile => !(bool)tile.Tag);
            undetected.IsChecked = true;
            Assert.Equal(mcp.Version.SelectedItem, undetected.Content);
            WpfContext.Drain();
            Assert.Equal("STA", mcp.ThreadText.Text);
            CheckCounts();
            mcp.SelectedReleaseKey = "21";
            mcp.SetReleaseEnabled(false);
            Assert.All(tiles, tile => Assert.False(tile.IsEnabled));
            mcp.SetReleaseEnabled(true);
            mcp.ServerAddress.Text = "127.0.0.1";
            mcp.ServerPort.Text = "8766";
            Assert.Equal("http://127.0.0.1:8766/mcp", mcp.LinkEndpoint.Text);
            Navigate(mcp.GoClients);
            var clients = mcp.Clients;
            Assert.Same(clients, window.ClientsHost.Content);
            Assert.Equal(Visibility.Collapsed, window.ConfigurationHost.Visibility);
            Assert.Equal(Visibility.Visible, window.ClientsHost.Visibility);
            Assert.DoesNotContain("Clients", Enum.GetNames<WorkbenchPage>());
            Assert.Null(window.ControlSurface.Snapshot.Read(new WorkbenchReadStateArguments()).Page);
            Assert.Equal("http://127.0.0.1:8766/mcp", clients.TargetEndpoint.Text);
            clients.PreviewJson.IsExpanded = true;
            Assert.True(clients.CopyJson.IsEnabled);
            var entry = JsonNode.Parse(clients.JsonText.Text)!["servers"]!["tia-portal-vm"]!;
            Assert.Equal("http://127.0.0.1:8766/mcp", (string?)entry["args"]![3]);
            Assert.DoesNotContain(Secret, clients.JsonText.Text);
            string? copied = null;
            mcp.CopyClientText = value => copied = value;
            Click(clients.CopyJson);
            Assert.Equal(clients.JsonText.Text, copied);
            Assert.Equal(Visibility.Visible, window.Toast.Visibility);
            Assert.Same(window.FindResource("Primer.ok"), window.ToastDot.Fill);
            Assert.Equal(Loc.Current.T("Config.FormatCopied", "JSON"), window.ToastMessage.Text);
            Assert.Null(clients.FindName("ClientStatus"));
            Navigate(clients.EditTarget);
            mcp.ServerPort.Text = "bad";
            Assert.False(clients.CopyJson.IsEnabled);
            Assert.DoesNotContain("8766", clients.JsonText.Text);
            mcp.LocalNav.IsChecked = true;
            mcp.TiaPath.Text = @"C:\fixture\Local Portal";
            Assert.Equal(Loc.Current.T("Config.LocalStatus", "V21"), mcp.TransportStatus.Text);
            Assert.Same(mcp.FindResource("Primer.ok"), mcp.Clients.TargetDot.Fill);
            Assert.Equal(Visibility.Collapsed, mcp.HostsCard.Visibility);
            Assert.Equal(Visibility.Collapsed, mcp.ServerPane.Visibility);
            mcp.HostList.Items.Add(new VisibleHost(37, 1, "21", Engine, root, "stdio", null, "1.0", false, false));
            Assert.Equal(Visibility.Visible, mcp.HostsCard.Visibility);
            mcp.HostList.Items.Clear();
            Assert.Equal(Visibility.Collapsed, mcp.HostsCard.Visibility);
            Assert.Equal(Loc.Current["Config.AssembliesMissing"], mcp.AssemblyStatus.Text);
            Navigate(mcp.GoClients);
            var local = JsonNode.Parse(clients.JsonText.Text)!["servers"]!["tia-portal"]!;
            Assert.Equal(mcp.TiaPath.Text, (string?)local["args"]!.AsArray().Last());
            Assert.DoesNotContain("--relay", clients.JsonText.Text);
            Assert.True(clients.CopyJson.IsEnabled);
            Assert.Same(mcp, window.Configuration);
            Assert.Same(mcp, window.ConfigurationHost.Content);
            Assert.False(File.Exists(((ClientProfile)clients.PreviewTarget.SelectedItem).Path));
            Assert.True(host.IsAncestorOf(clients));
        }
        finally { window.Close(); }
    });

    [Fact]
    public void Keyboard_can_select_local_transport_and_reopen_the_install_path_editor() => wpf.Run(() =>
    {
        var window = ConfigurationWindow();
        try
        {
            WpfContext.ShowWindow(window);
            var mcp = window.Configuration!;
            Assert.True(mcp.LocalNav.Focus());
            WpfContext.PressSpace(mcp.LocalNav);
            Assert.True(mcp.LocalNav.IsChecked);
            Assert.False(mcp.InstallDetails.IsExpanded);
            var toggle = WorkbenchRenderFeaturePagesTests.Descendants<ToggleButton>(mcp.InstallDetails)
                .Single(button => ReferenceEquals(button.TemplatedParent, mcp.InstallDetails));
            Assert.True(toggle.Focus());
            WpfContext.PressSpace(toggle);
            WpfContext.Drain();
            Assert.True(mcp.InstallDetails.IsExpanded);
            Assert.True(mcp.TiaPath.IsVisible);
            Assert.True(mcp.TiaPath.Focus());
            Assert.Equal("127.0.0.1", mcp.ServerAddress.Text);
            Assert.Equal("8765", mcp.ServerPort.Text);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void Detected_selection_and_automation_toggles_review_each_new_target_before_changing_selection() => wpf.Run(() =>
    {
        var window = ConfigurationWindow();
        try
        {
            window.Navigate("Clients");
            var host = WorkbenchRenderTests.CreateHost(window);
            var mcp = window.Configuration!;
            var clients = mcp.Clients;
            var profiles = clients.ClientChoices.Items.Cast<ClientProfile>().ToArray();
            var reviews = new List<ClientProfile>();
            mcp.ConfirmClientSelection = profile => { reviews.Add(profile); return profile != profiles[1]; };
            clients.ClientChoices.SelectAll();
            WpfContext.Drain();
            Assert.Empty(reviews);
            Assert.Equal(profiles.Length, clients.ClientChoices.SelectedItems.Count);
            Click(clients.ClearClients);
            Click(clients.SelectDetected);
            Assert.Equal(profiles.Take(2), reviews);
            Assert.Empty(clients.ClientChoices.SelectedItems);
            Assert.False(clients.SaveClient.IsEnabled);
            mcp.ConfirmClientSelection = _ => true;
            Click(clients.SelectDetected);
            Assert.Equal(profiles.Take(2), clients.ClientChoices.SelectedItems.Cast<ClientProfile>());
            Assert.True(clients.SaveClient.IsEnabled);
            clients.PreviewJson.IsExpanded = true;
            clients.PreviewTarget.SelectedItem = profiles[1];
            Assert.NotNull(JsonNode.Parse(clients.JsonText.Text)!["mcp"]!["tia-portal-vm"]!["command"]!.AsArray());
            var row = (ListBoxItem)clients.ClientChoices.ItemContainerGenerator.ContainerFromItem(profiles[2]);
            var tile = Assert.Single(WorkbenchRenderFeaturePagesTests.Descendants<ToggleButton>(row));
            var provider = (IToggleProvider)new ToggleButtonAutomationPeer(tile).GetPattern(PatternInterface.Toggle)!;
            reviews.Clear();
            mcp.ConfirmClientSelection = profile => { reviews.Add(profile); return false; };
            tile.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseDownEvent });
            Assert.Equal(ClickMode.Release, tile.ClickMode);
            Assert.Empty(reviews);
            provider.Toggle();
            Assert.Same(profiles[2], Assert.Single(reviews));
            Assert.False(tile.IsChecked);
            Assert.DoesNotContain(profiles[2], clients.ClientChoices.SelectedItems.Cast<ClientProfile>());
            mcp.ConfirmClientSelection = _ => true;
            provider.Toggle();
            clients.PreviewTarget.SelectedItem = profiles[2];
            string toml = clients.JsonText.Text;
            Assert.StartsWith("[mcp_servers.tia-portal-vm]" + Environment.NewLine, toml);
            Assert.Contains("command = ", toml);
            Assert.Contains("\"--relay\", \"http://127.0.0.1:8765/mcp\"", toml);
            Assert.Contains("tool_timeout_sec = 300", toml);
            Assert.Equal(Loc.Current.T("Config.HideFormat", "TOML"), clients.PreviewJson.Header);
            Assert.Equal(Loc.Current.T("Config.CopyFormat", "TOML"), clients.CopyJson.Content);
            clients.PreviewJson.IsExpanded = false;
            string? copied = null;
            mcp.CopyClientText = value => copied = value;
            Click(clients.CopyJson);
            Assert.Equal(toml, copied);
            Click(clients.SelectDetected);
            Assert.DoesNotContain(profiles[2], clients.ClientChoices.SelectedItems.Cast<ClientProfile>());
            Assert.All(profiles, profile => Assert.False(File.Exists(profile.Path)));
            Click(clients.ClearClients);
            Assert.False(clients.CopyJson.IsEnabled);
            Assert.False(clients.SaveClient.IsEnabled);
            Assert.True(host.IsAncestorOf(tile));
        }
        finally { window.Close(); }
    });

    [Fact]
    public void Firewall_and_client_write_keep_their_prompts_and_cancel_preserves_the_target() => wpf.Run(() =>
    {
        var window = ConfigurationWindow();
        try
        {
            WpfContext.ShowWindow(window);
            var mcp = window.Configuration!;
            Click(mcp.DetectTia);
            void CheckPrompt(Action action, string caption, string expectedText)
            {
                string? title = null;
                var messages = new List<string>();
                window.Dispatcher.BeginInvoke(DispatcherPriority.Background, () => {
                    var prompt = window.OwnedWindows.Cast<Window>().Single();
                    if (prompt is ClientsWriteConfirmation confirm) { confirm.Files.IsExpanded = true; confirm.UpdateLayout(); }
                    else Assert.IsType<WorkbenchMessageBox>(prompt);
                    title = prompt.Title;
                    messages.AddRange(WorkbenchRenderFeaturePagesTests.Descendants<TextBlock>(prompt).Select(text => text.Text));
                    prompt.Close();
                });
                action();
                WpfContext.Drain();
                Assert.Equal(caption, title);
                Assert.Contains(messages, text => text.Contains(expectedText, StringComparison.Ordinal));
            }
            CheckPrompt(() => Click(mcp.Network), Loc.Current["Config.Network"], Loc.Current["Config.NetworkPrompt"]);
            Navigate(mcp.GoClients);
            var target = (ClientProfile)mcp.Clients.ClientChoices.SelectedItems[0]!;
            Directory.CreateDirectory(Path.GetDirectoryName(target.Path)!);
            const string original = """{"servers":{"other":{"command":"keep.exe"},"tia-portal-vm":{"url":"http://127.0.0.1:8765/mcp"}}}""";
            File.WriteAllText(target.Path, original);
            CheckPrompt(() => Click(mcp.Clients.SaveClient), Loc.Current.T("Config.ConfirmWrite", 1), target.Path);
            Navigate(mcp.Clients.EditTarget);
            CheckPrompt(mcp.RepairRemoteClients, Loc.Current.T("Config.ConfirmWrite", 1), target.Path);
            Assert.Equal(Visibility.Visible, window.ClientsHost.Visibility);
            Assert.Equal(original, File.ReadAllText(target.Path));
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(target.Path)!, "*.bak_*"));
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData(1024, 640)]
    [InlineData(1366, 860)]
    public void Configuration_pages_render_in_both_languages_and_all_contrast_modes(int width, int height) => wpf.Run(() =>
    {
        var appearance = ThemeManager.Current;
        var previous = (appearance.Theme, appearance.HighContrastOverride, Loc.Current.Language);
        var catalogue = ClientProfiles.All().Select((client, index) =>
            new ClientProfile(client.Id, client.Name, Path.Combine(@"C:\fixture", client.Client, Path.GetFileName(client.Path)), "", client.Client, client.Kind)
            { Detected = index % 3 == 0 }).ToList();
        var window = ConfigurationWindow(catalogue);
        try
        {
            var host = WorkbenchRenderTests.CreateHost(window, width, height);
            var mcp = window.Configuration!;
            foreach (var language in new[] { AppLanguage.English, AppLanguage.Chinese })
            foreach (var mode in new[] { "Light", "Dark", "HighContrast" })
            {
                Loc.Current.Language = language;
                appearance.Theme = mode == "Dark" ? AppTheme.Dark : AppTheme.Light;
                appearance.HighContrastOverride = mode == "HighContrast";
                WorkbenchRenderTests.ApplyCurrentPalette(host);
                foreach (string page in new[] { "Mcp", "Clients" })
                {
                    mcp.RemoteNav.IsChecked = true;
                    window.Navigate(page);
                    host.UpdateLayout();
                    WpfContext.Drain();
                    host.UpdateLayout();
                    var toolbar = page == "Mcp" ? mcp.GoClients : mcp.Clients.ClearClients;
                    var bounds = toolbar.TransformToAncestor(host).TransformBounds(new Rect(toolbar.RenderSize));
                    Assert.True(toolbar.ActualHeight >= 24 && new Rect(host.RenderSize).Contains(bounds), $"{page} {language} {mode} {bounds}");
                    if (page == "Clients" && mode == "HighContrast")
                    {
                        var selected = mcp.Clients.ClientChoices.SelectedItems[0];
                        var row = (ListBoxItem)mcp.Clients.ClientChoices.ItemContainerGenerator.ContainerFromItem(selected);
                        var tile = Assert.Single(WorkbenchRenderFeaturePagesTests.Descendants<ToggleButton>(row));
                        var name = WorkbenchRenderFeaturePagesTests.Descendants<TextBlock>(tile).Single(text => text.Name == "ClientName");
                        Assert.Equal(Colors.Black, Assert.IsType<SolidColorBrush>(tile.Background).Color);
                        Assert.Equal(Colors.Yellow, Assert.IsType<SolidColorBrush>(tile.BorderBrush).Color);
                        Assert.Equal(Colors.White, Assert.IsType<SolidColorBrush>(name.Foreground).Color);
                    }
                    var output = Environment.GetEnvironmentVariable("TIA_WORKBENCH_RENDER_DIR");
                    if (!string.IsNullOrEmpty(output))
                    {
                        var folder = Directory.CreateDirectory(Path.Combine(output, "configuration"));
                        WorkbenchRenderTests.SaveRender(host, Path.Combine(folder.FullName, $"{page}-{width}x{height}-{language}-{mode}.png"), width, height);
                    }
                    mcp.LocalNav.IsChecked = true;
                    WpfContext.Drain();
                    host.UpdateLayout();
                    Assert.Equal("STDIO · TIA V21", mcp.Clients.TargetEndpoint.Text);
                    Assert.Equal(Visibility.Collapsed, mcp.HostsCard.Visibility);
                    if (page == "Mcp")
                    {
                        Assert.True(mcp.ModeHead.IsWrapped);
                        var title = mcp.ModeTitle.TranslatePoint(new Point(), host);
                        var tiles = mcp.LocalNav.TranslatePoint(new Point(), host);
                        Assert.True(tiles.Y >= title.Y + mcp.ModeTitle.ActualHeight);
                        Assert.Equal(8, mcp.ReleaseTiles.Columns);
                        foreach (RadioButton tile in mcp.ReleaseTiles.Children)
                        {
                            bool installed = (bool)tile.Tag;
                            Assert.Same(tile.FindResource(installed ? "Primer.Tile" : "Primer.Tile.Muted"), tile.Style);
                            var dash = Assert.IsType<System.Windows.Shapes.Rectangle>(tile.Template.FindName("Dash", tile));
                            Assert.Equal(installed ? Visibility.Collapsed : Visibility.Visible, dash.Visibility);
                            var labels = WorkbenchRenderFeaturePagesTests.Descendants<TextBlock>(tile).ToArray();
                            Assert.Equal(2, labels.Length);
                            var mark = Assert.Single(labels, label => label.Name == "InstalledLabel");
                            Assert.Equal(installed ? Loc.Current["Config.Found"] : "—", mark.Text);
                            Assert.Same(tile.FindResource(installed ? "Primer.ok" : "Primer.textFaint"), mark.Foreground);
                            var titleLabel = Assert.Single(labels, label => label.Name == "ReleaseName");
                            Assert.Same(installed ? tile.Foreground : tile.FindResource("Primer.textFaint"), titleLabel.Foreground);
                        }
                        Assert.Equal(880, mcp.InstallationCard.ActualWidth);
                        Assert.Equal(28, mcp.GoClients.ActualHeight);
                        Assert.Equal("MTA", mcp.ThreadText.Text);
                    }
                    else
                    {
                        var choices = mcp.Clients.ClientChoices;
                        var first = (ListBoxItem)choices.ItemContainerGenerator.ContainerFromIndex(0);
                        var badge = WorkbenchRenderFeaturePagesTests.Descendants<Border>(first).Single(border => border.Name == "Monogram");
                        var letters = Assert.IsType<TextBlock>(badge.Child);
                        Assert.Equal("CC", letters.Text);
                        Assert.Equal(32, badge.ActualWidth);
                        Assert.Equal(32, badge.ActualHeight);
                        Assert.Same(badge.FindResource("Primer.clientClaudeBg"), badge.Background);
                        Assert.Same(badge.FindResource("Primer.clientClaudeFg"), letters.Foreground);
                        var row = (ListBoxItem)choices.ItemContainerGenerator.ContainerFromItem(catalogue.Single(profile => profile.Id == "deepseek"));
                        var neutral = WorkbenchRenderFeaturePagesTests.Descendants<Border>(row).Single(border => border.Name == "Monogram");
                        Assert.Same(neutral.FindResource("Primer.clientNeutralBg"), neutral.Background);
                        Assert.Equal(3, Assert.IsType<UniformGrid>(VisualTreeHelper.GetParent(row)).Columns);
                    }
                    if (!string.IsNullOrEmpty(output))
                    {
                        var folder = Directory.CreateDirectory(Path.Combine(output, "configuration"));
                        var file = $"{page}-local-{width}x{height}-{language}-{mode}.png";
                        WorkbenchRenderTests.SaveRender(host, Path.Combine(folder.FullName, file), width, height);
                    }
                }
            }
        }
        finally
        {
            window.Close();
            (appearance.Theme, appearance.HighContrastOverride, Loc.Current.Language) = previous;
        }
    });
    private string Engine
    {
        get
        {
            foreach (string relative in new[] { "manifest/package-manifest.json", "manifest/delivery.json", "runtime/v21/TiaMcp.FoundationHost.exe" })
            {
                string path = Path.Combine(root, relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, "{}");
            }
            return ConfigCore.Engine(root, "21");
        }
    }

    [Fact]
    public void Every_client_schema_uses_stdio_relay_without_credentials() => wpf.Run(() =>
    {
        string engine = Engine;
        foreach (var profile in ClientProfiles.All())
        {
            var entry = ClientProfiles.Entry(profile, true, "192.0.2.10", 8765, engine, "21", null!);
            string serialized = ConfigCore.Json().Serialize(entry);
            Assert.DoesNotContain("Authorization", serialized);
            var arguments = profile.Client == "opencode" ? ((string[])entry["command"]).Skip(1).ToArray() : (string[])entry["args"];
            Assert.Equal(new[] { "--bundle-root", root, "--relay", "http://192.0.2.10:8765/mcp" }, arguments);
            if (profile.Client == "opencode") { Assert.Equal("local", entry["type"]); Assert.Equal(engine, ((string[])entry["command"])[0]); }
            else Assert.Equal(engine, entry["command"]);
        }
        string hostArguments = ConfigCore.Arguments(new ServerSettings { ReleaseKey = "21", Address = "127.0.0.1", Port = 8765, TiaPath = "TIA path" }, root);
        Assert.DoesNotContain("--http-api-key", hostArguments);
        Assert.Contains("--http-prefix", hostArguments);
    });

    [Theory]
    [InlineData("claude-code")][InlineData("vscode")][InlineData("gemini")][InlineData("opencode")][InlineData("codex")]
    public void Existing_remote_entries_require_confirmation_and_exact_backup(string client) => wpf.Run(() =>
    {
        string engine = Engine, path = Path.Combine(root, client == "codex" ? "config.toml" : "config.json");
        var profile = new ClientProfile(client, client, path, "");
        string original = client == "codex"
            ? "model = \"keep\"\r\n[mcp_servers.\"tia-portal-vm\"]\r\nurl = \"http://192.0.2.10:8765/mcp\"\r\nhttp_headers = { Authorization = \"Bearer " + Secret + "\" }\r\n[mcp_servers.other]\r\ncommand = \"keep.exe\"\r\n"
            : "{\"theme\":\"keep\",\"" + ClientProfiles.RootKey(profile) + "\":{\"other\":{\"command\":\"keep.exe\"},\"tia-portal-vm\":{\"url\":\"http://192.0.2.10:8765/mcp\",\"headers\":{\"Authorization\":\"Bearer " + Secret + "\"}}}}";
        File.WriteAllText(path, original, new UTF8Encoding(false));
        Assert.True(ClientProfiles.HasRemoteEntry(profile, "http://192.0.2.10:8765/mcp"));
        Assert.False(ClientProfiles.HasRemoteEntry(profile, "http://192.0.2.11:8765/mcp"));
        var change = ClientProfiles.PrepareSave(profile, true, "192.0.2.10", 8765, engine, "21", null!);
        Assert.True(change.RequiresMigration); Assert.Equal(original, File.ReadAllText(path));
        Assert.Throws<InvalidOperationException>(() => change.Apply(false)); Assert.Null(change.BackupPath);
        change.Apply(true);
        Assert.Equal(original, File.ReadAllText(change.BackupPath!));
        string saved = File.ReadAllText(path);
        Assert.DoesNotContain(Secret, saved); Assert.DoesNotContain("Authorization", saved);
        Assert.Contains("--relay", saved); Assert.Contains("keep.exe", saved); Assert.Contains("keep", saved);
        Assert.Contains(".bak_", change.BackupPath);
    });

    [Theory]
    [InlineData(AppLanguage.English)][InlineData(AppLanguage.Chinese)]
    public void Proxy_check_compares_routes_without_keys_and_localizes_guidance(AppLanguage language) => wpf.Run(() =>
    {
        var previous = Loc.Current.Language;
        try
        {
            Loc.Current.Language = language;
            string? Env(string name) => name == "HTTP_PROXY" ? "http://user:proxy-secret@127.0.0.1:7897" : null;
            var policy = new RemoteProxyPolicy(Env, new WebProxy(), _ => [IPAddress.Parse("192.0.2.10")]);
            var routes = new List<Uri?>();
            var finding = ProxyConnectionInspector.Inspect(["http://192.168.1.10:8765/mcp"], Env, policy,
                (_, proxy) => { routes.Add(proxy); return proxy == null ? 200 : 502; }, "WinINET: fixture");
            Assert.Equal("ProxyCaptured", finding.Result); Assert.Null(routes[0]); Assert.NotNull(routes[1]);
            Assert.Contains("200", finding.Evidence); Assert.Contains("502", finding.Evidence); Assert.Contains("NO_PROXY", finding.Evidence);
            Assert.DoesNotContain("proxy-secret", finding.Evidence); Assert.DoesNotContain(Secret, finding.Evidence);
            Assert.Equal(Loc.Current["Env.ProxyRemote"], EnvironmentCheckCatalogue.Name("proxy-remote").Resolve());
            Assert.Contains("NO_PROXY", EnvironmentCheckCatalogue.Fix("proxy-remote").Resolve());
            var context = new EnvironmentCheckContext { RemoteEndpoints = ["http://192.168.1.10:8765/mcp"] };
            var sources = new EnvironmentProbeSources { ProxyConnection = _ => finding };
            Assert.Same(finding, new WorkbenchEnvironmentChecks(context, sources).Run().Single(f => f.Id == "proxy-remote"));
        }
        finally { Loc.Current.Language = previous; }
    });

    [Fact]
    public async Task Reachability_probes_use_real_HTTP_without_sending_an_access_key()
    {
        static WebApplication Application(int status, Action<HttpContext> inspect)
        {
            var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders(); builder.WebHost.UseSetting("urls", "http://127.0.0.1:0");
            var app = builder.Build(); app.Run(context => { inspect(context); context.Response.StatusCode = status; return Task.CompletedTask; }); return app;
        }
        int direct = 0, proxied = 0;
        string? directAuthorization = null, proxyAuthorization = null;
        await using var endpoint = Application(200, context => { directAuthorization = context.Request.Headers.Authorization.ToString(); direct++; });
        await using var proxy = Application(502, context => { proxyAuthorization = context.Request.Headers.Authorization.ToString(); proxied++; });
        await endpoint.StartAsync(); await proxy.StartAsync();
        var target = new Uri(endpoint.Urls.Single() + "/mcp/health");
        Assert.Equal(200, await Task.Run(() => ProxyConnectionInspector.Probe(target, null)));
        Assert.Equal(502, await Task.Run(() => ProxyConnectionInspector.Probe(target, new Uri(proxy.Urls.Single()))));
        Assert.Equal(1, direct); Assert.Equal(1, proxied);
        Assert.Equal("", directAuthorization); Assert.Equal("", proxyAuthorization);
    }

    [Fact]
    public async Task Health_status_follows_the_tested_endpoint_and_clears_when_the_target_changes()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseSetting("urls", "http://127.0.0.1:0");
        await using var server = builder.Build();
        bool ready = true;
        server.MapGet("/mcp/health", () => Results.Json(new { fileVersion = "1.0" }));
        server.MapGet("/mcp/ready", () => Results.Json(new { mcpHostReady = ready }));
        await server.StartAsync();
        wpf.Run(() =>
        {
            var window = ConfigurationWindow(running: true);
            try
            {
                var mcp = window.Configuration!;
                mcp.ServerPort.Text = new Uri(server.Urls.Single()).Port.ToString();
                Assert.Equal(Loc.Current.T("Config.HttpRunning", 8765), mcp.TransportStatus.Text);
                void Probe(string expected)
                {
                    var finished = new TaskCompletionSource<bool>();
                    void Changed(object sender, DependencyPropertyChangedEventArgs args)
                    {
                        if (mcp.Panes.IsEnabled) finished.TrySetResult(true);
                    }
                    mcp.Panes.IsEnabledChanged += Changed;
                    try
                    {
                        Click(mcp.TestClient);
                        Assert.False(mcp.SaveBoth.IsEnabled);
                        WpfContext.Complete(finished.Task.WaitAsync(TiaMcp.Engine.Tests.Shared.TestDeadlines.Hang));
                        Assert.True(mcp.SaveBoth.IsEnabled);
                        Assert.Equal(Loc.Current[expected], mcp.HealthResult.Text);
                    }
                    finally { mcp.Panes.IsEnabledChanged -= Changed; }
                }
                Probe("Config.ProbeOk");
                ready = false;
                Probe("Config.TestFailed");
                mcp.ServerPort.Text = "8766";
                Assert.Equal(Loc.Current["Config.NoProbe"], mcp.HealthResult.Text);
                Assert.Equal(Loc.Current.T("Config.HttpRunning", 8765), mcp.TransportStatus.Text);
            }
            finally { window.Close(); }
        });
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
