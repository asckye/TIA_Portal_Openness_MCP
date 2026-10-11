using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using TiaOpenness.Gui.Services;
using TiaOpenness.Gui.ViewModels;
using Xunit;

namespace TiaOpenness.Gui.Tests;

[Collection(WpfCollection.Name)]
public sealed class OfflineDemoTests(WpfContext wpf)
{
    [Theory, InlineData("--mock"), InlineData("--demo")]
    public void Demo_entry_shows_only_offline_pages_and_cannot_open_configuration(string flag) => wpf.Run(() =>
    {
        Assert.True(MainViewModel.OfflineDemoRequested([flag]));
        var model = new MainViewModel(true);
        var window = new MainWindow(model, false, diagnostics: new FeaturePageFixtures.Diagnostics());
        try
        {
            window.Show(); WpfContext.Complete(model.ApplyStartupAsync([flag, "--project", @"D:\NeverOpened.ap21"]));
            Assert.False(model.HasMcpPanel); Assert.False(model.Session.IsConnected);
            Assert.False(model.VersionControlPageVisible);
            Assert.Equal(Visibility.Collapsed, ((RadioButton)window.FindName("RailMcp")).Visibility);
            Assert.Equal(Visibility.Collapsed, ((RadioButton)window.FindName("RailEngineering")).Visibility);
            window.ShowConfiguration(); Assert.Null(window.Configuration);
            Assert.Equal(Visibility.Collapsed, ((Button)window.FindName("SettingsButton")).Visibility);
            window.OpenSettings(); Assert.Equal(Visibility.Collapsed, ((FrameworkElement)window.FindName("DrawerOverlay")).Visibility);
            foreach (string page in new[] { "Calls", "Audit", "Environment", "Log" }) window.Navigate(page);
            window.Navigate("Engineering");
            Assert.Equal(Visibility.Collapsed, ((FrameworkElement)window.FindName("OperationsContent")).Visibility);
            Assert.Contains(model.Activity.Entries, e => e.Key == "Session.OfflineDemo");
            if (Environment.GetEnvironmentVariable("TIA_WORKBENCH_RENDER_DIR") is { Length: > 0 } output)
            {
                Directory.CreateDirectory(output);
                WorkbenchRenderTests.SaveRender(WorkbenchRenderTests.CreateHost(window), Path.Combine(output, "offline" + flag + ".png"));
            }
        }
        finally { window.Close(); }
    });
    [Fact]
    public async Task Offline_client_has_no_discovery_transport_or_launch_path()
    {
        await using var client = new OfflineSessionClient();
        Assert.Empty(await client.DiscoverAsync("21"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.CreateAsync("21"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.CallAsync("ConnectPortal", new Dictionary<string, object?>()));
        Assert.Null(client.SessionKey); Assert.Null(client.State); Assert.Null(client.ToolSchema("ConnectPortal"));
        await client.DetachAsync();
    }
    [Fact]
    public void Offline_environment_fix_cannot_read_configuration_or_start_the_real_doctor_host()
    {
        var service = new EnvironmentCheckService(() => throw new Exception("Configuration read forbidden."),
            () => throw new Exception("Probe forbidden."), _ => throw new Exception("Host launch forbidden."), allowFix: false);
        Assert.NotEqual(default, service.Fix("membership")); Assert.True(service.Completion.IsCompletedSuccessfully);
        Assert.Empty(service.Findings);
    }
}
