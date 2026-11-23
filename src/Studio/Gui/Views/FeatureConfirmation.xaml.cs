using System.Collections.Generic;
using System.Windows;
using TiaOpenness.Gui.Controls;
using TiaOpenness.Gui.Localization;

namespace TiaOpenness.Gui.Views;

internal partial class FeatureConfirmation : Window
{
    internal FeatureConfirmation(string title, string body, string action, params KeyValuePair<string, string>[] fields)
    {
        InitializeComponent();
        Title = title;
        Body.Text = body;
        ApplyAction.Content = action;
        Fields.ItemsSource = fields;
        Loaded += (_, _) => CancelAction.Focus();
    }

    internal bool AlternativeSelected { get; private set; }
    internal void OfferAlternative(string label)
    {
        AlternativeAction.Content = label;
        AlternativeAction.Visibility = Visibility.Visible;
    }

    private void OnAlternative(object sender, RoutedEventArgs e)
    {
        AlternativeSelected = true;
        DialogResult = true;
    }

    internal bool Confirm(FrameworkElement source)
    {
        Owner = GetWindow(source);
        WorkbenchMessageBox.ShowModal(this);
        return DialogResult == true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
    private void OnApply(object sender, RoutedEventArgs e) => DialogResult = true;
}
