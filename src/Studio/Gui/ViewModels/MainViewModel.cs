using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using TiaOpenness.Client;
using TiaOpenness.Gui.Common;
using TiaOpenness.Gui.Services;

namespace TiaOpenness.Gui.ViewModels;

/// <summary>Composes the workbench and owns navigation and window-wide services.</summary>
public sealed class MainViewModel : ObservableObject, IDisposable, IAsyncDisposable
{
    private readonly IProjectClient _client;
    private readonly IMcpSessionClient? _mcp;
    private MainView _view = MainView.Blocks;
    private bool _isConfigurationPage;

    public MainViewModel() : this(new McpProjectClient(new McpSessionClient()), new WpfDialogService()) { }

    internal MainViewModel(bool offlineDemo) : this(new McpProjectClient(offlineDemo ? new OfflineSessionClient() : new McpSessionClient()), new WpfDialogService(), offlineDemo) { }

    public MainViewModel(IProjectClient client, IDialogService dialogs, bool offlineDemo = false, IVersionControlClient? versionControl = null)
    {
        OfflineDemo = offlineDemo;
        _client = client;
        AtlasService = client is McpProjectClient mcpProject ? new McpAtlasService(mcpProject) : new Services.Stubs.AtlasService(client);
        Activity = new WorkbenchActivity();
        Engineering = new EngineeringViewModel(client, dialogs, Activity);
        Session = new SessionViewModel(client, dialogs, Activity, LoadProjectAsync);
        VersionControl = versionControl != null ? new VersionControlViewModel(versionControl, dialogs, Activity, () => Engineering.SelectedDevice?.Name)
            : client is McpProjectClient vcProject
            ? new VersionControlViewModel(new McpVersionControlClient(vcProject), dialogs, Activity, () => null)
            : new VersionControlViewModel(client as IStudioClient ?? new StudioClient(), dialogs, Activity,
                () => { if (!client.IsRunning) client.Start(false, SelectedReleaseKey); }, () => Engineering.SelectedDevice?.Id, legacyAccess: client is IStudioClient);
        if (client is McpProjectClient project)
        {
            _mcp = project.Session;
            Panel = new McpSessionPanel(_mcp, () => SelectedReleaseKey, Activity, Session.ApplySharedStateAsync);
            project.ResultChanged += OnMcpResult;
        }
        Session.PropertyChanged += OnSessionChanged;
        Activity.PropertyChanged += OnActivityChanged;
        _client.Log += OnBridgeLog;
        _client.Progress += OnBridgeProgress;
    }

    public McpSessionPanel? Panel { get; }
    public bool OfflineDemo { get; }
    internal static bool OfflineDemoRequested(string[] args) => args.Any(a => a.Equals("--mock", StringComparison.OrdinalIgnoreCase) || a.Equals("--demo", StringComparison.OrdinalIgnoreCase));
    public bool HasMcpPanel => Panel != null && !OfflineDemo;
    public bool LegacyDemoAvailable => !HasMcpPanel && !OfflineDemo;
    public IEnumerable<McpProjectClient.Receipt> Receipts => (_client as McpProjectClient)?.Receipts ?? [];
    private McpProjectClient.Receipt? _selectedReceipt;
    public McpProjectClient.Receipt? SelectedReceipt
    {
        get => _selectedReceipt;
        set { if (Set(ref _selectedReceipt, value)) Raise(nameof(LastEnvelope)); }
    }
    public string LastEnvelope => SelectedReceipt?.Json ?? (_client as McpProjectClient)?.LastEnvelope ?? "";
    private void OnMcpResult(object? sender, EventArgs args)
    { SelectedReceipt = (_client as McpProjectClient)?.Receipts.LastOrDefault(); Raise(nameof(LastEnvelope)); }
    public SessionViewModel Session { get; }
    public EngineeringViewModel Engineering { get; }
    public VersionControlViewModel VersionControl { get; }
    public WorkbenchActivity Activity { get; }
    internal Services.Stubs.IAtlasService AtlasService { get; }

    // Shared with the window chrome and the configuration page.
    public IEnumerable<TiaMcp.Versioning.TiaVersionDescriptor> Releases => Session.Releases;
    public string SelectedReleaseKey { get => Session.SelectedReleaseKey; set => Session.SelectedReleaseKey = value; }
    public bool ServiceOwnsRelease { get => Session.ServiceOwnsRelease; set => Session.ServiceOwnsRelease = value; }
    public bool CanSelectRelease => Session.CanSelectRelease;
    public bool Busy => Activity.Busy;
    public bool VersionControlPageVisible => !OfflineDemo && SelectedReleaseKey is "16" or "17" or "18" or "19" or "20" or "21";

