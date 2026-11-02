using System;
using System.ComponentModel;
using System.IO;
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
        LogPath.Text = TiaOpenness.Shared.DataLocations.Current.LogsDirectory;
    }
    private MainViewModel Model => (MainViewModel)DataContext;
    internal void SetFeatures(FeaturePagesViewModel? features)
    {
        if (_features != null) _features.PropertyChanged -= OnFeatureChanged;
        _features = features;
        LogTools.DataContext = features;
        if (features != null) { features.PropertyChanged += OnFeatureChanged; UpdateRetention(); }
    }
    private void OnFeatureChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(FeaturePagesViewModel.RetentionSize) or nameof(FeaturePagesViewModel.RetentionCopies)) UpdateRetention();
    }
    private void UpdateRetention()
    {
        if (_features == null) return;
        foreach (var button in new[] { Size5, Size10, Size50 })
            button.IsChecked = int.Parse((string)button.Tag, System.Globalization.CultureInfo.InvariantCulture) == _features.RetentionSize;
        foreach (var button in new[] { Copies3, Copies5, Copies10 })
            button.IsChecked = int.Parse((string)button.Tag, System.Globalization.CultureInfo.InvariantCulture) == _features.RetentionCopies;
    }
    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == TagProperty && e.NewValue is ResultPresentation results)
        {
            var source = new CollectionViewSource { Source = results.LogRows };
            SetValue(LogRowsProperty, source.View);
            source.View.Filter = Matches;
        }
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
    private void OnCopyLog(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(string.Join("\n", LogRows?.Cast<ResultPresentation.LogRow>()
                .Select(row => row.Time + " " + row.Message) ?? []));
        }
        catch (System.Runtime.InteropServices.COMException) /* swallow(ui): another process can hold the clipboard; an unsuccessful log copy does not interrupt the workbench */
        {
            // The clipboard is a shared OS resource and another process can hold it open.
            // Failing to copy a log is not worth an error dialog.
        }
    }

    internal Func<bool>? ConfirmClear { get; set; }
    private void OnClearLog(object sender, RoutedEventArgs e)
    {
        bool confirmed = ConfirmClear?.Invoke() ?? WorkbenchMessageBox.Show(Window.GetWindow(this),
            Loc.Current["Pages.ClearLogPrompt"], Loc.Current["Pages.ClearLog"], MessageBoxButton.OKCancel, MessageBoxImage.Warning) == MessageBoxResult.OK;
        if (confirmed) Model.Activity.ClearLog();
    }
    private void OnGenerate(object sender, RoutedEventArgs e) => _features?.Export();
    private void OnOpenFolder(object sender, RoutedEventArgs e) { if (_features != null) _features.Run(_features.Diagnostics.OpenFolder); }
    private void OnSize(object sender, RoutedEventArgs e) => _features?
        .SetRetention(int.Parse((string)((RadioButton)sender).Tag, System.Globalization.CultureInfo.InvariantCulture), true);
    private void OnCopies(object sender, RoutedEventArgs e) => _features?
        .SetRetention(int.Parse((string)((RadioButton)sender).Tag, System.Globalization.CultureInfo.InvariantCulture), false);
    private void OnRetentionFolder(object sender, RoutedEventArgs e) { if (_features != null) _features.Run(_features.Audit.OpenFolder); }
    private void OnRuntimeFolder(object sender, RoutedEventArgs e)
    {
        try
        {
            string path = LogPath.Text;
            if (!Directory.Exists(path)) throw new DirectoryNotFoundException(Loc.Current.T("Settings.PathMissing", path));
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) {
            WorkbenchMessageBox.Show(Window.GetWindow(this), ex.Message, Loc.Current["Dialog.Error.Caption"], MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

}
