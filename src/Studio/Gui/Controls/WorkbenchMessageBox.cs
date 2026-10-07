using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TiaOpenness.Gui.Localization;

namespace TiaOpenness.Gui.Controls;

internal sealed class WorkbenchMessageBox : Window
{
    internal MessageBoxResult Result { get; private set; }

    internal WorkbenchMessageBox(string text, string caption, MessageBoxButton buttons, MessageBoxImage icon)
    {
        Title = caption;
        Width = 420;
        SizeToContent = SizeToContent.Height;
        MaxHeight = Math.Max(360, SystemParameters.WorkArea.Height - 80);
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(StyleProperty, "Ui.Window");
        Result = buttons == MessageBoxButton.OK ? MessageBoxResult.OK : MessageBoxResult.Cancel;

        var panel = new DockPanel { Margin = new Thickness(24) };
        var heading = new TextBlock { Text = caption, FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 12) };
        heading.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };
        DockPanel.SetDock(heading, Dock.Top);
        panel.Children.Add(heading);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        DockPanel.SetDock(actions, Dock.Bottom);
        panel.Children.Add(actions);
        var message = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, LineHeight = 22 };
        message.SetResourceReference(ForegroundProperty, icon == MessageBoxImage.Error ? "Primer.diffRed" : "Primer.text");
        panel.Children.Add(new ScrollViewer { Content = message, MaxHeight = MaxHeight - 160, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        var choices = buttons switch
        {
            MessageBoxButton.OK => new[] { MessageBoxResult.OK },
            MessageBoxButton.OKCancel => new[] { MessageBoxResult.OK, MessageBoxResult.Cancel },
            MessageBoxButton.YesNo => new[] { MessageBoxResult.Yes, MessageBoxResult.No },
            _ => new[] { MessageBoxResult.Yes, MessageBoxResult.No, MessageBoxResult.Cancel },
        };
        foreach (var choice in choices)
        {
            var label = choice switch
            {
                MessageBoxResult.OK => TrExtension.CreateBinding("Dialog.OK"), MessageBoxResult.Yes => TrExtension.CreateBinding("Dialog.Yes"),
                MessageBoxResult.No => TrExtension.CreateBinding("Dialog.No"), _ => TrExtension.CreateBinding("Dialog.Cancel"),
            };
            var button = new Button { MinWidth = 84, Margin = new Thickness(8, 0, 0, 0), IsDefault = choice == choices[0], IsCancel = choice == MessageBoxResult.Cancel || buttons == MessageBoxButton.OK };
            button.SetBinding(ContentControl.ContentProperty, label);
            button.SetResourceReference(StyleProperty, choice == choices[0] && icon == MessageBoxImage.Warning ? "Primer.WarningAction" : "Primer.Secondary");
            button.Click += (_, _) => { Result = choice; Close(); };
            actions.Children.Add(button);
        }
        var card = new Border { Child = panel, Padding = new Thickness(0) };
        card.SetResourceReference(StyleProperty, "Primer.Dialog");
        Content = card;
    }

    internal static MessageBoxResult Show(string text, string caption, MessageBoxButton buttons, MessageBoxImage icon)
        => Show(Application.Current?.Windows.OfType<Window>().FirstOrDefault(window => window.IsActive), text, caption, buttons, icon);

    internal static MessageBoxResult Show(Window? owner, string text, string caption, MessageBoxButton buttons, MessageBoxImage icon)
    {
        var dialog = new WorkbenchMessageBox(text, caption, buttons, icon);
        if (owner?.IsVisible == true) dialog.Owner = owner;
        else dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        dialog.ShowDialog();
        return dialog.Result;
    }
}
