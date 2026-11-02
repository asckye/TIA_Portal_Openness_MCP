using System;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using TiaOpenness.Gui.ViewModels;

namespace TiaOpenness.Gui.Views;

public partial class AuditLogView : UserControl
{
    public AuditLogView() { InitializeComponent(); DataContext = null; }
    private FeaturePagesViewModel Model => (FeaturePagesViewModel)DataContext;
    private void OnVerify(object sender, RoutedEventArgs e) => Model.Verify();
    private void OnJump(object sender, RoutedEventArgs e)
    {
        var row = Model.JumpToBreak();
        if (row != null) AuditList.ScrollIntoView(row);
    }
    private async void OnOlder(object sender, RoutedEventArgs e) { if (Model.Audit is Services.AuditLogService audit) await audit.OlderAsync(); }
    private async void OnLatest(object sender, RoutedEventArgs e) { if (Model.Audit is Services.AuditLogService audit) await audit.LatestAsync(); }
}
