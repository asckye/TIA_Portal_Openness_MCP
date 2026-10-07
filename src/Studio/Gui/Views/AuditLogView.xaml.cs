using System;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using TiaOpenness.Gui.ViewModels;

namespace TiaOpenness.Gui.Views;

public partial class AuditLogView : UserControl
{
    public AuditLogView() { InitializeComponent(); DataContext = null; DataContextChanged += OnModelChanged; }
    private FeaturePagesViewModel Model => (FeaturePagesViewModel)DataContext;
    private void OnModelChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is FeaturePagesViewModel old) old.PropertyChanged -= OnChanged;
        if (e.NewValue is FeaturePagesViewModel model) { model.PropertyChanged += OnChanged; UpdateRetention(); }
    }
    private void OnChanged(object? sender, PropertyChangedEventArgs e)
    { if (e.PropertyName is nameof(Model.RetentionSize) or nameof(Model.RetentionCopies)) UpdateRetention(); }
    private void UpdateRetention()
    {
        foreach (var button in new[] { Size50, Size5, Size10 }) button.IsChecked = int.Parse((string)button.Tag) == Model.Audit.FileSizeMb;
        foreach (var button in new[] { Copies3, Copies5, Copies10 }) button.IsChecked = int.Parse((string)button.Tag) == Model.Audit.Copies;
    }
    private void OnVerify(object sender, RoutedEventArgs e) => Model.Verify();
    private void OnJump(object sender, RoutedEventArgs e)
    {
        var row = Model.JumpToBreak();
        if (row != null) AuditList.ScrollIntoView(row);
    }
    private async void OnOlder(object sender, RoutedEventArgs e) { if (Model.Audit is Services.AuditLogService audit) await audit.OlderAsync(); }
    private async void OnLatest(object sender, RoutedEventArgs e) { if (Model.Audit is Services.AuditLogService audit) await audit.LatestAsync(); }
    private void OnSize(object sender, RoutedEventArgs e) => Model.SetRetention(int.Parse((string)((RadioButton)sender).Tag), true);
    private void OnCopies(object sender, RoutedEventArgs e) => Model.SetRetention(int.Parse((string)((RadioButton)sender).Tag), false);
    private void OnOpenFolder(object sender, RoutedEventArgs e) => Model.Run(Model.Audit.OpenFolder);
}
