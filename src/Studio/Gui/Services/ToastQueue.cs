using System;
using System.Collections.Generic;
using TiaOpenness.Gui.Localization;
using TiaOpenness.Gui.ViewModels;

namespace TiaOpenness.Gui.Services;

internal sealed record ToastNotice(string Source, ToastKind Kind, LocalizedText Text, LocalizedText Title,
    Action? Action, string ActionKey, bool ActionShowsDetails = false)
{
    internal string FullText => Title.Resolve() is { Length: > 0 } title ? title + " · " + Text.Resolve() : Text.Resolve();
}

/// <summary>Dispatcher-owned FIFO. Only time actually visible and unpaused counts toward dismissal.</summary>
internal sealed class ToastQueue(Func<TimeSpan> now)
{
    private readonly List<ToastNotice> pending = [];
    private TimeSpan remaining, started;
    private bool hovered, modal;
    private ToastNotice? current;
    internal ToastNotice? Visible => modal ? null : current;
    internal TimeSpan? Delay => Visible != null && !hovered ? remaining - (now() - started) : null;
    internal event Action? Changed;

    internal void Show(ToastNotice notice)
    {
        if (current == null || current.Source == notice.Source) Start(notice);
        else
        {
            int index = pending.FindIndex(item => item.Source == notice.Source);
            if (index < 0) pending.Add(notice);
            else pending[index] = notice;
        }
    }

    private void Start(ToastNotice? notice)
    {
        current = notice;
        remaining = TimeSpan.FromSeconds(notice?.Kind == ToastKind.Error ? 8 : 4);
        started = now();
        Changed?.Invoke();
    }

    internal void Dismiss()
    {
        if (Visible == null) return;
        var next = pending.Count > 0 ? pending[0] : null;
        if (next != null) pending.RemoveAt(0);
        Start(next);
    }

    internal void Tick()
    {
        if (Delay is { } delay && delay <= TimeSpan.Zero) Dismiss();
        else Changed?.Invoke();
    }

    internal void SetPresentation(bool isModal, bool isHovered)
    {
        if (modal == isModal && hovered == isHovered) return;
        if (Delay is { } delay) remaining = delay;
        started = now();
        modal = isModal;
        hovered = isHovered;
        Changed?.Invoke();
    }
}
