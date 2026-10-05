using System;
using System.Windows.Forms;

namespace TiaOpenness.Gui.Services;

public interface IApprovalNotification : IDisposable
{
    void Show(string title, string message, Action activate);
}

/// <summary>The native notification is created only for an incoming request while minimised.</summary>
public sealed class WindowsApprovalNotification : IApprovalNotification
{
    private NotifyIcon? _icon;
    private Action? _activate;
    public void Show(string title, string message, Action activate)
    {
        if (_icon == null)
        {
            _icon = new NotifyIcon { Icon = System.Drawing.SystemIcons.Warning, Text = "TIA Workbench", Visible = true };
            _icon.BalloonTipClicked += OnActivate;
            _icon.Click += OnActivate;
        }
        _activate = activate;
        _icon.ShowBalloonTip(10000, title, message, ToolTipIcon.Warning);
    }
    private void OnActivate(object? sender, EventArgs e) => _activate?.Invoke();
    public void Dispose()
    {
        if (_icon != null) { _icon.BalloonTipClicked -= OnActivate; _icon.Click -= OnActivate; _icon.Dispose(); }
        _icon = null; _activate = null;
    }
}
