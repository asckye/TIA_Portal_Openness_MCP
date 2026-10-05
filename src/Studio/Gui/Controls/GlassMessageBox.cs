using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TiaOpenness.Gui.Localization;

namespace TiaOpenness.Gui.Controls;

internal sealed class GlassMessageBox : Window
{
    internal MessageBoxResult Result { get; private set; }

    internal GlassMessageBox(string text, string caption, MessageBoxButton buttons, MessageBoxImage icon)
    {
        Title = caption;
        Width = 560;
        SizeToContent = SizeToContent.Height;
        MaxHeight = Math.Max(360, SystemParameters.WorkArea.Height - 80);
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(StyleProperty, "Ui.Window");
        Result = buttons == MessageBoxButton.OK ? MessageBoxResult.OK : MessageBoxResult.Cancel;

        var panel = new DockPanel { Margin = new Thickness(24) };
        var heading = new TextBlock { Text = caption, FontSize = 18, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 18) };
        heading.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };
        DockPanel.SetDock(heading, Dock.Top);
        panel.Children.Add(heading);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        DockPanel.SetDock(actions, Dock.Bottom);
        panel.Children.Add(actions);
        var message = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, LineHeight = 22 };
        message.SetResourceReference(ForegroundProperty, icon == MessageBoxImage.Error ? "Ui.Red" : "Ui.Label");
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
            button.SetResourceReference(StyleProperty, choice == choices[0] ? "Glass.Primary" : "Glass.Button");
            button.Click += (_, _) => { Result = choice; Close(); };
            actions.Children.Add(button);
        }
        var card = new Border { Child = panel, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12) };
        card.SetResourceReference(Border.BackgroundProperty, "Ui.CardBackground");
        card.SetResourceReference(Border.BorderBrushProperty, "Ui.Separator");
        Content = card;
    }

    internal static MessageBoxResult Show(string text, string caption, MessageBoxButton buttons, MessageBoxImage icon)
        => Show(Application.Current?.Windows.OfType<Window>().FirstOrDefault(window => window.IsActive), text, caption, buttons, icon);

    internal static MessageBoxResult Show(Window? owner, string text, string caption, MessageBoxButton buttons, MessageBoxImage icon)
    {
        var dialog = new GlassMessageBox(text, caption, buttons, icon);
        if (owner?.IsVisible == true) dialog.Owner = owner;
        else dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        dialog.ShowDialog();
        return dialog.Result;
    }
}