    public bool IsEngineeringPage => !_isConfigurationPage;
    public bool IsConfigurationPage
    {
        get => _isConfigurationPage;
        internal set
        {
            _isConfigurationPage = value;
            // Re-selecting the current page also restores the menu's radio check after its click toggle.
            Raise(nameof(IsEngineeringPage));
            Raise(nameof(IsConfigurationPage));
        }
    }

    /// <summary>Shown at the foot of the sidebar, from the assembly the app was built as.</summary>
    public static string AppVersion { get; } =
        "v" + (Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0");

    // The views are one choice, so they are one field with a bindable face each; independent
    // bools would let all of them be false and show nothing at all.
    private MainView View
    {
        get => _view;
        set
        {
            if (_view == value) return;
            _view = value;
            Raise(nameof(IsBlocksTab));
            Raise(nameof(IsVcTab));
            Raise(nameof(IsLogTab));
        }
    }

    public bool IsBlocksTab
    {
        get => _view == MainView.Blocks;
        set { if (value) View = MainView.Blocks; }
    }

    public bool IsVcTab
    {
        get => _view == MainView.VersionControl;
        set { if (value) View = MainView.VersionControl; }
    }

    public bool IsLogTab
    {
        get => _view == MainView.Log;
        set { if (value) View = MainView.Log; }
    }

    private async Task LoadProjectAsync(string projectName)
    {
        await Engineering.LoadDevicesAsync(projectName);
        // Populate VCI up front, including its unsupported state, after loading the blocks.
        if (_client is IStudioClient) await VersionControl.VcRefreshAsync();
    }

    private void OnSessionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SessionViewModel.IsConnected) && !Session.IsConnected
            || e.PropertyName == nameof(SessionViewModel.ProjectName) && Session.ProjectName.Length == 0)
        {
            Engineering.ClearPresentation();
            VersionControl.ClearPresentation();
        }
        if (e.PropertyName is nameof(SelectedReleaseKey) or nameof(ServiceOwnsRelease) or nameof(CanSelectRelease))
        {
            Raise(e.PropertyName);
            Raise(nameof(VersionControlPageVisible));
            VersionControl.RefreshCommands();
        }
    }

    private void OnActivityChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WorkbenchActivity.Busy)) { Raise(nameof(Busy)); VersionControl.RefreshCommands(); }
    }

    private void OnBridgeLog(object? sender, BridgeLogEventArgs e) => Activity.Append((_client is McpProjectClient ? "MCP: " : "bridge: ") + e.Line);
    private void OnBridgeProgress(object? sender, ProgressEventArgs e) => Activity.OnProgress(e.Progress);

    /// <summary>Applies the initial view, then lets the session handle mock/project startup.</summary>
    public async Task ApplyStartupAsync(string[] args)
    {
        if (OfflineDemo) { Activity.AppendLocalized("Session.OfflineDemo"); return; }
        var tab = Array.FindIndex(args, a => string.Equals(a, "--tab", StringComparison.OrdinalIgnoreCase));
        if (tab >= 0 && tab + 1 < args.Length)
        {
            View = args[tab + 1].ToLowerInvariant() switch
            {
                "vc" or "version-control" => MainView.VersionControl,
                "log" => MainView.Log,
                _ => MainView.Blocks,
            };
        }
        await Session.ApplyStartupAsync(args);
    }

    private void PrepareDispose()
    {
        Session.PropertyChanged -= OnSessionChanged;
        Activity.PropertyChanged -= OnActivityChanged;
        _client.Log -= OnBridgeLog;
        _client.Progress -= OnBridgeProgress;
        Session.Dispose();
        Engineering.Dispose();
        VersionControl.Dispose();
        Activity.Dispose();
        if (_client is McpProjectClient project) project.ResultChanged -= OnMcpResult;
        Panel?.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        PrepareDispose();
        // Retain the UI context: client disposal must follow MCP detach on the owning thread.
        if (_mcp != null) await _mcp.DisposeAsync();
        _client.Dispose();
    }

    public void Dispose()
    {
        PrepareDispose();
        // Synchronous compatibility for non-UI callers. The window uses DisposeAsync.
        // McpSessionClient detaches with ConfigureAwait(false), so this cannot capture its caller's context.
        _mcp?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _client.Dispose();
    }
}
