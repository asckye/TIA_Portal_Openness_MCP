using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Reflection;
using System.Threading.Tasks;
using TiaOpenness.Client;
using TiaOpenness.Gui.Common;
using TiaOpenness.Gui.Services;

namespace TiaOpenness.Gui.ViewModels;

/// <summary>Composes the workbench and owns navigation and window-wide services.</summary>
public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly IStudioClient _client;
    private MainView _view = MainView.Blocks;
    private bool _isConfigurationPage;

    public MainViewModel() : this(new StudioClient(), new WpfDialogService()) { }

    public MainViewModel(IStudioClient client, IDialogService dialogs)
    {
        _client = client;
        Activity = new WorkbenchActivity();
        Engineering = new EngineeringViewModel(client, dialogs, Activity);
        Session = new SessionViewModel(client, dialogs, Activity, LoadProjectAsync);
        VersionControl = new VersionControlViewModel(client, dialogs, Activity,
            Session.EnsureBridge, () => Engineering.SelectedDevice?.Id);
        Session.PropertyChanged += OnSessionChanged;
        Activity.PropertyChanged += OnActivityChanged;
        _client.Log += OnBridgeLog;
        _client.Progress += OnBridgeProgress;
    }

    public SessionViewModel Session { get; }
    public EngineeringViewModel Engineering { get; }
    public VersionControlViewModel VersionControl { get; }
    public WorkbenchActivity Activity { get; }

    // Shared with the window chrome and the configuration page.
    public IEnumerable<TiaMcp.Versioning.TiaVersionDescriptor> Releases => Session.Releases;
    public string SelectedReleaseKey { get => Session.SelectedReleaseKey; set => Session.SelectedReleaseKey = value; }
    public bool ServiceOwnsRelease { get => Session.ServiceOwnsRelease; set => Session.ServiceOwnsRelease = value; }
    public bool CanSelectRelease => Session.CanSelectRelease;
    public bool Busy => Activity.Busy;

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
        await VersionControl.VcRefreshAsync();
    }

    private void OnSessionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SelectedReleaseKey) or nameof(ServiceOwnsRelease) or nameof(CanSelectRelease))
            Raise(e.PropertyName);
    }

    private void OnActivityChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WorkbenchActivity.Busy)) Raise(nameof(Busy));
    }

    private void OnBridgeLog(object? sender, BridgeLogEventArgs e) => Activity.Append("bridge: " + e.Line);
    private void OnBridgeProgress(object? sender, ProgressEventArgs e) => Activity.OnProgress(e.Progress);

    /// <summary>Applies the initial view, then lets the session handle mock/project startup.</summary>
    public async Task ApplyStartupAsync(string[] args)
    {
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

    public void Dispose()
    {
        Session.PropertyChanged -= OnSessionChanged;
        Activity.PropertyChanged -= OnActivityChanged;
        _client.Log -= OnBridgeLog;
        _client.Progress -= OnBridgeProgress;
        Session.Dispose();
        Engineering.Dispose();
        VersionControl.Dispose();
        Activity.Dispose();
        _client.Dispose();
    }
}
