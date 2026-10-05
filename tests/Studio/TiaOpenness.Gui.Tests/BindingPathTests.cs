using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using TiaOpenness.Contracts.Models;
using TiaOpenness.Contracts.Rpc;
using TiaOpenness.Gui.Localization;
using TiaOpenness.Gui.Themes;
using TiaOpenness.Gui.ViewModels;
using Xunit;
using Xunit.Abstractions;

namespace TiaOpenness.Gui.Tests;

[Collection(WpfCollection.Name)]
public sealed class BindingPathTests(WpfContext wpf, ITestOutputHelper output)
{
    internal sealed class BindingTrace : IDisposable
    {
        private readonly TraceSource _source = PresentationTraceSources.DataBindingSource;
        private readonly SourceLevels _previous;
        private readonly StringWriter _text = new();
        private readonly TextWriterTraceListener _listener;

        public BindingTrace()
        {
            _previous = _source.Switch.Level;
            // Enable WPF diagnostics in a Release test host without an attached debugger.
            PresentationTraceSources.Refresh();
            _listener = new TextWriterTraceListener(_text);
            _source.Listeners.Add(_listener);
            _source.Switch.Level = SourceLevels.Warning;
        }

        public string Text { get { _listener.Flush(); return _text.ToString(); } }

        public void Dispose()
        {
            _source.Listeners.Remove(_listener);
            _source.Switch.Level = _previous;
            _listener.Dispose();
            _text.Dispose();
        }
    }

    [Fact]
    public void Binding_trace_detects_a_missing_property()
    {
        wpf.Run(() =>
        {
            using var trace = new BindingTrace();
            var probe = new TextBlock();
            probe.SetBinding(TextBlock.TextProperty, new Binding("DeliberatelyMissingBindingPath") { Source = new object() });
            Flush();
            Assert.Contains("System.Windows.Data Error: 40", trace.Text);
            Assert.Contains("DeliberatelyMissingBindingPath", trace.Text);
            BindingOperations.ClearAllBindings(probe);
        });
    }

