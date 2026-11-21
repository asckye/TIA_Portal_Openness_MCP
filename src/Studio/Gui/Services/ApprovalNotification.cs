using System;
using System.Windows.Forms;

namespace TiaOpenness.Gui.Services;

public interface IApprovalNotification : IDisposable
{
    public void Show(string title, string message, Action activate);
    public void Hide();
}

/// <summary>The native notification is created only for an incoming request while minimised.</summary>
public sealed class WindowsApprovalNotification : IApprovalNotification
{
    private readonly Action<NotifyIcon> _showBalloon;
    private NotifyIcon? _icon;
    private Action? _activate;

    public WindowsApprovalNotification() : this(icon => { icon.Visible = true; icon.ShowBalloonTip(10000); }) { }
    internal WindowsApprovalNotification(Action<NotifyIcon> showBalloon) => _showBalloon = showBalloon;

    public void Show(string title, string message, Action activate)
    {
        Hide();
        _icon = new NotifyIcon { Icon = System.Drawing.SystemIcons.Warning, Text = Localization.Loc.Current["Common.WorkbenchName"],
            BalloonTipTitle = title, BalloonTipText = message, BalloonTipIcon = ToolTipIcon.Warning };
        _activate = activate;
        _icon.Click += OnActivate;
        _icon.BalloonTipClicked += OnActivate;
        _showBalloon(_icon);
    }

    private void OnActivate(object? sender, EventArgs e) => _activate?.Invoke();

    public void Hide()
    {
        if (_icon == null) return;
        _icon.Click -= OnActivate;
        _icon.BalloonTipClicked -= OnActivate;
        _icon.Dispose();
        _icon = null;
        _activate = null;
    }

    public void Dispose() => Hide();
}
