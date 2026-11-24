using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using TiaOpenness.Gui.Localization;
using TiaOpenness.Gui.Services;

namespace TiaOpenness.Gui;

public partial class MainWindow
{
    private readonly HashSet<(string Id, Uri? Endpoint)> _dismissedIssues = [];
    private (string Id, Uri? Endpoint)? _environmentIssue;

    private void UpdateEnvironmentAlert(bool findingsRefreshed = false)
    {
        var rows = Features.EnvironmentRows;
        var issues = rows.Select(row => row.Check.Issue).OfType<EnvironmentIssue>().ToArray();
        if (!rows.Any(row => row.Check.Status == CheckStatus.Checking))
            _dismissedIssues.RemoveWhere(key => !issues.Any(issue => (issue.Id, issue.Endpoint) == key));
        var issue = issues.FirstOrDefault(item => !_dismissedIssues.Contains((item.Id, item.Endpoint)));
        (string Id, Uri? Endpoint)? key = issue == null ? null : (issue.Id, issue.Endpoint);
        if (key == _environmentIssue)
        {
            if (findingsRefreshed && issue != null) _toasts.Refresh("env-alert");
            return;
        }
        _environmentIssue = key;
        if (issue == null) { _toasts.Remove("env-alert"); return; }

        ShowToast(issue.Id == "port" ? ViewModels.ToastKind.Error : ViewModels.ToastKind.Warning, issue.Description, "env-alert",
            () => _ = ReviewEnvironmentIssue(issue), issue.Title, sticky: true, actionLabel: issue.Action, onDismiss: () =>
            {
                _dismissedIssues.Add((issue.Id, issue.Endpoint));
                UpdateEnvironmentAlert();
            });
    }

    internal async Task ReviewEnvironmentIssue(EnvironmentIssue issue)
    {
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