    [Theory]
    [InlineData(AppLanguage.English, AppTheme.Light)]
    [InlineData(AppLanguage.English, AppTheme.Dark)]
    [InlineData(AppLanguage.Chinese, AppTheme.Light)]
    [InlineData(AppLanguage.Chinese, AppTheme.Dark)]
    public void Every_workbench_panel_realizes_its_bindings_without_path_errors(AppLanguage language, AppTheme theme)
    {
        wpf.RunWithLanguage(language, () =>
        {
            using var trace = new BindingTrace();
            var previousTheme = ThemeManager.Current.Theme;
            ThemeManager.Current.Theme = theme;
            var client = new FakeStudioClient();
            client.Blocks.Add(new BlockInfo { Name = "Protected", Path = "Group/Protected", Kind = BlockKind.FB,
                IsConsistent = false, IsKnowHowProtected = true, ProgrammingLanguage = "SCL", Number = 2 });
            client.StatusItems.AddRange(new[]
            {
                new MappedObjectInfo { Name = "Main", FilePath = "Main.xml", FileFormat = "SimaticML", CompareState = VcCompareState.Unequal },
                new MappedObjectInfo { Name = "Protected", FilePath = "Protected.xml", FileFormat = "SimaticML", CompareState = VcCompareState.WorkspaceFileMissing },
            });
            var dialogs = new FakeDialogService();
            var model = new MainViewModel(client, dialogs);
            var window = new MainWindow(model, false);
            var root = (FrameworkElement)window.Content;
            window.Content = null;
            var host = new Border { Child = root, DataContext = model, Width = 1200, Height = 780 };
            // Preserve ElementName=Root/ToWorkspace in styles when rehosting without a native window.
            NameScope.SetNameScope(host, NameScope.GetNameScope(window));
            try
            {
                Layout(host); // Empty engineering state and title-bar bindings.
                Assert.Equal(8, Find<ComboBox>(window, "ReleasePicker").Items.Count);
                RealizePickers(host);

                model.Session.ProjectPath = @"D:\fixture\Line.ap21";
                model.Session.OpenProject.Execute(null); // Synchronous fake replies populate all three children.
                Assert.True(model.Session.IsConnected);
                Assert.False(model.Busy);
                Assert.Equal(2, model.Engineering.Blocks.Count);
                model.Engineering.Blocks[0].Selected = true;
                model.Engineering.OutputDirectory = @"D:\fixture\exports";
                model.Activity.AppendDiagnostic("Main", "Binding fixture warning", TiaOpenness.Gui.Services.WorkbenchActivity.Severity.Warning);
                model.Activity.AppendLocalized("Status.CompileResult", "Warning", 0, 1, "1.0");
                model.Activity.AppendLocalized("Log.InspectionHeader", "PLC_1");
                model.Activity.AppendRule("NAMING-001", 1);
                model.Activity.AppendLocalized("Status.InspectResult", 1, 2);
                window.Navigate("Blocks");
                Layout(host);
                var blocks = Descendants<ListBox>(host).Single(list => ReferenceEquals(list.ItemsSource, model.Engineering.BlocksView));
                RealizeItems(host, blocks, 2);
                Assert.Contains(Descendants<TextBlock>(blocks), text => text.Text == "Protected");
                var diagnostics = Descendants<ItemsControl>(host).Single(items => BindingOperations.GetBinding(items, ItemsControl.ItemsSourceProperty)?.Path.Path == "Tag.Diagnostics");
                RealizeItems(host, diagnostics, 1);
                var rules = Descendants<ItemsControl>(host).Single(items => BindingOperations.GetBinding(items, ItemsControl.ItemsSourceProperty)?.Path.Path == "Tag.Rules");
                RealizeItems(host, rules, 2);
                Assert.Contains(Descendants<Button>(host), button => ReferenceEquals(button.Command, model.Engineering.Compile));
                var gate = new TaskCompletionSource();
                var operation = model.Activity.Guarded("Status.Compiling", () => gate.Task);
                try
                {
                    client.EmitProgress(new ProgressPayload { Operation = "Compile", Current = 1, Total = 2, Message = "fixture" });
                    Layout(host);
                    var progress = Assert.Single(Descendants<ProgressBar>(host), bar => bar.ActualHeight > 0);
                    Assert.True(progress.ActualWidth > 0 && progress.ActualHeight > 0);
                    Assert.Equal(2, progress.Maximum);
                    Assert.Equal(1, progress.Value);
                }
                finally { gate.SetResult(); Flush(); }
                Assert.True(operation.IsCompletedSuccessfully);

                window.Navigate("Engineering");
                var options = (TiaOpenness.Gui.Views.EngineeringOptionsView)((UserControl)window.FindName("OperationsContent")).FindName("Options");
                foreach (string section in new[] { "Project", "Export", "Inspect", "Workspace" })
                {
                    options.Show(section);
                    Layout(host);
                    var panel = (StackPanel)options.FindName(section + "Options");
                    Assert.True(panel.ActualHeight > 0);
                    RealizePickers(panel);
                    if (section == "Project")
                    {
                        var tree = Assert.Single(Descendants<TreeView>(panel));
                        ExpandTree(host, tree);
                        Assert.Contains(Descendants<TextBlock>(tree), text => text.Text.Contains("PLC_1", StringComparison.Ordinal));
                    }
                }
                options.Hide();

                window.Navigate("VersionControl");
                Layout(host);
                RealizeItems(host, Find<ListBox>(window, "MappedList"), 2);
                Assert.Same(model.VersionControl.SelectedVcItem, Find<ListBox>(window, "MappedList").SelectedItem);
                var diff = Descendants<ListBox>(host).Single(list => ReferenceEquals(list.ItemsSource, model.VersionControl.VcDiffLines));
                RealizeItems(host, diff, 1);
                Assert.Contains(Descendants<TextBlock>(diff), text => text.Text == "+fixture");
                RealizePickers(host);
                var otherFiles = Descendants<ItemsControl>(host).Single(items => BindingOperations.GetBinding(items, ItemsControl.ItemsSourceProperty)?.Path.Path == "Tag.OtherMappedFiles");
                RealizeItems(host, otherFiles, 1);

                var preview = Descendants<Button>(host).Single(button => ReferenceEquals(button.Style, window.FindResource("Glass.SyncPreview")));
                var run = Descendants<Button>(host).Single(button => ReferenceEquals(button.Style, window.FindResource("Glass.SyncRun")));
                foreach (bool toWorkspace in new[] { true, false, true })
                {
                    Find<RadioButton>(window, "ToWorkspace").IsChecked = toWorkspace;
                    Find<RadioButton>(window, "ToProject").IsChecked = !toWorkspace;
                    Layout(host);
                    Assert.True(preview.ActualWidth > 0 && run.ActualWidth > 0);
                    var expected = toWorkspace ? model.VersionControl.VcPush : model.VersionControl.VcPull;
                    Assert.Same(expected, preview.Command);
                    Assert.Same(expected, run.Command);
                }

                model.VersionControl.SelectedVcItem = null; // Also realize the no-diff explanation.
                model.VersionControl.VcStatusItems.Clear();
                Layout(host);
                window.Navigate("Log");
                Layout(host);

                window.ShowConfiguration(false);
                UnifiedDesktopTests.AssertPage(window, true);
                var configuration = window.Configuration!;
                foreach (string mode in new[] { "RemoteNav", "LocalNav" })
                {
                    Find<RadioButton>(configuration, mode).IsChecked = true;
                    Layout(host);
                    Assert.Equal(960, configuration.ActualWidth);
                    var clients = Find<ListBox>(configuration, "ClientChoices");
                    RealizeItems(host, clients, clients.Items.Count);
                    RealizePickers(configuration);
                    clients.SelectedItems.Clear();
                    clients.SelectedItems.Add(clients.Items[0]);
                    clients.SelectedItems.Add(clients.Items[1]);
                    Layout(host);
                }
                window.ShowEngineering();
                UnifiedDesktopTests.AssertPage(window, false);
                model.IsBlocksTab = true;
                model.Engineering.Blocks.Clear();
                Layout(host);
                Assert.Empty(dialogs.Messages); // No confirmation/native operation is needed for rendering.
            }
            finally
            {
                window.Close();
                host.Child = null;
                host.DataContext = null;
                ThemeManager.Current.Theme = previousTheme;
                Flush();
                output.WriteLine(trace.Text.Length == 0 ? "No WPF binding warnings or errors." : trace.Text);
            }
            // The pre-refactor baseline (8ecf201) has no diagnostics in these states; no allowlist.
            Assert.True(string.IsNullOrWhiteSpace(trace.Text), trace.Text);
        });
    }

