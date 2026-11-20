using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TiaOpenness.Contracts.Models;
using TiaOpenness.Gui.ViewModels;
using TiaOpenness.Gui.Views;
using Xunit;

namespace TiaOpenness.Gui.Tests;

public sealed partial class ProjectPageInteractionTests
{
    [Fact]
    public void Project_documents_keep_their_parents_across_tabs_and_breakpoints()
    {
        wpf.Run(() =>
        {
            var model = ProjectPageTestSupport.Model();
            var window = new MainWindow(model, false);
            try
            {
                var host = WorkbenchRenderTests.CreateHost(window, 1366, 860);
                foreach (int width in new[] { 1366, 1024, 1200 })
                {
                    host.Width = width;
                    host.Measure(new Size(width, 640)); host.Arrange(new Rect(0, 0, width, 640));
                    foreach (var document in new[] { ProjectDocument.Render, ProjectDocument.Tags, ProjectDocument.Compile })
                    {
                        model.Engineering.Document = document;
                        host.UpdateLayout(); WpfContext.Drain();
                        Assert.Same(window.DocumentHost, window.PlcTagsContent.Parent);
                        Assert.Same(window.BlockDocument, window.BlocksContent.Parent);
                        Assert.Same(window.CompileDocument, window.EngineeringSideContent.Parent);
                        Assert.Same(window.DocumentHost, window.BlockDocument.Parent);
                        Assert.Same(window.DocumentHost, window.CompileDocument.Parent);
                    }
                    var side = window.CompileDocument;
                    var blocks = window.BlockDocument;
                    if (width < 1200) Assert.True(blocks.TranslatePoint(new Point(), side).Y >= side.ActualHeight);
                    else
                    {
                        Assert.Equal(300, side.ActualWidth);
                        Assert.True(side.TranslatePoint(new Point(), blocks).X >= blocks.ActualWidth);
                        Assert.Equal(Visibility.Visible, window.FoldProjectContext.Visibility);
                        UnifiedDesktopTests.ClickControl(window.FoldProjectContext);
                        Assert.Equal(Visibility.Collapsed, side.Visibility);
                        UnifiedDesktopTests.ClickControl(window.FoldProjectContext);
                        Assert.Equal(Visibility.Visible, side.Visibility);
                    }
                }
            }
            finally { window.Close(); }
        });
    }

