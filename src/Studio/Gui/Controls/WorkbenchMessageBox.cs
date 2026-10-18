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

    internal WorkbenchMessageBox(string text, string caption, MessageBoxButton buttons, MessageBoxImage icon, Services.ConfirmationLabels? labels = null)
    {
        Title = caption;
        Language = System.Windows.Markup.XmlLanguage.GetLanguage(Loc.Current.Language == AppLanguage.Chinese ? "zh-CN" : "en-US");
        System.Windows.Automation.AutomationProperties.SetName(this, caption);
        Width = 420;
        SizeToContent = SizeToContent.Height;
        MaxHeight = Math.Max(360, SystemParameters.WorkArea.Height - 80);
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(StyleProperty, "Ui.Window");
        Result = buttons == MessageBoxButton.OK ? MessageBoxResult.OK : MessageBoxResult.Cancel;

        // Ant Modal.confirm: 20/24 content padding, a 22px status icon beside a fontSizeLG title, the body
        // aligned under the title 8px below it, and the footer 12px below the body.
        var panel = new DockPanel { Margin = new Thickness(24, 20, 24, 20) };
        var title = new DockPanel { Margin = new Thickness(0, 0, 0, 8), Background = System.Windows.Media.Brushes.Transparent };
        string? mark = icon switch
        {
            MessageBoxImage.Error => "Primer.AlertIcon.Error",
            MessageBoxImage.Warning => "Primer.AlertIcon.Warning",
            MessageBoxImage.Information or MessageBoxImage.Question => "Primer.AlertIcon.Info",
            _ => null,
        };
        if (mark != null)
        {
            var glyph = new System.Windows.Shapes.Path { Width = 22, Height = 22, Margin = new Thickness(0, 0, 12, 0) };
            glyph.SetResourceReference(StyleProperty, mark);
            DockPanel.SetDock(glyph, Dock.Left);
            title.Children.Add(glyph);
        }
        var heading = new TextBlock { Text = caption, FontWeight = FontWeights.SemiBold };
        heading.SetResourceReference(TextBlock.FontSizeProperty, "Ui.FontSize.DialogTitle");
        title.Children.Add(heading);
        title.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };
        DockPanel.SetDock(title, Dock.Top);
        panel.Children.Add(title);
        // Ant's footer: the buttons are added in visual order, safe choices first and the affirmative
        // action last, so it sits on the right and Tab follows what the user sees.
        var actions = new WrapPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        DockPanel.SetDock(actions, Dock.Bottom);
        panel.Children.Add(actions);
        var message = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, LineHeight = 22 };
        message.SetResourceReference(ForegroundProperty, "Primer.text");
        panel.Children.Add(new ScrollViewer { Content = message, MaxHeight = MaxHeight - 160, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(mark != null ? 34 : 0, 0, 0, 0) });
        var choices = labels?.Detach == true ? new[] { MessageBoxResult.OK, MessageBoxResult.No, MessageBoxResult.Cancel } : buttons switch
        {
            MessageBoxButton.OK => new[] { MessageBoxResult.OK },
            MessageBoxButton.OKCancel => new[] { MessageBoxResult.OK, MessageBoxResult.Cancel },
            MessageBoxButton.YesNo => new[] { MessageBoxResult.Yes, MessageBoxResult.No },
            _ => new[] { MessageBoxResult.Yes, MessageBoxResult.No, MessageBoxResult.Cancel },
        };
        foreach (var choice in Enumerable.Reverse(choices))
        {
            var label = choice == choices[0] && labels != null ? labels.Action switch
            {
                Services.ConfirmationAction.SaveProject => TrExtension.CreateBinding("Dialog.Save.Action"),
                Services.ConfirmationAction.EndSharedSession => TrExtension.CreateBinding("Dialog.Disconnect.Action"),
                Services.ConfirmationAction.OverwriteProject => TrExtension.CreateBinding("Dialog.Pull.Action"),
                _ => TrExtension.CreateBinding("Dialog.Import.Action"),
            } : choice == MessageBoxResult.No && labels?.Action == Services.ConfirmationAction.EndSharedSession
                ? TrExtension.CreateBinding("Dialog.Detach.Action")
                : choice == MessageBoxResult.No && labels?.Action == Services.ConfirmationAction.ImportBlocks
                ? TrExtension.CreateBinding("Dialog.Import.Safe") : choice switch
            {
                MessageBoxResult.OK => TrExtension.CreateBinding("Dialog.OK"), MessageBoxResult.Yes => TrExtension.CreateBinding("Dialog.Yes"),
                MessageBoxResult.No => TrExtension.CreateBinding("Dialog.No"), _ => TrExtension.CreateBinding("Dialog.Cancel"),
            };
            var safe = buttons == MessageBoxButton.OK ? MessageBoxResult.OK : buttons == MessageBoxButton.YesNo ? MessageBoxResult.No : MessageBoxResult.Cancel;
            var button = new Button { MinWidth = 84, Margin = new Thickness(actions.Children.Count == 0 ? 0 : 8, 0, 0, 0), IsDefault = choice == safe, IsCancel = choice == safe };
            button.SetBinding(ContentControl.ContentProperty, label);
            button.SetBinding(System.Windows.Automation.AutomationProperties.NameProperty, TrExtension.NameBinding(label));
            // Ant: the affirmative action is the primary button, or the danger button when the dialog warns;
            // a lone acknowledgement is primary, and every other choice (including the safe default) is default.
            string style = choice == choices[0] && (choice != safe || buttons == MessageBoxButton.OK)
                ? icon == MessageBoxImage.Warning && choice != safe ? "Primer.WarningAction" : "Primer.Run"
                : "Primer.Secondary";
            button.SetResourceReference(StyleProperty, style);
            button.Click += (_, _) => { Result = choice; Close(); };
            actions.Children.Add(button);
            if (choice == safe) Loaded += (_, _) => button.Focus();
        }
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            Result = buttons == MessageBoxButton.YesNo ? MessageBoxResult.No : buttons == MessageBoxButton.OK ? MessageBoxResult.OK : MessageBoxResult.Cancel;
            e.Handled = true; Close();
        };
        var card = new Border { Child = panel, Padding = new Thickness(0) };
        card.SetResourceReference(StyleProperty, "Primer.Dialog");
        Content = card;
    }

    internal static MessageBoxResult Show(string text, string caption, MessageBoxButton buttons, MessageBoxImage icon)
        => Show(Application.Current?.Windows.OfType<Window>().FirstOrDefault(window => window.IsActive), text, caption, buttons, icon);

    internal static MessageBoxResult Show(string text, string caption, MessageBoxButton buttons, MessageBoxImage icon, Services.ConfirmationLabels labels)
        => Show(Application.Current?.Windows.OfType<Window>().FirstOrDefault(window => window.IsActive), text, caption, buttons, icon, labels);

    internal static MessageBoxResult Show(Window? owner, string text, string caption, MessageBoxButton buttons, MessageBoxImage icon, Services.ConfirmationLabels? labels = null)
    {
        var dialog = new WorkbenchMessageBox(text, caption, buttons, icon, labels);
        if (owner?.IsVisible == true) dialog.Owner = owner;
        else dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        dialog.ShowDialog();
        return dialog.Result;
    }
}
