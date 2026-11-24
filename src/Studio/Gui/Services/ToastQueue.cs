using System;
using System.Collections.Generic;
using System.Linq;
using TiaOpenness.Gui.Localization;
using TiaOpenness.Gui.ViewModels;

namespace TiaOpenness.Gui.Services;

internal sealed record ToastNotice(string Source, ToastKind Kind, LocalizedText Text, LocalizedText Title,
    Action? Action, string ActionKey, bool ActionShowsDetails = false, bool Sticky = false,
    Action? SecondAction = null, LocalizedText SecondActionKey = default, LocalizedText ActionLabel = default, Action? OnDismiss = null)
{
    internal bool MayDisplaceSticky { get; init; }
    internal string? KeepNewestSource { get; init; }
    internal string FullText => Title.Resolve() is { Length: > 0 } title ? title + " · " + Text.Resolve() : Text.Resolve();
}

/// <summary>Time advances for all visible transient cards together while the stack is unpaused.</summary>
internal sealed class ToastQueue(Func<TimeSpan> now)
{
    private sealed record Entry(ToastNotice Notice, TimeSpan Deadline);
    private readonly List<Entry> cards = [];
    private readonly List<Entry> waiting = [];
    private TimeSpan pausedAt, pausedDuration;
    private bool hovered, modal;
    private TimeSpan Clock => (hovered || modal ? pausedAt : now()) - pausedDuration;
    internal IEnumerable<ToastNotice> Cards => modal ? [] : cards.Select(entry => entry.Notice);
    internal ToastNotice? Visible => modal || cards.Count == 0 ? null : cards[^1].Notice;
    internal TimeSpan? Delay => hovered || modal ? null : cards.Where(entry => !entry.Notice.Sticky)
        .Select(entry => (TimeSpan?)(entry.Deadline - Clock)).Min();
    internal event Action? Changed;
    internal event Action<ToastNotice>? ErrorDropped;
    internal event Action<ToastNotice>? StickyDisplaced;
    internal bool HasSource(string source) => cards.Concat(waiting).Any(entry => entry.Notice.Source == source);
    internal bool Contains(ToastNotice notice) => cards.Concat(waiting).Any(entry => ReferenceEquals(entry.Notice, notice));

    internal void Show(ToastNotice notice)
    {
        waiting.RemoveAll(entry => entry.Notice.Source == notice.Source);
        cards.RemoveAll(entry => entry.Notice.Source == notice.Source);
        if (cards.Count == 3)
        {
            int oldest = cards.FindIndex(entry => !entry.Notice.Sticky);
            if (oldest < 0 && !notice.Sticky && !notice.MayDisplaceSticky)
            {
                if (notice.Kind == ToastKind.Error) ErrorDropped?.Invoke(notice);
                return;
            }
            if (oldest < 0)
            {
                waiting.Add(cards[0]);
                StickyDisplaced?.Invoke(cards[0].Notice);
                oldest = 0;
            }
            cards.RemoveAt(oldest);
        }
        int insertBefore = cards.FindIndex(entry => entry.Notice.Source == notice.KeepNewestSource);
        cards.Insert(insertBefore < 0 ? cards.Count : insertBefore, new(notice, Clock + TimeSpan.FromSeconds(notice.Kind == ToastKind.Error ? 8 : 4)));
        Changed?.Invoke();
    }

    internal void Refresh(string source)
    {
        if (waiting.Find(entry => entry.Notice.Source == source) is { } displaced) Show(displaced.Notice);
    }

    internal void Remove(string source)
    {
        int removed = waiting.RemoveAll(entry => entry.Notice.Source == source);
        if (cards.RemoveAll(entry => entry.Notice.Source == source) + removed == 0) return;
        RestoreWaiting();
        Changed?.Invoke();
    }

    internal void Dismiss() { if (Visible is { } newest) Dismiss(newest); }
    internal void Dismiss(ToastNotice notice)
    {
        if (cards.RemoveAll(entry => ReferenceEquals(entry.Notice, notice)) == 0) return;
        RestoreWaiting();
        notice.OnDismiss?.Invoke();
        Changed?.Invoke();
    }

    // Restore in stack order. The newest card keeps its Escape and shortcut targets.
    private void RestoreWaiting()
    {
        while (cards.Count < 3 && waiting.Count > 0)
        {
            cards.Insert(0, waiting[^1]);
            waiting.RemoveAt(waiting.Count - 1);
        }
    }

    internal void Tick()
    {
        if (!hovered && !modal) cards.RemoveAll(entry => !entry.Notice.Sticky && entry.Deadline <= Clock);
        RestoreWaiting();
        Changed?.Invoke();
    }

    internal void SetPresentation(bool isModal, bool isHovered)
    {
        if (modal == isModal && hovered == isHovered) return;
        if (!modal && !hovered) pausedAt = now();
        if (!isModal && !isHovered) pausedDuration += now() - pausedAt;
        modal = isModal;
        hovered = isHovered;
        Changed?.Invoke();
    }
}
