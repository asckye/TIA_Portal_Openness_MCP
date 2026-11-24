using System;
using System.ComponentModel;
using System.Collections.Specialized;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using TiaOpenness.Gui.Controls;
using TiaOpenness.Gui.Localization;
using TiaOpenness.Gui.ViewModels;

namespace TiaOpenness.Gui.Views;

public partial class LogView : UserControl
{
    private bool _followQueued;
    private int _level;
    private readonly System.Windows.Input.RoutedCommand _storage = new();
    private FeaturePagesViewModel? _features;
    public static readonly DependencyProperty LogRowsProperty = DependencyProperty.Register(
        nameof(LogRows), typeof(ICollectionView), typeof(LogView));
    public ICollectionView? LogRows => (ICollectionView?)GetValue(LogRowsProperty);
    public LogView()
    {
        InitializeComponent();
        CommandBindings.Add(new System.Windows.Input.CommandBinding(_storage, (_, _) => OpenStorageSettings()));
        Loaded += (_, _) => Loc.Current.LanguageChanged += OnLanguageChanged;
        Unloaded += (_, _) => Loc.Current.LanguageChanged -= OnLanguageChanged;
    }
    private void OnLanguageChanged(object? sender, EventArgs e) => UpdateCounts();
    private MainViewModel Model => (MainViewModel)DataContext;
    internal void SetFeatures(FeaturePagesViewModel? features)
    {
        if (_features != null) _features.PropertyChanged -= OnFeatureChanged;
        _features = features;
        if (features != null) features.PropertyChanged += OnFeatureChanged;
        UpdateGenerateState();
    }
    private void OnFeatureChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FeaturePagesViewModel.DiagnosticCanStart)) UpdateGenerateState();
    }
    private void UpdateGenerateState() => GenerateButton.IsEnabled = _features?.DiagnosticCanStart ?? false;
    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property != TagProperty) return;
        if (e.OldValue is ResultPresentation previous)
            CollectionChangedEventManager.RemoveHandler((INotifyCollectionChanged)previous.LogRows, OnLogRowsChanged);
        if (e.NewValue is ResultPresentation results)
        {
            CollectionChangedEventManager.AddHandler((INotifyCollectionChanged)results.LogRows, OnLogRowsChanged);
            var source = new CollectionViewSource { Source = results.LogRows };
            SetValue(LogRowsProperty, source.View);
            source.View.Filter = Matches;
            UpdateCounts();
        }
    }
    private bool _countsQueued;
    private void OnLogRowsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_countsQueued) return;
        _countsQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.DataBind, new Action(() => { _countsQueued = false; UpdateCounts(); }));
    }
    private void UpdateCounts()
    {
        if (Tag is not ResultPresentation results) return;
        var rows = results.LogRows;
        bool empty = rows.Count == 0;
        EmptyLog.Title = empty ? Loc.Current["Log.Empty"] : Loc.Current["Log.FilterEmpty"];
        EmptyLog.Description = empty ? Loc.Current.T("Log.EmptyHelp", TiaOpenness.Shared.DataLocations.Current.LogsDirectory) : Loc.Current["Log.FilterHint"];
        EmptyLog.ActionCommand = empty ? _storage : null;
        ClearLogButton.IsEnabled = !empty;
        AllLevelsCount.Text = rows.Count.ToString();
        WarningsOnlyCount.Text = rows.Count(row => row.Level == LogLevel.Warning).ToString();
        ErrorsOnlyCount.Text = rows.Count(row => row.Level == LogLevel.Error).ToString();
    }
    private bool Matches(object item)
    {
        var row = (ResultPresentation.LogRow)item;
        string query = LogSearch?.Text.Trim() ?? "";
        return (_level == 0 || _level == 1 && row.Level == LogLevel.Warning || _level == 2 && row.Level == LogLevel.Error)
            && (row.Message.Contains(query, StringComparison.OrdinalIgnoreCase) || row.Time.Contains(query, StringComparison.OrdinalIgnoreCase));
    }
    private void OnLevel(object sender, RoutedEventArgs e)
    {
        _level = int.Parse((string)((RadioButton)sender).Tag, System.Globalization.CultureInfo.InvariantCulture);
        LogRows?.Refresh();
    }
    private void OnSearch(object sender, TextChangedEventArgs e) => LogRows?.Refresh();
    private void OnClearSearch(object sender, RoutedEventArgs e) { LogSearch.Clear(); LogSearch.Focus(); }
    private void OnMoreActions(object sender, RoutedEventArgs e)
    {
        MoreActions.ContextMenu.PlacementTarget = MoreActions;
        MoreActions.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        MoreActions.ContextMenu.IsOpen = true;
    }
    private void OnCopyRow(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).DataContext is ResultPresentation.LogRow row) _features?.Copy(row.DisplayText);
    }
    private void OnFollow(object sender, RoutedEventArgs e) { if (FollowLatest.IsChecked == true) QueueFollow(); }
    private void OnLogScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (FollowLatest.IsChecked == true && e.ExtentHeightChange > 0) QueueFollow();
    }
    private void QueueFollow()
    {
        if (_followQueued) return;
        // ScrollChanged can arrive while a virtual row is being prepared. Follow after layout, so scrolling
        // cannot recycle that row before its container style has replaced the theme's ancestor bindings.
        _followQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            _followQueued = false;
            if (FollowLatest.IsChecked == true && LogList.Items.Count > 0) LogList.ScrollIntoView(LogList.Items[^1]);
        }));
    }
    internal void ShowMessage(string text)
    {
        MessageText.Text = text;
        MessageDetails.Visibility = Visibility.Visible;
    }
    private void OnCloseMessage(object sender, RoutedEventArgs e) => MessageDetails.Visibility = Visibility.Collapsed;
    private void OnCopyLog(object sender, RoutedEventArgs e)
    {
        _features?.Copy(string.Join("\n", LogRows?.Cast<ResultPresentation.LogRow>()
            .Select(row => row.DisplayText) ?? []));
    }

    internal Func<bool>? ConfirmClear { get; set; }
    internal FeatureConfirmation ClearConfirmation() => new(Loc.Current["Log.ClearTitle"],
        Loc.Current.T("Log.ClearBody", TiaOpenness.Shared.DataLocations.Current.LogsDirectory), Loc.Current["Log.ClearAction"],
        new(Loc.Current["Log.Scope"], Loc.Current.T("Log.MemoryCount", Model.Activity.Entries.Count)),
        new(Loc.Current["Log.Disk"], Loc.Current["Log.Retained"]));

    private void OnClearLog(object sender, RoutedEventArgs e)
    {
        bool confirmed = ConfirmClear?.Invoke() ?? ClearConfirmation().Confirm(this);
        if (confirmed)
        {
            Model.Activity.ClearLog();
            Model.Activity.Notify(ToastKind.Success, LocalizedText.Key("Toast.Cleared"), "clear-log");
        }
    }
    private void OnGenerate(object sender, RoutedEventArgs e) => _features?.Export();
    private void OnOpenStorageSettings(object sender, RoutedEventArgs e) => OpenStorageSettings();
    internal event EventHandler? StorageSettingsRequested;
    private void OpenStorageSettings() => StorageSettingsRequested?.Invoke(this, EventArgs.Empty);
}
