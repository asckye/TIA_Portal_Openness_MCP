using System;
using System.Windows;
using System.Windows.Controls;

namespace TiaOpenness.Gui.Views;

public partial class EngineeringSideView : UserControl
{
    public EngineeringSideView() => InitializeComponent();
    internal event EventHandler? InspectionRulesRequested;
    private void OnInspectionRules(object sender, RoutedEventArgs e) => InspectionRulesRequested?.Invoke(this, EventArgs.Empty);
}
