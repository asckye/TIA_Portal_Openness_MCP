using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TiaOpenness.Gui.ViewModels;

namespace TiaOpenness.Gui.Controls;

public partial class CommandPalette : UserControl
{
    private readonly Action _close;
    internal System.Windows.Shapes.Rectangle Backdrop => Scrim.Backdrop;

    internal CommandPalette(CommandPaletteViewModel model, Action close)
    {
        _close = close;
        InitializeComponent();
        DataContext = model;
        ResultList.SelectedIndex = 0;
    }

    private void OnSearch(object sender, TextChangedEventArgs e)
    {
        ((CommandPaletteViewModel)DataContext).Search(SearchBox.Text);
        ResultList.SelectedIndex = 0;
        if (ResultList.SelectedItem != null) ResultList.ScrollIntoView(ResultList.SelectedItem);
    }

    private void OnPaletteKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !Dismiss.IsKeyboardFocusWithin)
        {
            e.Handled = true;
            Choose(ResultList.SelectedItem as CommandPaletteItem);
        }
        else if (SearchBox.IsKeyboardFocusWithin && e.Key is Key.Down or Key.Up && ResultList.HasItems)
        {
            e.Handled = true;
            ((CommandPaletteViewModel)DataContext).ShowKeyboardSelection = true;
            ResultList.SelectedIndex = Math.Clamp(ResultList.SelectedIndex + (e.Key == Key.Down ? 1 : -1), 0, ResultList.Items.Count - 1);
            ResultList.ScrollIntoView(ResultList.SelectedItem);
        }
    }

    private void Choose(CommandPaletteItem? item)
    {
        if (item == null) return;
        _close();
        item.Execute();
    }

    private void OnChoose(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        Choose(((ListBoxItem)sender).DataContext as CommandPaletteItem);
    }

    private void OnBackdrop(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource != Scrim) return;
        e.Handled = true;
        _close();
    }

    private void OnDismiss(object sender, RoutedEventArgs e) => _close();
}
