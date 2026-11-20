using System;
using System.ComponentModel;
using System.Collections.Specialized;
using System.IO;
using System.Threading.Tasks;
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
    private FeaturePagesViewModel? _features;
    public static readonly DependencyProperty LogRowsProperty = DependencyProperty.Register(
        nameof(LogRows), typeof(ICollectionView), typeof(LogView));
    public ICollectionView? LogRows => (ICollectionView?)GetValue(LogRowsProperty);
    public LogView()
    {
        InitializeComponent();
        IsVisibleChanged += OnLogVisibility;
    }
    private async void OnLogVisibility(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!IsVisible) return;
        string directory = TiaOpenness.Shared.DataLocations.Current.LogsDirectory;
        StoragePath.Text = directory;
        StoragePath.ToolTip = directory;
        try
        {
            long bytes = await Task.Run(() => new DirectoryInfo(directory).EnumerateFiles("*", new EnumerationOptions
            {
                RecurseSubdirectories = true, IgnoreInaccessible = false, AttributesToSkip = FileAttributes.ReparsePoint
            }).Sum(file => file.Length));
            StorageSize.Text = $"{bytes / 1048576d:N2} MB";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Trace.TraceWarning("Log directory size unavailable: {0}", ex.GetType().Name);
            StorageSize.Text = "—";
        }
    }
    private MainViewModel Model => (MainViewModel)DataContext;
    internal void SetFeatures(FeaturePagesViewModel? features)
    {
        if (_features != null) _features.PropertyChanged -= OnFeatureChanged;
        _features = features;
        DiagnosticStrip.DataContext = features;
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
        AllLevelsCount.Text = rows.Count.ToString();
        WarningsOnlyCount.Text = rows.Count(row => row.Level == LogLevel.Warning).ToString();
        ErrorsOnlyCount.Text = rows.Count(row => row.Level == LogLevel.Error).ToString();
    }
    private bool Matches(object item)
    {
        var row = (ResultPresentation.LogRow)item;
        return (_level == 0 || _level == 1 && row.Level == LogLevel.Warning || _level == 2 && row.Level == LogLevel.Error)
            && row.Message.Contains(LogSearch?.Text.Trim() ?? "", StringComparison.OrdinalIgnoreCase);
    }
    private void OnLevel(object sender, RoutedEventArgs e)
    {
        _level = int.Parse((string)((RadioButton)sender).Tag, System.Globalization.CultureInfo.InvariantCulture);
        LogRows?.Refresh();
    }
    private void OnSearch(object sender, TextChangedEventArgs e) => LogRows?.Refresh();
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
            .Select(row => row.Time + " " + row.Message) ?? []));
    }

    internal Func<bool>? ConfirmClear { get; set; }
    private void OnClearLog(object sender, RoutedEventArgs e)
    {
        bool confirmed = ConfirmClear?.Invoke() ?? WorkbenchMessageBox.Show(Window.GetWindow(this),
            Loc.Current["Pages.ClearLogPrompt"], Loc.Current["Pages.ClearLog"], MessageBoxButton.OKCancel, MessageBoxImage.Warning) == MessageBoxResult.OK;
        if (confirmed)
        {
            Model.Activity.ClearLog();
            Model.Activity.Notify(ToastKind.Success, LocalizedText.Key("Toast.Cleared"), "clear-log");
        }
    }
    private void OnGenerate(object sender, RoutedEventArgs e) => _features?.Export();
    private void OnOpenFolder(object sender, RoutedEventArgs e) { if (_features != null) _features.Run(_features.Diagnostics.OpenFolder); }
    private void OnOpenStorageSettings(object sender, RoutedEventArgs e)
    {
        var button = (Button)sender;
        WorkbenchCommands.Navigate.Execute(button.CommandParameter, button);
        if (Window.GetWindow(this) is MainWindow { CurrentPage: "Settings" } window) window.SettingsContent.ShowSection("Service");
    }
}
