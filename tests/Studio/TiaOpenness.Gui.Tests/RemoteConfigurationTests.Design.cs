using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Linq;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using TiaMcpConfigurator;
using TiaOpenness.Gui.Localization;
using TiaOpenness.Gui.Services;
using TiaOpenness.Gui.Themes;
using Xunit;

namespace TiaOpenness.Gui.Tests;

public sealed partial class RemoteConfigurationTests
{
    [Theory]
    [InlineData("light-zh", 1360, 812)]
    [InlineData("light-en", 1360, 812)]
    [InlineData("dark-zh", 1360, 812)]
    [InlineData("hc-zh", 1360, 812)]
    [InlineData("light-zh-1024x640", 1024, 640)]
    public void Service_and_client_design_states_keep_their_actions_and_confirm_the_actual_targets(string variant, int width, int height) => wpf.Run(() =>
    {
        var theme = ThemeManager.Current;
        var previous = (theme.Theme, theme.HighContrastOverride, Loc.Current.Language);
        var output = Environment.GetEnvironmentVariable("TIA_WORKBENCH_RENDER_DIR");
        var folder = output == null ? null : Directory.CreateDirectory(Path.Combine(output, "b3")).FullName;
        try
        {
            Loc.Current.Language = variant == "light-en" ? AppLanguage.English : AppLanguage.Chinese;
            theme.Theme = variant == "dark-zh" ? AppTheme.Dark : AppTheme.Light;
            theme.HighContrastOverride = variant == "hc-zh";
            string install = CreateInstallationFixture();
            foreach (bool running in new[] { false, true })
            {
                var catalogue = ClientProfiles.All().Select(c => new ClientProfile(c.Id, c.Name,
                    Path.Combine(root, c.Client, Path.GetFileName(c.Path)), "", c.Client, c.Kind)
                    { Detected = c.Id is "claude-code" or "codex" or "cursor" or "vscode" or "gemini" }).ToList();
                var window = ConfigurationWindow(catalogue, running, install, "09:30:00   Fixture initialized\n09:30:01   Configuration loaded\n");
                try
                {
                    var host = WorkbenchRenderTests.CreateHost(window, width, height);
                    WorkbenchRenderTests.ApplyCurrentPalette(host);
                    var mcp = window.Configuration!;
                    var clients = mcp.Clients;
                    mcp.HostList.ItemsSource = new[] {
                        new VisibleHost(37, 1, "21", Engine, root, "http", "http://127.0.0.1:8765/", "4.0", true, false, SessionCount: 2),
                        new VisibleHost(38, 2, "21", Engine, root, "stdio", null, "4.0", false, false, SessionCount: 1),
                        new VisibleHost(39, 3, "19", Engine, root, "http", "http://127.0.0.1:8766/", "3.9", false, true, CallInProgress: true, SessionCount: 0),
                    };
                    void Layout()
                    {
                        WpfContext.Drain();
                        host.UpdateLayout();
                        WpfContext.Drain();
                        host.UpdateLayout();
                    }
                    void Capture(string view)
                    {
                        Layout();
                        Assert.Equal((double)width, host.ActualWidth);
                        Assert.Equal((double)height, host.ActualHeight);
                        if (folder != null) WorkbenchRenderTests.SaveRender(host, Path.Combine(folder, $"{view}-{variant}.png"), width, height);
                    }

                    Capture(running ? "mcp-remote-running" : "mcp-remote-stopped");
                    Assert.Equal(running ? Visibility.Visible : Visibility.Collapsed, mcp.StopServer.Visibility);
                    Assert.Equal(running ? Visibility.Collapsed : Visibility.Visible, mcp.StartServer.Visibility);
                    Assert.Equal(running ? Loc.Current.T("Config.HttpRunning", "8765") : Loc.Current["Config.HttpStopped"], mcp.TransportStatus.Text);
                    Assert.Equal(Loc.Current["Config.NoProbe"], mcp.HealthResult.Text);
                    Assert.Contains(DesktopCapture.Descendants<TextBlock>(mcp.LocalNav), block => block.Text == Loc.Current["Config.LocalTileHint"]);
                    Assert.Contains(DesktopCapture.Descendants<TextBlock>(mcp.RemoteNav), block => block.Text == Loc.Current["Config.RemoteTileHint"]);
                    Assert.True(mcp.InstallationCard.TranslatePoint(default, host).Y < mcp.HostsCard.TranslatePoint(default, host).Y);
                    CheckPageColumns(mcp.Panes, mcp.ActivityCard, host, width, 348);
                    var rows = DesktopCapture.Descendants<Button>(mcp.HostList).ToArray();
                    Assert.Equal(new[] { true, false, false }, rows.Where(b => b.Name == "RestartHost").Select(b => b.IsEnabled));
                    Assert.Equal(new[] { true, true, false }, rows.Where(b => b.Name == "StopHost").Select(b => b.IsEnabled));
                    Assert.True(mcp.ActivityCard.IsAncestorOf(mcp.ConfigurationLogCard));
                    Assert.Equal(Visibility.Visible, mcp.ConfigurationLogCard.Visibility);
                    mcp.HostList.ItemsSource = null;
                    Assert.Equal(Visibility.Visible, mcp.NoHosts.Visibility);
                    if (running) continue;

                    mcp.LocalNav.IsChecked = true;
                    Capture("mcp-local");
                    Assert.Equal(Visibility.Visible, mcp.HostsCard.Visibility);
                    Navigate(window, window.RailClients);
                    mcp.ConfirmClientSelection = _ => true;
                    foreach (var client in catalogue.Where(c => c.Id is "codex" or "cursor" or "vscode"))
                        Assert.True(mcp.SetClientSelected(client, true));
                    Capture("clients");
                    CheckPageColumns(clients.ClientsScroll, clients.ClientActionsBand, host, width, 392);
                    Assert.Equal(2, clients.ClientChoices.Items.Groups!.Count);
                    Assert.Equal(4, clients.ClientChoices.SelectedItems.Count);
                    Assert.True(clients.ClientActionsBand.IsAncestorOf(clients.JsonText));
                    Capture("clients-preview");
                    Assert.Contains("tia-portal", clients.JsonText.Text);
                    Assert.Contains(DesktopCapture.Descendants<TextBlock>(clients), block => block == clients.JsonText);
                    clients.PreviewTarget.SelectedItem = catalogue.Single(c => c.Id == "codex");
                    Capture("clients-preview-toml");
                    Assert.StartsWith("[mcp_servers.tia-portal]", clients.JsonText.Text);
                    Assert.Equal(TextWrapping.Wrap, clients.JsonText.TextWrapping);
                    window.Width = width; window.Height = height; window.Content = host;
                    WpfContext.ShowWindow(window);
                    VerifyAndCaptureWrite(window, host, folder, variant);
                    Assert.All(catalogue, c => Assert.False(File.Exists(c.Path)));
                }
                finally { window.Close(); }
            }
        }
        finally { (theme.Theme, theme.HighContrastOverride, Loc.Current.Language) = previous; }
    });

