using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using TiaMcp.Engine.Tests.Shared;
using TiaOpenness.Gui.Common;
using TiaOpenness.Gui.Controls;
using TiaOpenness.Gui.Localization;
using TiaOpenness.Gui.Services;
using TiaOpenness.Gui.Services.Stubs;
using TiaOpenness.Gui.Themes;
using TiaOpenness.Gui.ViewModels;
using Xunit;

namespace TiaOpenness.Gui.Tests;

public sealed partial class WorkbenchControlOverlayTests
{
    private static void InvokeSessionButton(Button button, AsyncCommand command)
    {
        Assert.True(button.IsEnabled);
        Assert.Same(command, button.Command);
        UnifiedDesktopTests.ClickControl(button);
        WpfContext.Complete(Completed().WaitAsync(TestDeadlines.Hang));
        async Task Completed()
        {
            while (command.IsRunning) await Task.Delay(10);
        }
    }

    [Fact]
    public async Task Popover_discovers_attaches_detaches_and_creates_through_the_operator_panel()
    {
        await using var fixture = new McpOperatorFixture("19");
        var client = fixture.Client();
        wpf.Run(() =>
        {
            var model = new MainViewModel(new McpProjectClient(client), new FakeDialogService());
            var preview = new TiaMcpConfigurator.ConfigurationPreview("19", @"C:\fixture", "127.0.0.1", true, false, true, "", "", []);
            var window = new MainWindow(model, false, new ApprovalServiceStub(), preview: preview);
            try
            {
                WpfContext.ShowWindow(window);
                model.SelectedReleaseKey = "18";
                var panel = model.Panel!;
                var popover = OpenSession(window);
                UnifiedDesktopTests.ClickControl(popover.VersionMenuButton);
                var menu = popover.VersionMenuButton.ContextMenu;
                var item = Assert.Single(menu.Items.OfType<MenuItem>(), row => Equals(row.Header, "V19 · STA"));
                item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, item));
                menu.IsOpen = false;
                Assert.Equal(fixture.Host.ReleaseKey, model.SelectedReleaseKey);
                Assert.False(popover.Attach.IsEnabled);
                InvokeSessionButton(popover.Discover, panel.Refresh);
                var found = Assert.IsType<OperatorSession>(Assert.Single(popover.SessionList.Items));
                Assert.Equal(fixture.Key, found.SessionKey);
                Assert.Contains(Loc.Current["Session.Unverified"], found.Summary);
                Assert.Contains(Loc.Current["ProcessPicker.NoProject"], found.ProjectAndParticipants);
                Assert.False(panel.Attached);
                popover.SessionList.SelectedItem = found;
                InvokeSessionButton(popover.Attach, panel.Attach);
                Assert.Equal(fixture.Key, panel.SessionKey);
                Assert.Empty(window.Root.Children.OfType<SessionPopover>());
                InvokeSessionButton(OpenSession(window).Detach, panel.Detach);
                Assert.False(panel.Attached);
                InvokeSessionButton(OpenSession(window).Create, panel.Create);
                Assert.True(panel.Attached);
                Assert.Contains("create", fixture.Actions);
                InvokeSessionButton(OpenSession(window).Detach, panel.Detach);
                Assert.False(panel.Attached);
                Assert.DoesNotContain(fixture.Calls, call => call.Name == "DisconnectPortal");
            }
            finally { window.Close(); }
        });
    }

    [Theory]
    [InlineData(1360, 812, AppTheme.Light, AppLanguage.Chinese, false)]
    [InlineData(1360, 812, AppTheme.Light, AppLanguage.English, false)]
    [InlineData(1360, 812, AppTheme.Dark, AppLanguage.Chinese, false)]
    [InlineData(1360, 812, AppTheme.Light, AppLanguage.Chinese, true)]
    [InlineData(1024, 640, AppTheme.Light, AppLanguage.Chinese, false)]
    public async Task Overlay_cards_show_the_bound_project_and_safe_choices(int width, int height, AppTheme theme, AppLanguage language, bool hc)
    {
        await using var fixture = new McpOperatorFixture("19");
        fixture.BindProject();
        var client = fixture.Client();
        await client.AttachAsync(Assert.Single(await client.DiscoverAsync("19")));
        wpf.RunWithLanguage(language, () =>
        {
            var manager = ThemeManager.Current;
            var saved = (manager.Theme, manager.Accent, manager.HighContrastOverride, manager.LowEffects);
            manager.Theme = theme; manager.Accent = AppAccent.Cobalt; manager.HighContrastOverride = hc; manager.LowEffects = LowEffectsMode.Off;
            var model = new MainViewModel(new McpProjectClient(client), new FakeDialogService());
            WpfContext.Complete(model.Session.ApplySharedStateAsync(client.State));
            var window = new MainWindow(model, false, new ApprovalServiceStub()) { Width = width, Height = height };
            string? output = Environment.GetEnvironmentVariable("TIA_WORKBENCH_RENDER_DIR");
            string suffix = (hc ? "hc" : theme.ToString().ToLowerInvariant()) + (Loc.Current.IsChinese ? "-zh" : "-en") + $"-{width}x{height}";
            try
            {
                var host = WorkbenchRenderTests.CreateHost(window, width, height);
                window.Content = host;
                WpfContext.ShowWindow(window);
                window.Navigate("Engineering");
                window.BlocksContent.LocateControlBlock(Assert.Single(model.Engineering.Blocks));
                var palette = OpenPalette(window);
                Assert.Contains(palette.ResultList.Items.Cast<CommandPaletteItem>(), i => i.Title == Loc.Current["Overlay.Picker"]);
                Capture("palette");
                UnifiedDesktopTests.ClickControl(palette.Dismiss);

                var popover = OpenSession(window);
                InvokeSessionButton(popover.Discover, model.Panel!.Refresh);
                popover.SessionList.SelectedIndex = 0;
                Assert.Equal(fixture.Key, model.Panel.Selected!.SessionKey);
                Capture("session-popover");
                UnifiedDesktopTests.ClickControl(window.SessionStatusButton);

                var candidates = new TiaOpenness.Contracts.Models.PortalProcessInfo[]
                {
                    new() { ProcessId = 123, Release = "19", ProjectPath = model.Session.OpenProjectFile, UiMode = "WithUserInterface" },
                    new() { ProcessId = 456, Release = "19", ProjectPath = @"D:\fixture\Mixer.ap19", UiMode = "WithoutUserInterface" },
                    new() { ProcessId = 789, Release = "19", UiMode = "WithUserInterface" },
                };
                WpfContext.Complete(model.Session.ApplySharedStateAsync(null));
                using var picker = new PortalProcessPickerViewModel(candidates,
                    _ => Task.FromResult<System.Collections.Generic.IReadOnlyList<TiaOpenness.Contracts.Models.PortalProcessInfo>>(candidates));
                picker.SelectedProcess = picker.Processes[0];
                Modal(new PortalProcessPicker(picker), "process-picker");
                Assert.Null(picker.ResultProcessId);
                WpfContext.Complete(model.Session.ApplySharedStateAsync(client.State));
                window.BlocksContent.LocateControlBlock(Assert.Single(model.Engineering.Blocks));
                foreach (var action in new[] { ConfirmationAction.SaveProject, ConfirmationAction.EndSharedSession })
                {
                    bool save = action == ConfirmationAction.SaveProject;
                    var message = save ? Loc.Current["Dialog.Save.Text"] : Loc.Current.T("Session.DisconnectWarning", "fixture AI");
                    var dialog = new WorkbenchMessageBox(message, Loc.Current[save ? "Dialog.Save.Caption" : "Pages.Disconnect"],
                        MessageBoxButton.OKCancel, MessageBoxImage.Warning, new(action));
                    Modal(dialog, save ? "confirm-save" : "confirm-disconnect");
                    var sheet = Assert.IsType<ProjectConfirmation>(dialog.Content);
                    Assert.Contains(save ? model.Session.OpenProjectFile : fixture.Key, sheet.FirstValue.Text);
                    if (!save) Assert.Contains("123", sheet.SecondValue.Text);
                    Assert.Equal(MessageBoxResult.Cancel, dialog.Result);
                }

                void Capture(string scene, FrameworkElement? popup = null)
                {
                    host.UpdateLayout();
                    Assert.Equal(width, host.ActualWidth); Assert.Equal(height, host.ActualHeight);
                    if (output == null) return;
                    var directory = Directory.CreateDirectory(Path.Combine(output, "part-c"));
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(popup == null ? DesktopCapture.Render(host) : DesktopCapture.Render(host, popup)));
                    using var stream = File.Create(Path.Combine(directory.FullName, $"{scene}-{suffix}.png"));
                    encoder.Save(stream);
                }
                void Modal(Window dialog, string scene)
                {
                    dialog.Owner = window;
                    Exception? failure = null;
                    dialog.ContentRendered += (_, _) =>
                    {
                        try
                        {
                            dialog.Left = window.Left + (window.ActualWidth - dialog.ActualWidth) / 2;
                            dialog.Top = window.Top + (window.ActualHeight - dialog.ActualHeight) / 2;
                            dialog.UpdateLayout();
                            Assert.True(window.OwnedDialogScrim.IsVisible);
                            var cancel = Assert.Single(DesktopCapture.Descendants<Button>(dialog), b => b.IsDefault);
                            Assert.True(cancel.IsCancel);
                            if (hc && dialog is PortalProcessPicker pickerWindow)
                            {
                                var item = (ListBoxItem)pickerWindow.ProcessList.ItemContainerGenerator.ContainerFromIndex(0);
                                Assert.True(item.IsSelected);
                                var pid = Assert.Single(DesktopCapture.Descendants<TextBlock>(item), label => label.Text == "123");
                                Assert.Same(Application.Current.FindResource("Primer.accent"), pid.Foreground);
                            }
                            var backdrop = Assert.IsType<Grid>(dialog.Content);
                            Capture(scene, backdrop);
                            var click = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Mouse.MouseDownEvent };
                            backdrop.RaiseEvent(click);
                            Assert.True(click.Handled);
                            Assert.False(dialog.IsVisible);
                        }
                        catch (Exception error) { failure = error; }
                        finally { if (dialog.IsVisible) dialog.Close(); }
                    };
                    WorkbenchMessageBox.ShowModal(dialog);
                    Assert.Null(failure);
                }
            }
            finally
            {
                window.Close();
                (manager.Theme, manager.Accent, manager.HighContrastOverride, manager.LowEffects) = saved;
            }
        });
    }
}
