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
using TiaOpenness.Gui.ViewModels;

namespace TiaOpenness.Gui.Views;

/// <summary>View state for the atlas boundary, independent of native engineering commands.</summary>
public sealed class AtlasPresentation : ObservableObject, IDisposable
{
    private readonly MainViewModel _model;
    private readonly IAtlasService _service;
    private readonly Action<string> _open;
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _generation;
    private string _state = "Idle";
    private string _detail = "";
    private int _completed;
    private int _total;
    private bool _disposed;

    internal AtlasPresentation(MainViewModel model, IAtlasService service, Action<string>? open = null)
    {
        _model = model;
        _service = service;
        _open = open ?? (path => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }));
        Loc.Current.LanguageChanged += OnLanguageChanged;
        _model.Engineering.PropertyChanged += OnModelChanged;
        _model.Session.PropertyChanged += OnModelChanged;
        _model.Activity.PropertyChanged += OnModelChanged;
    }

    public bool Visible => _state != "Idle";
    public bool Running => _state == "Running";
    public bool Done => _state == "Done";
    public bool Failed => _state == "Failed";
    public bool CanGenerate => _model.Session.IsConnected && _model.Engineering.SelectedDevice != null && !_model.Busy && !Running;
    public bool CanViewBlock => CanGenerate && _model.Engineering.Blocks.Any(b => b.Selected);
    public string Label => Running ? Loc.Current.T("Pages.AtlasRunning", _completed, _total)
        : Done ? Loc.Current["Pages.AtlasDone"] : Loc.Current["Pages.AtlasFailed"];
    public string Detail => _detail;
    public double Percent => _total == 0 ? 0 : 100.0 * _completed / _total;

    internal async Task GenerateAsync(bool singleBlock)
    {
        if (!CanGenerate || (singleBlock && !CanViewBlock)) return;
        using var generation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _generation = generation;
        var token = generation.Token;
        var blocks = singleBlock ? _model.Engineering.Blocks.Where(b => b.Selected).Take(1) : _model.Engineering.Blocks;
        var request = new AtlasRequest(_model.Session.ProjectPath, _model.Engineering.SelectedDevice!.Id,
            blocks.Select(b => b.Path).ToArray(), singleBlock);
        _state = "Running";
        _detail = "";
        _completed = 0;
        _total = request.BlockPaths.Count;
        Raise(null);
        try
        {
            var progress = new Progress<AtlasProgress>(value =>
            {
                if (!Running || token.IsCancellationRequested) return;
                _completed = value.Completed;
                _total = value.Total;
                _detail = value.CurrentBlock;
                Raise(null);
            });
            var result = await _service.GenerateAsync(request, progress, token);
            if (token.IsCancellationRequested) return;
            if (result.Error != null) Fail(result.Error);
            else if (string.IsNullOrWhiteSpace(result.HtmlPath)) Fail(Loc.Current["Pages.AtlasMissingPath"]);
            else
            {
                _state = "Done";
                _detail = result.HtmlPath;
                Raise(null);
                if (singleBlock) Open();
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) /* swallow(teardown): closing or disconnecting cancels only the view's offline atlas work */
        {
        }
        catch (Exception ex) { Fail(ex.Message); }
        finally { if (ReferenceEquals(_generation, generation)) _generation = null; }
    }

    internal void Open()
    {
        if (!Done) return;
        try
        {
            // Generated HTML is opened with the OS browser association, never an embedded WebView.
            string path = Path.GetFullPath(_detail);
            if (!File.Exists(path) || !string.Equals(Path.GetExtension(path), ".html", StringComparison.OrdinalIgnoreCase))
                throw new IOException(Loc.Current["Pages.AtlasMissingPath"]);
            _open(path);
        }
        catch (Exception ex) { Fail(ex.Message); }
    }

    private void Fail(string reason) { _state = "Failed"; _detail = reason; Raise(null); }
    private void OnLanguageChanged(object? sender, EventArgs e) => Raise(null);
    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender == _model.Session && e.PropertyName == nameof(SessionViewModel.IsConnected) && !_model.Session.IsConnected)
        {
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
