using System;
using System.IO;
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
                    Assert.True(mcp.HostsCard.TranslatePoint(default, host).Y < mcp.ActivityCard.TranslatePoint(default, host).Y);
                    Assert.True(mcp.ActivityCard.TranslatePoint(default, host).Y < mcp.InstallationCard.TranslatePoint(default, host).Y);
                    var rows = DesktopCapture.Descendants<Button>(mcp.HostList).ToArray();
                    Assert.Equal(new[] { true, false, false }, rows.Where(b => b.Name == "RestartHost").Select(b => b.IsEnabled));
                    Assert.Equal(new[] { true, true, false }, rows.Where(b => b.Name == "StopHost").Select(b => b.IsEnabled));
                    mcp.ConfigurationActivity.IsExpanded = false;
                    Assert.Equal(Visibility.Collapsed, ((FrameworkElement)mcp.ConfigurationLogCard.Parent).Visibility);
                    mcp.HostList.ItemsSource = null;
                    Assert.Equal(Visibility.Visible, mcp.NoHosts.Visibility);
                    if (running) continue;

                    mcp.LocalNav.IsChecked = true;
                    Capture("mcp-local");
                    Assert.Equal(Visibility.Collapsed, mcp.HostsCard.Visibility);
                    Navigate(mcp.GoClients);
                    mcp.ConfirmClientSelection = _ => true;
                    foreach (var client in catalogue.Where(c => c.Id is "codex" or "cursor" or "vscode"))
                        Assert.True(mcp.SetClientSelected(client, true));
                    Capture("clients");
                    Assert.Equal(4, clients.ClientChoices.SelectedItems.Count);
                    clients.PreviewJson.IsExpanded = true;
                    Capture("clients-preview");
                    Assert.Contains("tia-portal", clients.JsonText.Text);
                    Assert.Contains(DesktopCapture.Descendants<TextBlock>(clients), block => block == clients.JsonText);
                    clients.PreviewTarget.SelectedItem = catalogue.Single(c => c.Id == "codex");
                    Capture("clients-preview-toml");
                    Assert.StartsWith("[mcp_servers.tia-portal]", clients.JsonText.Text);
                    clients.PreviewJson.IsExpanded = false;
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
