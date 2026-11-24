using System;
using System.Windows;
using System.Windows.Controls;

namespace TiaOpenness.Gui.Views;

public partial class EngineeringSideView : UserControl
{
    public EngineeringSideView() => InitializeComponent();
    private void OnResultsSizeChanged(object sender, SizeChangedEventArgs e)
    {
        bool stacked = e.NewSize.Width < 780;
        Gutter.Width = new GridLength(stacked ? 0 : 16);
        SecondColumn.Width = stacked ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        Grid.SetColumn(InspectionPane, stacked ? 0 : 2);
        Grid.SetRow(InspectionPane, stacked ? 1 : 0);
        InspectionPane.Margin = stacked ? new Thickness(0, 16, 0, 0) : new Thickness(0);
    }

    internal event EventHandler? InspectionRulesRequested;
    private void OnInspectionRules(object sender, RoutedEventArgs e) => InspectionRulesRequested?.Invoke(this, EventArgs.Empty);
}