    private static void ClickNode(UIElement target)
    {
        void Raise(RoutedEvent preview, RoutedEvent bubble)
        {
            var args = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = preview };
            target.RaiseEvent(args);
            if (!args.Handled) target.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = bubble });
        }
        Raise(Mouse.PreviewMouseDownEvent, Mouse.MouseDownEvent);
        Raise(Mouse.PreviewMouseUpEvent, Mouse.MouseUpEvent);
    }

    private static void PressTreeKey(TreeViewItem target, Key key)
    {
        var source = PresentationSource.FromVisual(target);
        var preview = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        target.RaiseEvent(preview);
        if (!preview.Handled)
            target.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key) { RoutedEvent = Keyboard.KeyDownEvent });
    }

    private static (MainWindow Window, MainViewModel Model, FakeProjectClient Client) TwoPlcProject()
    {
        var client = new FakeProjectClient { AttachedProject = new ProjectInfo { Name = "Line", Path = @"D:\Line.ap21" } };
        client.Devices.Add(new() { Id = "PLC_2", Name = "PLC_2", Category = "Plc" });
        var model = new MainViewModel(client, new FakeDialogService());
        var window = new MainWindow(model, false) { Width = 1366, Height = 860 };
        model.SelectedReleaseKey = "21";
        model.Session.Connect.Execute(null);
        WpfContext.ShowWindow(window);
        WpfContext.Drain();
        client.Calls.Clear();
        return (window, model, client);
    }

    [Fact]
    public void Arrows_browse_plcs_tags_and_version_control_and_only_activation_opens_them()
    {
        wpf.Run(() =>
        {
            var (window, model, client) = TwoPlcProject();
            try
            {
                var tree = window.ProjectExplorer.Nodes;
                var first = (TreeViewItem)tree.ItemContainerGenerator.ContainerFromIndex(0);
                var second = (TreeViewItem)tree.ItemContainerGenerator.ContainerFromIndex(1);
                first.IsExpanded = false;
                Assert.True(first.Focus());
                PressTreeKey(first, Key.Down);
                Assert.Same(second, Keyboard.FocusedElement);
                PressTreeKey(second, Key.Right);
                Assert.False(second.IsExpanded);
                Assert.Same(client.Devices[0], model.Engineering.SelectedDevice);
                Assert.Empty(client.Calls);
                PressTreeKey(second, Key.Enter);
                WpfContext.Drain();
                Assert.Same(client.Devices[1], model.Engineering.SelectedDevice);
                Assert.Equal("Blocks", Assert.Single(client.Calls).Name);
                var links = WorkbenchRenderFeaturePagesTests.Descendants<TreeViewItem>(second).Where(item => item.Header is ProjectLink).ToArray();
                var tags = links[0];
                var vci = links[1];
                client.Calls.Clear();
                Assert.True(tags.Focus());
                PressTreeKey(tags, Key.Down);
                Assert.Same(vci, Keyboard.FocusedElement);
                Assert.Equal("Engineering", window.CurrentPage);
                PressTreeKey(vci, Key.Up);
                Assert.Same(tags, Keyboard.FocusedElement);
                Assert.Equal(ProjectDocument.Render, model.Engineering.Document);
                Assert.Empty(client.Calls);
                PressTreeKey(tags, Key.Space);
                Assert.Equal("Tags", Assert.Single(client.Calls).Name);
                Assert.Equal(ProjectDocument.Tags, model.Engineering.Document);
                PressTreeKey(tags, Key.Down);
                PressTreeKey(vci, Key.Enter);
                Assert.Equal("VersionControl", window.CurrentPage);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void A_slow_block_load_keeps_the_device_focused_and_only_disables_block_checkboxes()
    {
        wpf.Run(() =>
        {
            var (window, model, client) = TwoPlcProject();
            var pending = new TaskCompletionSource<List<BlockInfo>>();
            try
            {
                var tree = window.ProjectExplorer.Nodes;
                var first = (TreeViewItem)tree.ItemContainerGenerator.ContainerFromIndex(0);
                var second = (TreeViewItem)tree.ItemContainerGenerator.ContainerFromIndex(1);
                client.BlockHandler = _ => pending.Task;
                Assert.True(first.Focus());
                model.Engineering.Refresh.Execute(null);
                WpfContext.Drain();
                Assert.True(tree.IsEnabled);
                Assert.Same(first, Keyboard.FocusedElement);
                var boxes = WorkbenchRenderFeaturePagesTests.Descendants<CheckBox>(first).ToArray();
                Assert.NotEmpty(boxes);
                Assert.All(boxes, box => Assert.False(box.IsEnabled));
                pending.SetResult(client.Blocks); WpfContext.Drain();
                Assert.Same(first, Keyboard.FocusedElement);
                Assert.All(WorkbenchRenderFeaturePagesTests.Descendants<CheckBox>(first), box => Assert.True(box.IsEnabled));

                pending = new TaskCompletionSource<List<BlockInfo>>();
                ClickNode(second);
                Assert.Same(second, Keyboard.FocusedElement);
                Assert.True(model.Busy);
                pending.SetResult(client.Blocks); WpfContext.Drain();
                Assert.Same(second, tree.ItemContainerGenerator.ContainerFromIndex(1));
                Assert.Same(second, Keyboard.FocusedElement);
                Assert.Same(client.Devices[1], model.Engineering.SelectedDevice);
            }
            finally { pending.TrySetResult(client.Blocks); WpfContext.Drain(); window.Close(); }
        });
    }

    [Fact]
    public void Activating_a_plc_from_the_narrow_vci_flyout_keeps_focus_through_the_load()
    {
        wpf.Run(() =>
        {
            var (window, model, client) = TwoPlcProject();
            var blocks = new TaskCompletionSource<List<BlockInfo>>();
            try
            {
                window.Width = 1024;
                window.Navigate("VersionControl");
                WpfContext.Drain();
                UnifiedDesktopTests.ClickControl(window.LeftPaneToggle);
                var second = (TreeViewItem)window.ProjectExplorer.Nodes.ItemContainerGenerator.ContainerFromIndex(1);
                client.BlockHandler = _ => blocks.Task;
                ClickNode(second);
                Assert.Equal("Engineering", window.CurrentPage);
                Assert.Equal(Visibility.Visible, window.ProjectTree.Visibility);
                Assert.True(model.Busy);
                Assert.Same(second, Keyboard.FocusedElement);
                blocks.SetResult(client.Blocks); WpfContext.Drain();
                Assert.False(model.Busy);
                Assert.Same(second, Keyboard.FocusedElement);
            }
            finally { blocks.TrySetResult(client.Blocks); WpfContext.Drain(); window.Close(); }
        });
    }

    [Fact]
    public void Tags_wait_for_the_target_plc_blocks_and_exclude_competing_operations()
    {
        wpf.Run(() =>
        {
            var (window, model, client) = TwoPlcProject();
            var blocks = new TaskCompletionSource<List<BlockInfo>>();
            var tags = new TaskCompletionSource<List<TagInfo>>();
            try
            {
                var engineering = model.Engineering;
                var second = (TreeViewItem)window.ProjectExplorer.Nodes.ItemContainerGenerator.ContainerFromIndex(1);
                second.IsExpanded = true;
                window.UpdateLayout();
                var link = WorkbenchRenderFeaturePagesTests.Descendants<TreeViewItem>(second)
                    .Single(item => item.Header is ProjectLink { IsVersionControl: false });
                client.BlockHandler = _ => blocks.Task;
                client.TagHandler = _ => tags.Task;
                ClickNode(link);
                Assert.Equal("Blocks", Assert.Single(client.Calls).Name);
                Assert.True(model.Busy);
                blocks.SetResult(client.Blocks); WpfContext.Drain();
                Assert.Equal(new[] { "Blocks", "Tags" }, client.Calls.Select(call => call.Name));
                Assert.All(client.Calls, call => Assert.Equal("PLC_2", call.Arguments[0]));
                Assert.Same(link, second.ItemContainerGenerator.ContainerFromItem(link.Header));
                Assert.True(model.Busy);
                Assert.Equal(model.Activity.Status, engineering.TagStatus);
                Assert.False(window.OperationsContent.TagsTab.IsEnabled);
                Assert.False(((PlcTagsView)window.PlcTagsContent.Content).ReloadTags.IsEnabled);
                foreach (var command in new[] { engineering.Refresh, engineering.Save, engineering.Compile, engineering.Import })
                {
                    Assert.False(command.CanExecute(null));
                    command.Execute(null);
                }
                WpfContext.Complete(engineering.ReadTagsAsync());
                ClickNode(link);
                Assert.Equal(new[] { "Blocks", "Tags" }, client.Calls.Select(call => call.Name));
                tags.SetResult([new() { Name = "Ready", DataType = "Bool" }]); WpfContext.Drain();
                Assert.False(model.Busy);
                Assert.True(engineering.Save.CanExecute(null));
                Assert.Equal("Ready", Assert.Single(engineering.PlcTags).Name);
            }
            finally { blocks.TrySetResult(client.Blocks); tags.TrySetResult([]); WpfContext.Drain(); window.Close(); }
        });
    }

    [Fact]
    public void Finishing_a_nested_block_read_keeps_tags_gated_until_the_project_operation_finishes()
    {
        wpf.Run(() =>
        {
            var client = new FakeProjectClient();
            using var model = new MainViewModel(client, new FakeDialogService());
            var finish = new TaskCompletionSource();
            var connecting = model.Activity.Guarded("Status.Connecting", async () =>
            {
                await model.Engineering.LoadDevicesAsync("Line");
                await finish.Task;
            });
            try
            {
                Assert.Equal(new[] { "Devices", "Blocks" }, client.Calls.Select(call => call.Name));
                Assert.True(model.Busy);
                WpfContext.Complete(model.Engineering.ReadTagsAsync());
                Assert.DoesNotContain(client.Calls, call => call.Name == "Tags");
            }
            finally { finish.TrySetResult(); WpfContext.Complete(connecting); }
            Assert.False(model.Busy);
            WpfContext.Complete(model.Engineering.ReadTagsAsync());
            Assert.Single(client.Calls, call => call.Name == "Tags");
        });
    }
}
