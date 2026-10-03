using System.Windows;
using System.Windows.Controls;
using TiaOpenness.Gui.ViewModels;

namespace TiaOpenness.Gui;

public partial class MainWindow : Window
{
    private readonly MainViewModel _model;
    private readonly bool _loadExistingConfiguration;

    public MainWindow() : this(new MainViewModel()) { }

    internal MainWindow(MainViewModel model, bool loadExistingConfiguration = true)
    {
        _model = model;
        _loadExistingConfiguration = loadExistingConfiguration;
        _model.SelectedReleaseKey = InitialReleaseKey(System.Environment.GetCommandLineArgs());
        InitializeComponent();
        DataContext = _model;
        Root.Tag = new Controls.GlassResults(_model);
        ToWorkspace.Checked += OnSyncDirection;
        ToProject.Checked += OnSyncDirection;
        SyncStrip.SizeChanged += (_, _) => {
            var transform = (System.Windows.Media.TranslateTransform)SyncThumb.RenderTransform;
            transform.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, null);
            transform.X = ToWorkspace.IsChecked == true ? 0 : ToWorkspace.ActualWidth;
        };
        _model.PropertyChanged += (_, e) => {
            if (e.PropertyName == nameof(MainViewModel.IsVcTab)) DetailColumn.Width = new GridLength(_model.IsVcTab ? 340 : 320);
        };
        Closing += (_, e) => { if (_model.Busy) e.Cancel = true; };
        Closed += (_, _) => { DisposeConfiguration(); ((Controls.GlassResults)Root.Tag).Dispose(); _model.Dispose(); };
        Loaded += async (_, _) => {
            await _model.ApplyStartupAsync(System.Environment.GetCommandLineArgs());
            if (System.Array.IndexOf(System.Environment.GetCommandLineArgs(), "--mcp") >= 0) ShowConfiguration();
        };
    }

    private void OnSyncDirection(object sender, RoutedEventArgs e)
    {
        double target = ToWorkspace.IsChecked == true ? 0 : ToWorkspace.ActualWidth;
        ((System.Windows.Media.TranslateTransform)SyncThumb.RenderTransform).BeginAnimation(
            System.Windows.Media.TranslateTransform.XProperty,
            new System.Windows.Media.Animation.DoubleAnimation(target, System.TimeSpan.FromMilliseconds(160)) {
                EasingFunction = new System.Windows.Media.Animation.QuadraticEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }
            });
    }
    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void OnMaximize(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void OnClose(object sender, RoutedEventArgs e) => Close();
    private void ShowOptions(StackPanel panel, string title)
    {
        foreach (var item in new[] { ProjectOptions, ExportOptions, InspectOptions, WorkspaceOptions })
            item.Visibility = item == panel ? Visibility.Visible : Visibility.Collapsed;
        OptionsTitle.SetBinding(TextBlock.TextProperty, Localization.TrExtension.CreateBinding(title));
        OptionsOverlay.Visibility = Visibility.Visible;
    }
    private void OnProjectOptions(object sender, RoutedEventArgs e) => ShowOptions(ProjectOptions, "Project.Label");
    private void OnExportOptions(object sender, RoutedEventArgs e) => ShowOptions(ExportOptions, "Glass.Transfer");
    private void OnInspectOptions(object sender, RoutedEventArgs e) => ShowOptions(InspectOptions, "Toolbar.Inspect");
    private void OnWorkspaceOptions(object sender, RoutedEventArgs e) => ShowOptions(WorkspaceOptions, "Glass.NewWorkspace");
    private void OnCloseOptions(object sender, RoutedEventArgs e) => OptionsOverlay.Visibility = Visibility.Collapsed;
    private void OnSoftware(object sender, RoutedEventArgs e) { ShowEngineering(); _model.IsBlocksTab = true; SoftwarePicker.IsDropDownOpen = true; }
    private void OnGit(object sender, RoutedEventArgs e) { ShowEngineering(); _model.IsVcTab = true; }
    private void OnRadioMenuClick(object sender, RoutedEventArgs e)
    {
        // MenuItem toggles before Click; a radio choice must remain selected on a second click.
        ((MenuItem)sender).SetCurrentValue(MenuItem.IsCheckedProperty, true);
    }
    private void OnEngineeringView(object sender, RoutedEventArgs e)
    {
        OnRadioMenuClick(sender, e);
        ShowEngineering();
    }
    private void OnMappedFile(object sender, RoutedEventArgs e) => _model.VersionControl.SelectedVcItem = (TiaOpenness.Contracts.Models.MappedObjectInfo)((Button)sender).Tag;
    private void OnMappedFilter(object sender, RoutedEventArgs e)
    {
        string state = (string)((Button)sender).Tag;
        MappedList.Items.Filter = item => ((TiaOpenness.Contracts.Models.MappedObjectInfo)item).CompareState.ToString() == state;
    }
    private void OnMappedAll(object sender, RoutedEventArgs e)
    {
        MappedList.Items.Filter = null;
        if (_model.VersionControl.VcStatus.CanExecute(null)) _model.VersionControl.VcStatus.Execute(null);
    }
    private void OnPreview(object sender, RoutedEventArgs e) => _model.VersionControl.VcDryRun = true;
    private void OnRun(object sender, RoutedEventArgs e) => _model.VersionControl.VcDryRun = false;
    private void OnExportClicked(object sender, RoutedEventArgs e) { if (_model.Engineering.OutputDirectory.Length == 0) OnExportOptions(sender, e); else if (_model.Engineering.Export.CanExecute(null)) _model.Engineering.Export.Execute(null); }

    // ---- browse buttons ----------------------------------------------------

    /// <summary>The tree carries folders as well as devices; only a device changes the selection.</summary>
    private void OnDeviceSelected(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is ViewModels.DeviceNode { Device: not null } node) _model.Engineering.SelectedDevice = node.Device;
    }

    private void OnBrowseProject(object sender, RoutedEventArgs e) => _model.Session.BrowseProject();

    private void OnBrowseOutput(object sender, RoutedEventArgs e) => _model.Engineering.BrowseOutput();

    private void OnBrowseWorkspace(object sender, RoutedEventArgs e) => _model.VersionControl.BrowseWorkspaceFolder();

    private void OnSelectAll(object sender, RoutedEventArgs e) => _model.Engineering.SelectAll(true);

    private void OnSelectNone(object sender, RoutedEventArgs e) => _model.Engineering.SelectAll(false);

    // ---- log ---------------------------------------------------------------

    /// <summary>Keeps the log pinned to the newest line as it grows.</summary>
    private void OnLogChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is TextBox box) box.ScrollToEnd();
    }

    private void OnCopyLog(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(_model.Activity.Log);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // The clipboard is a shared OS resource and another process can hold it open.
            // Failing to copy a log is not worth an error dialog.
        }
    }

    private void OnClearLog(object sender, RoutedEventArgs e) => _model.Activity.ClearLog();

}