    [Fact]
    public void Address_picker_filters_local_adapters_rescans_and_copies_the_current_transport() => wpf.Run(() =>
    {
        var window = ConfigurationWindow(install: CreateInstallationFixture());
        try
        {
            var mcp = window.Configuration!;
            var lan = new AdapterAddress("Ethernet_2", "LAN adapter", NetworkInterfaceType.Ethernet, OperationalStatus.Up, IPAddress.Parse("192.168.1.5"));
            var hostOnly = lan with { Name = "VirtualBox Host-Only", Address = IPAddress.Parse("192.168.56.1") };
            AdapterAddress[] rejected = [
                lan with { Status = OperationalStatus.Down }, lan with { Type = NetworkInterfaceType.Tunnel },
                lan with { Type = NetworkInterfaceType.Ppp }, lan with { Type = NetworkInterfaceType.Loopback }, lan with { Name = "VPN" },
                lan with { Description = "TAP-Windows" }, lan with { Description = "WireGuard" }, lan with { Description = "Wintun" },
                lan with { Address = IPAddress.Loopback }, lan with { Address = IPAddress.Parse("169.254.1.1") },
                lan with { Address = IPAddress.Parse("203.0.113.2") }, lan with { Address = IPAddress.Parse("fd00::1") },
            ];
            int scans = 0;
            mcp.ReadAddresses = () => ++scans == 1 ? [lan, .. rejected, hostOnly] : [];
            WpfContext.ShowWindow(window);
            Click(mcp.ScanAddresses);
            var menu = mcp.ScanAddresses.ContextMenu;
            var items = menu.Items.OfType<MenuItem>().ToArray();
            Assert.Equal(3, items.Length);
            Assert.Contains(Loc.Current["Config.Recommended"], Assert.IsType<TextBlock>(items[0].Header).Text);
            Assert.Contains("LAN", Assert.IsType<TextBlock>(items[1].Header).Text);
            menu.UpdateLayout();
            string label = $"{lan.Address}   Ethernet_2 · LAN";
            Assert.Contains(WorkbenchRenderFeaturePagesTests.Descendants<TextBlock>(items[1]), text => text.Text == label);
            Assert.DoesNotContain(WorkbenchRenderFeaturePagesTests.Descendants<AccessText>(items[1]), text => text.AccessKey != '\0');
            Assert.Equal(label, new System.Windows.Automation.Peers.MenuItemAutomationPeer(items[1]).GetName());
            items[0].RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.Equal(hostOnly.Address.ToString(), mcp.ServerAddress.Text);
            menu.IsOpen = false;
            items[2].RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.Equal(2, scans);
            var empty = mcp.ScanAddresses.ContextMenu.Items.OfType<MenuItem>().First();
            Assert.False(empty.IsEnabled);
            Assert.Equal(Loc.Current["Config.NoAdapters"], empty.Header);
            mcp.ScanAddresses.ContextMenu.IsOpen = false;
            mcp.ServerAddress.Text = "10.0.2.5";
            string? copied = null;
            mcp.CopyClientText = text => copied = text;
            Click(mcp.CopyEndpoint);
            Assert.Equal("http://10.0.2.5:8765/mcp", copied);
            mcp.LocalNav.IsChecked = true;
            Assert.Equal(Visibility.Visible, mcp.StdioReady.Visibility);
            Click(mcp.CopyStdio);
            Assert.Equal(string.Join(" ", ConfigCore.LocalArguments(Engine, "21", mcp.TiaPath.Text).Prepend(Engine).Select(ConfigCore.Quote)), copied);
            mcp.Version.SelectedValue = "17";
            WpfContext.Drain();
            Assert.False(mcp.CopyStdio.IsEnabled);
            Assert.Equal(Visibility.Collapsed, mcp.StdioReady.Visibility);
        }
        finally { window.Close(); }
    });