    private static T Find<T>(FrameworkElement owner, string name) where T : FrameworkElement
        => (T)(owner is MainWindow window ? ProjectPageTestSupport.Find(window, name) : owner.FindName(name));

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }

    private static void Flush() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);

    private static void Layout(FrameworkElement host)
    {
        host.Measure(new Size(1200, 780));
        host.Arrange(new Rect(0, 0, 1200, 780));
        host.UpdateLayout();
        Flush();
        host.UpdateLayout();
        var bitmap = new RenderTargetBitmap(1200, 780, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(host);
    }

    private static void RealizeItems(FrameworkElement host, ItemsControl items, int expectedCount)
    {
        Assert.Equal(expectedCount, items.Items.Count);
        VirtualizingPanel.SetIsVirtualizing(items, false);
        Layout(host);
        for (int i = 0; i < items.Items.Count; i++)
        {
            var container = Assert.IsAssignableFrom<FrameworkElement>(items.ItemContainerGenerator.ContainerFromIndex(i));
            container.ApplyTemplate();
            Assert.NotEmpty(Descendants<TextBlock>(container));
        }
    }

    private static void RealizePickers(FrameworkElement root)
    {
        foreach (var picker in Descendants<ComboBox>(root).Where(picker => picker.ActualWidth > 0).ToArray())
        {
            picker.ApplyTemplate();
            // Materialize the real popup subtree offscreen without showing a native popup window.
            var popup = Assert.IsType<Popup>(picker.Template.FindName("PART_Popup", picker));
            var child = Assert.IsAssignableFrom<FrameworkElement>(popup.Child);
            child.Measure(new Size(600, 800));
            child.Arrange(new Rect(child.DesiredSize));
            child.UpdateLayout();
            Flush();
            for (int i = 0; i < picker.Items.Count; i++)
            {
                var container = Assert.IsType<ComboBoxItem>(picker.ItemContainerGenerator.ContainerFromIndex(i));
                Assert.NotEmpty(Descendants<TextBlock>(container));
            }
        }
    }

    private static void ExpandTree(FrameworkElement host, ItemsControl parent)
    {
        Layout(host);
        for (int i = 0; i < parent.Items.Count; i++)
        {
            var item = Assert.IsType<TreeViewItem>(parent.ItemContainerGenerator.ContainerFromIndex(i));
            item.IsExpanded = true;
            ExpandTree(host, item);
        }
    }
}
