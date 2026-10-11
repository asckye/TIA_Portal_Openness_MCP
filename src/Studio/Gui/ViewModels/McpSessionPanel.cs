using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using TiaOpenness.Gui.Common;
using TiaOpenness.Gui.Localization;
using TiaOpenness.Gui.Services;

namespace TiaOpenness.Gui.ViewModels;

public sealed class McpSessionPanel : ObservableObject, IDisposable
{
    private readonly IMcpSessionClient client;
    private readonly Func<string> release;
    private readonly WorkbenchActivity activity;
    private readonly Func<JsonObject?, Task> apply;
    private readonly Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
    private readonly CancellationTokenSource lifetime = new();
    private JsonObject? pending, state;
    private int queued;
    private bool disposed;
    private OperatorSession? selected;
    public ObservableCollection<OperatorSession> Sessions { get; } = [];
    public OperatorSession? Selected { get => selected; set { if (Set(ref selected, value)) Attach.RaiseCanExecuteChanged(); } }
    public bool Attached => client.SessionKey != null;
    public string? SessionKey => client.SessionKey;
    public string Participants => ((string?)state?["participants"]?["mcpClient"] ?? "MCP") + " · " + Loc.Current["Session.Unverified"]
        + " · Workbench: " + ((int?)state?["participants"]?["workbench"] ?? 0);
    public string Status => state == null || !Attached ? Loc.Current["Session.SelectHint"] :
        $"{state["worker"]?["state"]} · PID {state["worker"]?["pid"]} · generation {state["worker"]?["generation"]} · {state["binding"]?["projectFile"]}";
    public string Lane => state?["lane"] is JsonObject lane ? $"{lane["actor"]}: {lane["tool"]} · {lane["sinceUtc"]}" : "";
    public bool Locked => (bool?)state?["sessionLocked"] == true;
    public string Ended => state?["endedBy"] == null ? "" : $"endedBy: {state["endedBy"]} · {state["endReason"]}. "
        + ((string?)state?["worker"]?["state"] == "disconnected" ? Loc.Current["Session.ReconnectHint"] : "");
    public bool ApprovalEnabled => (bool?)state?["approvalEnabled"] != false;
    public AsyncCommand Refresh { get; }
    public AsyncCommand Attach { get; }
    public AsyncCommand Create { get; }
    public AsyncCommand Detach { get; }
    public System.Windows.Input.ICommand Cancel { get; }
    public McpSessionPanel(IMcpSessionClient client, Func<string> release, WorkbenchActivity activity, Func<JsonObject?, Task> apply)
    {
        this.client = client; this.release = release; this.activity = activity; this.apply = apply;
        state = client.State;
        Cancel = new AsyncCommand(() => { client.CancelPending(); return Task.CompletedTask; });
        Refresh = new(() => Guard(async () => { Selected = null; Sessions.Clear(); foreach (var session in await client.DiscoverAsync(release(), lifetime.Token)) Sessions.Add(session); }), () => !activity.Busy);
        Attach = new(() => Guard(async () => { await client.AttachAsync(Selected!, lifetime.Token); await apply(client.State); Raise(null); }), () => !activity.Busy && Selected != null);
        Create = new(() => Guard(async () => { await client.CreateAsync(release(), lifetime.Token); await apply(client.State); Raise(null); }), () => !activity.Busy);
        Detach = new(() => Guard(async () => { await client.DetachAsync(); await apply(null); Raise(null); }), () => Attached);
        client.StateChanged += Changed; client.Detached += Detached;
        activity.PropertyChanged += ActivityChanged;
        Loc.Current.LanguageChanged += LanguageChanged;
    }
    private Task Guard(Func<Task> action) => activity.Guarded("Session.Working", action);
    private void Changed(object? sender, JsonObject snapshot)
    {
        Interlocked.Exchange(ref pending, snapshot);
        if (Interlocked.Exchange(ref queued, 1) != 0) return;
        dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            Interlocked.Exchange(ref queued, 0);
            if (disposed) return;
            state = Interlocked.Exchange(ref pending, null);
            Raise(null); Detach.RaiseCanExecuteChanged();
            if (state != null) _ = apply(state);
        }));
    }
    private void Detached(object? sender, EventArgs args) => dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
    { if (!disposed && client.SessionKey == null) { Raise(null); Detach.RaiseCanExecuteChanged(); _ = apply(null); } }));
    private void LanguageChanged(object? sender, EventArgs args) => Raise(null);
    private void ActivityChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName != nameof(WorkbenchActivity.Busy)) return;
        Refresh.RaiseCanExecuteChanged(); Attach.RaiseCanExecuteChanged(); Create.RaiseCanExecuteChanged();
    }
    public void Dispose()
    { disposed = true; lifetime.Cancel(); client.StateChanged -= Changed; client.Detached -= Detached; activity.PropertyChanged -= ActivityChanged; Loc.Current.LanguageChanged -= LanguageChanged; lifetime.Dispose(); }
}