    private sealed class TestAdapter(string name) : NetworkInterface
    {
        public override string Name => name;
        public override string Description => name;
        public override OperationalStatus OperationalStatus => OperationalStatus.Up;
        public override NetworkInterfaceType NetworkInterfaceType => NetworkInterfaceType.Ethernet;
        public override IPInterfaceProperties GetIPProperties() => throw new NetworkInformationException();
    }

    [Fact]
    public void Scan_keeps_healthy_adapters_when_an_adapter_or_one_of_its_addresses_disappears()
    {
        var address = IPAddress.Parse("192.168.1.12");
        IEnumerable<IPAddress> Addresses(NetworkInterface nic)
        {
            if (nic.Name == "Missing") _ = nic.GetIPProperties();
            yield return address;
            if (nic.Name == "Interrupted") throw new NetworkInformationException();
        }
        var rows = AdapterAddress.Scan([new TestAdapter("First"), new TestAdapter("Missing"), new TestAdapter("Interrupted"),
            new TestAdapter("Last")], Addresses).ToList();
        Assert.Equal(new[] { "First", "Last" }, rows.Select(row => row.Name));
        Assert.All(rows, row => Assert.Equal(address, row.Address));
    }

    [Fact]
    public void Client_write_footer_tracks_the_viewport_while_the_narrow_catalogue_scrolls() => wpf.Run(() =>
    {
        var window = ConfigurationWindow(ClientProfiles.Catalogue());
        try
        {
            var mcp = window.Configuration!;
            window.Navigate("Clients");
            var host = WorkbenchRenderTests.CreateHost(window, 1360, 640);
            var clients = mcp.Clients;
            var viewport = (DockPanel)clients.Content;
            mcp.LocalNav.IsChecked = true;
            Assert.Equal(Visibility.Collapsed, clients.RelayNote.Visibility);
            mcp.RemoteNav.IsChecked = true;
            Assert.Equal(Visibility.Visible, clients.RelayNote.Visibility);
            Assert.Equal(Loc.Current["Config.RelayLabel"], clients.RelayNote.Text);
            Assert.Same(clients.SidebarFooter, clients.WriteBar.Parent);
            void Resize(int width)
            {
                host.Width = width;
                host.Measure(new Size(width, 640)); host.Arrange(new Rect(0, 0, width, 640));
                host.UpdateLayout(); WpfContext.Drain(); host.UpdateLayout();
            }
            foreach (int width in new[] { 1199, 1024 })
            {
                Resize(width);
                Assert.Same(clients.ViewportFooter, clients.WriteBar.Parent);
                Assert.True(clients.WriteBar.IsAncestorOf(clients.SaveClient));
                var scroll = clients.ClientLayout.Children.OfType<ScrollViewer>().Single();
                Assert.True(scroll.ScrollableHeight > 0);
                scroll.ScrollToTop(); WpfContext.Drain(); host.UpdateLayout();
                var button = clients.SaveClient.TranslatePoint(default, viewport);
                var footer = clients.WriteBar.TranslatePoint(default, viewport);
                Assert.True(clients.SaveClient.ActualHeight > 0);
                Assert.InRange(footer.Y, 0, viewport.ActualHeight);
                Assert.InRange(button.Y + clients.SaveClient.ActualHeight, footer.Y, viewport.ActualHeight);
                Assert.Equal(viewport.ActualHeight, clients.ViewportFooter.TranslatePoint(new Point(0, clients.ViewportFooter.ActualHeight), viewport).Y, 1);
                Assert.True(scroll.TranslatePoint(new Point(0, scroll.ActualHeight), viewport).Y <= footer.Y);
                scroll.ScrollToEnd(); WpfContext.Drain(); host.UpdateLayout();
                Assert.True(scroll.VerticalOffset > 0);
                Assert.Equal(footer, clients.WriteBar.TranslatePoint(default, viewport));
                Assert.Equal(button, clients.SaveClient.TranslatePoint(default, viewport));
            }
            Resize(1200);
            Assert.Same(clients.SidebarFooter, clients.WriteBar.Parent);
            Assert.Equal(Visibility.Collapsed, clients.ViewportFooter.Visibility);
            Assert.Equal(392, clients.ClientActionsBand.ActualWidth);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void Shared_client_files_have_one_preview_and_keep_the_actual_schema() => wpf.Run(() =>
    {
        string shared = Path.Combine(root, "opencode.json");
        var window = ConfigurationWindow([
            new("opencode", "OpenCode", shared, "", "opencode"),
            new("deepseek", "DeepSeek", shared, "", "opencode"),
            new("codex", "Codex", Path.Combine(root, "config.toml"), ""),
        ]);
        try
        {
            var clients = window.Configuration!.Clients;
            clients.ClientChoices.SelectAll();
            Assert.Equal(3, clients.ClientChoices.SelectedItems.Count);
            Assert.Equal(2, clients.PreviewTarget.Items.Count);
            Assert.Equal(Loc.Current.T("Config.TargetFileCount", 2), clients.WriteTargets.Text);
            Assert.NotNull(JsonNode.Parse(clients.JsonText.Text)!["mcp"]);
            clients.PreviewTarget.SelectedIndex = 1;
            Assert.StartsWith("[mcp_servers.", clients.JsonText.Text);
            Assert.False(File.Exists(shared));
        }
        finally { window.Close(); }
    });

    [Fact]
    public void Stop_reviews_the_clicked_host_even_when_a_different_row_is_selected() => wpf.Run(() =>
    {
        var window = ConfigurationWindow();
        try
        {
            var mcp = window.Configuration!;
            var first = new VisibleHost(37, 1, "21", Engine, root, "http", "http://127.0.0.1:8765/", "4.0", true, false);
            var second = first with { Pid = 38, WorkbenchStarted = false };
            mcp.HostList.ItemsSource = new[] { first, second };
            mcp.HostList.SelectedItem = first;
            WpfContext.ShowWindow(window);
            window.UpdateLayout();
            string? question = null;
            window.Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
            {
                var prompt = Assert.Single(window.OwnedWindows.OfType<TiaOpenness.Gui.Controls.WorkbenchMessageBox>());
                question = string.Join("\n", DesktopCapture.Descendants<TextBlock>(prompt).Select(b => b.Text));
                prompt.Close();
            });
            var stop = DesktopCapture.Descendants<Button>(mcp.HostList).Single(b => b.Name == "StopHost" && ReferenceEquals(b.DataContext, second));
            Click(stop);
            Assert.Contains(second.Display, question);
            Assert.DoesNotContain(first.Display, question);
            Assert.Equal(2, mcp.HostList.Items.Count);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void Confirming_writes_the_reviewed_JSON_and_TOML_to_the_selected_files_only() => wpf.Run(() =>
    {
        var window = ConfigurationWindow(install: CreateInstallationFixture());
        try
        {
            var mcp = window.Configuration!;
            mcp.LocalNav.IsChecked = true;
            window.Navigate("Clients");
            WpfContext.ShowWindow(window);
            var clients = mcp.Clients;
            var profiles = clients.ClientChoices.Items.Cast<ClientProfile>().ToArray();
            mcp.ConfirmClientSelection = _ => true;
            Assert.True(mcp.SetClientSelected(profiles[2], true));
            clients.PreviewTarget.SelectedItem = profiles[0];
            var expectedJson = JsonNode.Parse(clients.JsonText.Text);
            clients.PreviewTarget.SelectedItem = profiles[2];
            string expectedToml = clients.JsonText.Text;
            bool reviewed = false;
            window.Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
            {
                var prompt = window.OwnedWindows.OfType<ClientsWriteConfirmation>().Single();
                reviewed = prompt.TargetFiles.Text.Contains(profiles[0].Path, StringComparison.Ordinal)
                    && prompt.TargetFiles.Text.Contains(profiles[2].Path, StringComparison.Ordinal);
                if (reviewed) Click(prompt.ConfirmWrite);
                else prompt.Close();
            });
            Click(clients.SaveClient);
            Assert.True(reviewed);
            Assert.True(JsonNode.DeepEquals(expectedJson, JsonNode.Parse(File.ReadAllText(profiles[0].Path))));
            Assert.Equal(expectedToml, File.ReadAllText(profiles[2].Path));
            Assert.False(File.Exists(profiles[1].Path));
        }
        finally { window.Close(); }
    });

    internal static void CheckPageColumns(FrameworkElement main, FrameworkElement side, FrameworkElement host, double width, double sidebarWidth)
    {
        var left = main.TranslatePoint(default, host);
        var right = side.TranslatePoint(default, host);
        if (width < 1200)
        {
            Assert.Equal(left.X, right.X);
            Assert.True(right.Y >= left.Y + main.ActualHeight - 1);
            Assert.Equal(main.ActualWidth, side.ActualWidth);
        }
        else
        {
            Assert.Equal(left.Y, right.Y);
            Assert.Equal(left.X + main.ActualWidth, right.X);
            Assert.Equal(sidebarWidth, side.ActualWidth);
        }
    }

    private string CreateInstallationFixture()
    {
        var release = TiaMcp.Versioning.TiaVersionCatalog.Get("21");
        string install = Directory.CreateDirectory(Path.Combine(root, release.InstallFolder)).FullName;
        // Installation discovery checks this file's presence without loading a vendor assembly.
        File.Copy(typeof(RemoteConfigurationTests).Assembly.Location, Path.Combine(install, release.ApiAssembly));
        return install;
    }

    private static void VerifyAndCaptureWrite(MainWindow window, FrameworkElement host, string? folder, string variant)
    {
        Exception? error = null;
        bool reviewed = false;
        window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
        {
            var dialog = window.OwnedWindows.OfType<ClientsWriteConfirmation>().SingleOrDefault();
            try
            {
                Assert.NotNull(dialog);
                dialog.Left = window.Left + (window.ActualWidth - dialog.ActualWidth) / 2;
                dialog.Top = window.Top + (window.ActualHeight - dialog.ActualHeight) / 2;
                host.UpdateLayout(); dialog.UpdateLayout();
                Assert.Equal(Visibility.Visible, window.OwnedDialogScrim.Visibility);
                Assert.Equal(Loc.Current.T("Config.ConfirmWrite", 4), dialog.Title);
                Assert.True(dialog.CancelWrite.IsDefault);
                Assert.True(dialog.CancelWrite.IsCancel);
                Assert.False(dialog.ConfirmWrite.IsDefault);
                Assert.True(dialog.CancelWrite.IsKeyboardFocused);
                Assert.Equal(440, dialog.ActualWidth);
                if (folder != null)
                {
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(DesktopCapture.Render(host, (FrameworkElement)dialog.Content)));
                    using var stream = File.Create(Path.Combine(folder, $"confirm-clients-{variant}.png"));
                    encoder.Save(stream);
                }
                dialog.Files.IsExpanded = true;
                foreach (ClientProfile selected in window.Configuration!.Clients.ClientChoices.SelectedItems)
                {
                    Assert.Contains(selected.DisplayName, dialog.TargetNames.Text);
                    Assert.Contains(selected.Path, dialog.TargetFiles.Text);
                }
                reviewed = true;
            }
            catch (Exception ex) { error = ex; }
            finally { dialog?.Close(); }
        });
        Click(window.Configuration!.Clients.SaveClient);
        Assert.Null(error);
        Assert.True(reviewed);
        Assert.Equal(Visibility.Collapsed, window.OwnedDialogScrim.Visibility);
    }
}
