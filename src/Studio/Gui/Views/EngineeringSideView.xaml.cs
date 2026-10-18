using System;
using System.Windows;
using System.Windows.Controls;
using TiaOpenness.Gui.ViewModels;

namespace TiaOpenness.Gui.Views;

public partial class EngineeringSideView : UserControl
{
    public EngineeringSideView() => InitializeComponent();
    private MainViewModel Model => (MainViewModel)DataContext;
    internal event EventHandler? EnvironmentRequested;
    internal event EventHandler? InspectionRulesRequested;
    private void OnClearLog(object sender, RoutedEventArgs e) => Model.Activity.ClearLog();
    private void OnEnvironment(object sender, RoutedEventArgs e) => EnvironmentRequested?.Invoke(this, EventArgs.Empty);
    private void OnInspectionRules(object sender, RoutedEventArgs e) => InspectionRulesRequested?.Invoke(this, EventArgs.Empty);
}
