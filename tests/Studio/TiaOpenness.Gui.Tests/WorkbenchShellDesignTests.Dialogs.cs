using System;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using TiaOpenness.Gui.Controls;
using TiaOpenness.Gui.Localization;
using TiaOpenness.Gui.Services;
using TiaOpenness.Gui.ViewModels;
using Xunit;

namespace TiaOpenness.Gui.Tests;

public sealed partial class WorkbenchShellDesignTests
{
    private static void CaptureNativeModals(MainWindow window, FrameworkElement host, string? folder, string suffix)
    {
        var loc = Loc.Current;
        foreach (var (scene, page, message, caption, labels) in new (string, string, string, string, ConfirmationLabels?)[]
        {
            ("confirm-save", "Engineering", loc["Dialog.Save.Text"], loc["Dialog.Save.Caption"], new(ConfirmationAction.SaveProject)),
            ("confirm-disconnect", "Engineering", loc.T("Session.DisconnectWarning", "MCP"), loc["Pages.Disconnect"], new(ConfirmationAction.EndSharedSession)),
            ("confirm-clients", "Clients", loc["Config.WriteClientsPrompt"] + @"C:\fixture\config.toml", loc["Config.Write"], null),
            ("confirm-clear-log", "Log", loc["Pages.ClearLogPrompt"], loc["Pages.ClearLog"], null),
            ("confirm-safety", "Settings", loc.T("Settings.ConfirmReplaceTargets", @"C:\fixture\targets.json"), loc["Settings.HumanOnly"], null),
        })
        {
            window.Navigate(page);
            if (page == "Settings") window.SettingsContent.ShowSection("AI");
            var icon = page == "Clients" ? MessageBoxImage.Information : MessageBoxImage.Warning;
            var dialog = new WorkbenchMessageBox(message, caption, MessageBoxButton.OKCancel, icon, labels);
            Capture(dialog, scene);
            Assert.Equal(MessageBoxResult.Cancel, dialog.Result);
        }
        window.Navigate("Engineering");
        using var picker = new PortalProcessPickerViewModel([new() { ProcessId = 37, Release = "21", ProjectPath = @"C:\fixture\example.ap21" }]);
        Capture(new PortalProcessPicker(picker), "process-picker");
        Assert.Null(picker.ResultProcessId);

        void Capture(Window dialog, string scene)
        {
            dialog.Owner = window;
            Exception? renderError = null;
            dialog.ContentRendered += (_, _) =>
            {
                try
                {
                    // The test desktop keeps all HWNDs offscreen. Position this real modal relative to its real owner.
                    dialog.Left = window.Left + (window.ActualWidth - dialog.ActualWidth) / 2;
                    dialog.Top = window.Top + (window.ActualHeight - dialog.ActualHeight) / 2;
                    host.UpdateLayout(); dialog.UpdateLayout();
                    Assert.Equal(Visibility.Visible, window.OwnedDialogScrim.Visibility);
                    Assert.Equal(5, window.OwnedDialogScrim.BlurRadius);
                    Assert.Same(window.Root, window.OwnedDialogScrim.Source);
                    Assert.Equal(Themes.ThemeManager.Current.EffectivelyHighContrast, window.OwnedDialogScrim.Backdrop.Effect == null);
                    if (folder == null) return;
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(DesktopCapture.Render(host, (FrameworkElement)dialog.Content)));
                    using var stream = File.Create(Path.Combine(folder, "design", $"{scene}-{suffix}.png"));
                    encoder.Save(stream);
                }
                catch (Exception ex) { renderError = ex; }
                finally { dialog.Close(); }
            };
            WorkbenchMessageBox.ShowModal(dialog);
            Assert.Null(renderError);
            Assert.Equal(Visibility.Collapsed, window.OwnedDialogScrim.Visibility);
        }
    }
}
