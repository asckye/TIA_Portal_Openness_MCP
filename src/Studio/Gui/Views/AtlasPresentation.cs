using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TiaOpenness.Gui.Common;
using TiaOpenness.Gui.Localization;
using TiaOpenness.Gui.Services.Stubs;
using TiaOpenness.Gui.Services;
using TiaOpenness.Gui.ViewModels;

namespace TiaOpenness.Gui.Views;

/// <summary>View state for the atlas boundary, independent of native engineering commands.</summary>
public sealed class AtlasPresentation : ObservableObject, IDisposable
{
    private readonly MainViewModel _model;
    private readonly IAtlasService _service;
    private readonly Action<string> _open;
    private readonly Action<ToastNotice>? _notify;
    internal event Action? ClearNotice;
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _generation;
    private string _state = "Idle";
    private string _detail = "";
    private AtlasRequest? _retryRequest;
    private int _completed;
    private int _total;
    private bool _disposed;

    internal AtlasPresentation(MainViewModel model, IAtlasService service, Action<string>? open = null, Action<ToastNotice>? notify = null)
    {
        _notify = notify;
        _model = model;
        _service = service;
        _open = open ?? (path => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }));
        Loc.Current.LanguageChanged += OnLanguageChanged;
        _model.Engineering.PropertyChanged += OnModelChanged;
        _model.Session.PropertyChanged += OnModelChanged;
        _model.Activity.PropertyChanged += OnModelChanged;
    }

    public bool Visible => _state != "Idle";
    public bool HasOutputOrProgress => Running || Done;
    public bool Running => _state == "Running";
    public bool Done => _state == "Done";
    public bool Failed => _state is "Failed" or "OpenFailed";
    public bool CanGenerate => !_disposed && _model.Session.IsConnected && _model.Engineering.SelectedDevice?.Category == "Plc" && !_model.Busy && !Running;
    private BlockRow? RenderBlock => _model.Engineering.FocusedBlock ?? _model.Engineering.Blocks.FirstOrDefault(b => b.Selected);
    public bool CanViewBlock => CanGenerate && RenderBlock != null;
    public string Label => Running ? Loc.Current.T("Pages.AtlasRunning", _completed, _total)
        : Done ? Loc.Current["Pages.AtlasDone"] : _state == "OpenFailed" ? Loc.Current["Project.AtlasOpenFailed"] : Loc.Current["Pages.AtlasFailed"];
    public string Detail => _detail;
    public double Percent => _total == 0 ? 0 : 100.0 * _completed / _total;
    public bool Indeterminate => Running && _total <= 0;
    internal void Cancel() => _generation?.Cancel();

    internal void Dismiss()
    {
        if (Running) Cancel();
        else { _state = "Idle"; _detail = ""; Raise(null); }
    }

    internal Task GenerateAsync(bool singleBlock)
    {
        if (!CanGenerate || (singleBlock && !CanViewBlock)) return Task.CompletedTask;
        var blocks = singleBlock ? new[] { RenderBlock! }.AsEnumerable() : _model.Engineering.Blocks;
        var request = new AtlasRequest(_model.Session.ProjectPath, _model.Engineering.SelectedDevice!.Id,
            blocks.Select(block => block.Path).ToArray(), singleBlock);
        _retryRequest = request with { BlockPaths = blocks.Where(block => !block.Info.IsKnowHowProtected).Select(block => block.Path).ToArray() };
        return GenerateAsync(request);
    }

    private async Task GenerateAsync(AtlasRequest request)
    {
        if (!CanGenerate || _model.Engineering.SelectedDevice!.Id != request.DeviceId) return;
        ClearNotice?.Invoke();
        if (request.BlockPaths.Count == 0) { Fail(Loc.Current["Project.NoAtlasBlocks"], canRetry: false); return; }
        using var generation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _generation = generation;
        var token = generation.Token;
        _state = "Running";
        _detail = request.SingleBlock ? request.BlockPaths[0] : request.DeviceId;
        _completed = 0;
        _total = request.BlockPaths.Count;
        Raise(null);
        try
        {
            var progress = new Progress<AtlasProgress>(value =>
            {
                if (_generation != generation || !Running || token.IsCancellationRequested) return;
                _completed = value.Completed;
                _total = value.Total;
                _detail = value.CurrentBlock;
                Raise(null);
            });
            var result = await _service.GenerateAsync(request, progress, token);
            if (_disposed || _generation != generation || !Running) return;
            if (result.Error != null) Fail(result.Error);
            else if (string.IsNullOrWhiteSpace(result.HtmlPath)) Fail(Loc.Current["Pages.AtlasMissingPath"]);
            else
            {
                _state = "Done";
                _detail = result.HtmlPath;
                _notify?.Invoke(new("atlas", ToastKind.Success, LocalizedText.Literal(result.HtmlPath), LocalizedText.Key("Pages.AtlasDone"),
                    () => Open(), "", ActionLabel: LocalizedText.Key("Project.OpenAtlas"),
                    SecondAction: () => Open(true), SecondActionKey: LocalizedText.Key("Project.OpenFolder")));
                Raise(null);
                if (request.SingleBlock && !token.IsCancellationRequested) Open();
            }
        }
        catch (OperationCanceledException ex) when (ex.CancellationToken == token && token.IsCancellationRequested)
        {
            /* swallow(teardown): cancellation between steps ends this generation */
            if (_generation == generation && Running) { _state = "Idle"; _detail = ""; Raise(null); }
        }
        catch (Exception ex) { if (!_disposed && _generation == generation && Running) Fail(ex.Message); }
        finally
        {
            if (_generation == generation) _generation = null;
        }
    }

    internal void Open(bool folder = false)
    {
        if (!Done && _state != "OpenFailed") return;
        try
        {
            // Generated HTML is opened with the OS browser association, never an embedded WebView.
            string path = Path.GetFullPath(_detail);
            if (!string.Equals(Path.GetExtension(path), ".html", StringComparison.OrdinalIgnoreCase) || !folder && !File.Exists(path))
                throw new IOException(Loc.Current["Pages.AtlasMissingPath"]);
            _open(folder ? Path.GetDirectoryName(path)! : path);
            if (_state == "OpenFailed") { _state = "Done"; Raise(null); }
        }
        catch (Exception ex)
        {
            _state = "OpenFailed";
            Raise(null);
            _notify?.Invoke(new("atlas", ToastKind.Error, LocalizedText.Literal(ex.Message), LocalizedText.Key("Project.AtlasOpenFailed"),
                () => Open(true), "", ActionLabel: LocalizedText.Key("Project.OpenFolder"),
                SecondAction: () => Open(), SecondActionKey: LocalizedText.Key("Project.RetryOpenAtlas"), Sticky: true));
        }
    }

    private void Fail(string reason, bool canRetry = true)
    {
        _state = "Failed"; _detail = reason; Raise(null);
        var retry = _retryRequest;
        _notify?.Invoke(new("atlas", ToastKind.Error, LocalizedText.Literal(reason), LocalizedText.Key("Pages.AtlasFailed"),
            canRetry && retry != null ? () => _ = GenerateAsync(retry) : null, "",
            ActionLabel: LocalizedText.Key("Project.RetryAtlas"), Sticky: true));
    }
    private void OnLanguageChanged(object? sender, EventArgs e) => Raise(null);
    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        bool disconnected = sender == _model.Session && e.PropertyName == nameof(SessionViewModel.IsConnected) && !_model.Session.IsConnected;
        bool changedDevice = sender == _model.Engineering && e.PropertyName == nameof(EngineeringViewModel.SelectedDevice)
            && _retryRequest != null && _retryRequest.DeviceId != _model.Engineering.SelectedDevice?.Id;
        if (disconnected || changedDevice)
        {
            ClearNotice?.Invoke();
            _retryRequest = null;
            _generation?.Cancel();
            _state = "Idle";
            _detail = "";
        }
        Raise(null);
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        Loc.Current.LanguageChanged -= OnLanguageChanged;
        _model.Engineering.PropertyChanged -= OnModelChanged;
        _model.Session.PropertyChanged -= OnModelChanged;
        _model.Activity.PropertyChanged -= OnModelChanged;
        _lifetime.Dispose();
    }
}
