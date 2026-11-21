using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using TiaOpenness.Gui.Localization;
using TiaOpenness.Gui.Services;

namespace TiaOpenness.Gui;

public partial class MainWindow
{
    private readonly HashSet<(string Id, Uri? Endpoint)> _dismissedIssues = [];
    private EnvironmentIssue? _environmentIssue;

    private void UpdateEnvironmentBanner()
    {
        var rows = Features.EnvironmentRows;
        var issues = rows.Select(row => row.Check.Issue).OfType<EnvironmentIssue>().ToArray();
        if (!rows.Any(row => row.Check.Status == CheckStatus.Checking))
            _dismissedIssues.RemoveWhere(key => !issues.Any(issue => (issue.Id, issue.Endpoint) == key));
        _environmentIssue = issues.FirstOrDefault(issue => !_dismissedIssues.Contains((issue.Id, issue.Endpoint)));
        GlobalEnvironmentBanner.Visibility = _environmentIssue == null ? Visibility.Collapsed : Visibility.Visible;
        if (_environmentIssue is not { } current) return;
        bool port = current.Id == "port";
        GlobalEnvironmentBanner.SetResourceReference(Border.BackgroundProperty, port ? "Ant.colorErrorBg" : "Primer.warnBg");
        EnvironmentGlyph.SetResourceReference(TextBlock.ForegroundProperty, port ? "Ant.colorError" : "Primer.warn");
        EnvironmentGlyph.Text = port ? "✕" : "!";
        EnvironmentTitle.Text = current.Title.Resolve();
        EnvironmentDescription.Text = "· " + current.Description.Resolve();
        EnvironmentAction.Content = current.Action.Resolve();
    }

    private void OnDismissEnvironment(object sender, RoutedEventArgs e)
    {
        if (_environmentIssue is { } current) _dismissedIssues.Add((current.Id, current.Endpoint));
        UpdateEnvironmentBanner();
    }

    private async void OnEnvironmentAction(object sender, RoutedEventArgs e)
    {
        if (_environmentIssue is not { } issue) return;
        try
        {
            if (issue.Id == "membership")
            {
                NavigateGuarded("Environment");
                if (_page == "Environment")
                    Features.SelectedEnvironment = Features.EnvironmentRows.FirstOrDefault(row => row.Check.Id == issue.Id);
                return;
            }
            NavigateGuarded("Mcp");
            if (_page != "Mcp" || Configuration == null) return;
            await Configuration.ReviewNetworkIssue(issue.Id == "firewall", issue.Endpoint);
        }
        catch (Exception ex) { ShowToast(ViewModels.ToastKind.Error, LocalizedText.Literal(ex.Message), "environment-action"); }
    }
}
