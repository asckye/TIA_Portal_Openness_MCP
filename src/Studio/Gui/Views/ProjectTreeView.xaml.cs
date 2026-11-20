using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using TiaOpenness.Contracts.Models;
using TiaOpenness.Gui.ViewModels;

namespace TiaOpenness.Gui.Views;

public sealed class ProjectNodeStyleSelector : StyleSelector
{
    public Style Device { get; set; } = null!;
    public Style Block { get; set; } = null!;
    public Style Link { get; set; } = null!;
    public override Style SelectStyle(object item, DependencyObject container) => item switch
    {
        ProjectLink => Link, DeviceNode => Device, _ => Block,
    };
}

public partial class ProjectTreeView : UserControl
{
    private bool _updateQueued;
    private BlockNode? _contextGroup;
    private BlockRow? _anchor;
    internal event EventHandler? FilterClearRequested;
    internal void SetFilterPending(bool pending) => FilterMarker.SetPending(pending);
    internal Func<string, bool, bool> OpenPage { private get; set; } = null!;
    public ProjectTreeView()
    {
        InitializeComponent();
        FilterMarker.ClearRequested += (_, _) => FilterClearRequested?.Invoke(this, EventArgs.Empty);
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is MainViewModel old)
            {
                old.Session.PropertyChanged -= OnSessionChanged;
                old.PropertyChanged -= OnPageChanged;
                old.VersionControl.PropertyChanged -= OnVcChanged;
                old.Engineering.PropertyChanged -= OnEngineeringChanged;
                old.VersionControl.VcStatusItems.CollectionChanged -= OnWorkspaceChanged;
            }
            if (e.NewValue is MainViewModel model)
            {
                model.Session.PropertyChanged += OnSessionChanged;
                ProjectFile.Text = System.IO.Path.GetFileName(model.Session.OpenProjectFile);
                ProjectFile.ToolTip = model.Session.OpenProjectFile;
                model.PropertyChanged += OnPageChanged;
                model.VersionControl.PropertyChanged += OnVcChanged;
                model.Engineering.PropertyChanged += OnEngineeringChanged;
                model.VersionControl.VcStatusItems.CollectionChanged += OnWorkspaceChanged;
                QueueModifiedIndicators();
            }
        };
    }

    private void OnEngineeringChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(EngineeringViewModel.HasBlocks) or nameof(EngineeringViewModel.TagCount)
            or nameof(EngineeringViewModel.Document) or nameof(EngineeringViewModel.FocusedBlock) or nameof(EngineeringViewModel.SelectedDevice))
            QueueModifiedIndicators();
    }
    private void OnPageChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.IsVcTab)) QueueModifiedIndicators();
    }
    private void OnSessionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (DataContext is not MainViewModel model) return;
        ProjectFile.Text = System.IO.Path.GetFileName(model.Session.OpenProjectFile);
        ProjectFile.ToolTip = model.Session.OpenProjectFile;
    }
    private void OnVcChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(VersionControlViewModel.HasStatus)) QueueModifiedIndicators();
    }
    private void OnWorkspaceChanged(object? sender, NotifyCollectionChangedEventArgs e) => QueueModifiedIndicators();
    private void QueueModifiedIndicators()
    {
        if (_updateQueued) return;
        _updateQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.DataBind, new Action(() =>
        {
            _updateQueued = false;
            if (DataContext is not MainViewModel model) return;
            // Workspace status identifies objects by name, without their PLC. Mark only unambiguous matches.
            var changed = new HashSet<string>(StringComparer.Ordinal);
            if (model.VersionControl.HasStatus && model.Engineering.Devices.Count(device => device.Category == "Plc") == 1)
            {
                changed.UnionWith(model.VersionControl.VcStatusItems.GroupBy(item => item.Name)
                    .Where(group => group.Count() == 1 && group.First().CompareState == VcCompareState.Unequal).Select(group => group.Key));
                changed.ExceptWith(model.Engineering.Blocks.GroupBy(row => row.Name).Where(group => group.Count() > 1).Select(group => group.Key));
            }
            foreach (var device in model.Engineering.ProjectDevices.SelectMany(node => node.DeviceNodes()))
            foreach (var link in device.ProjectChildren.OfType<ProjectLink>())
            {
                link.Visible = !link.IsVersionControl || device.IsActive;
                bool tagsOpen = !model.IsVcTab && model.Engineering.Document == ProjectDocument.Tags;
                link.IsActive = device.IsActive && (link.IsVersionControl ? model.IsVcTab : tagsOpen);
                link.CountLabel = !device.IsActive ? "" : link.IsVersionControl
                    ? model.VersionControl.HasStatus ? model.VersionControl.ModifiedCount + " M" : ""
                    : model.Engineering.TagCount?.ToString() ?? "";
            }
            bool editorOpen = !model.IsVcTab && model.Engineering.Document != ProjectDocument.Tags;
            foreach (var root in model.Engineering.BlockTree) Mark(root, changed, editorOpen);
        }));
    }

    private static void Mark(BlockNode node, HashSet<string> changed, bool editorOpen)
    {
        node.IsActive = editorOpen && node.Row?.IsFocused == true;
        node.IsModified = node.Row != null && changed.Contains(node.Name);
        if (node.Row != null) node.Row.IsModified = node.IsModified;
        foreach (var child in node.Children) Mark(child, changed, editorOpen);
    }

    private async void OnNodeClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source || ItemsControl.ContainerFromElement(null, source) is not TreeViewItem item) return;
        for (var part = source; part != null && part != item; part = part is ContentElement content
            ? ContentOperations.GetParent(content) : VisualTreeHelper.GetParent(part))
        {
            if (part is CheckBox) break;
            if (part is not ToggleButton) continue;
            if (item.IsExpanded || item.Header is not DeviceNode { Device.Category: "Plc" }) return;
            // Opening the PLC selects it and expands it. Consume this click so the expander cannot toggle it back.
            if (DataContext is MainViewModel { Busy: false }) e.Handled = true;
            break;
        }
        item.Focus();
        if (item.Header is BlockNode node)
        {
            e.Handled = true;
            if (DataContext is not MainViewModel { Busy: false } model) return;
            if (node.IsFolder) { item.IsExpanded = !item.IsExpanded; return; }
            SelectRangeOrToggle(model.Engineering, node.Row!, Keyboard.Modifiers);
        }
        await ActivateAsync(item);
    }

    private async void OnNodeKey(object sender, KeyEventArgs e)
    {
        if (e.OriginalSource is not TreeViewItem item) return;
        if (e.Key == Key.A && Keyboard.Modifiers == ModifierKeys.Control && DataContext is MainViewModel { Busy: false } model)
        {
            model.Engineering.SelectAll(true); e.Handled = true; return;
        }
        if (e.Key is Key.Left or Key.Right)
        {
            e.Handled = true;
            var next = e.Key == Key.Left ? ItemsControl.ItemsControlFromItemContainer(item) as TreeViewItem
                : item.IsExpanded ? item.ItemContainerGenerator.ContainerFromIndex(0) as TreeViewItem : null;
            next?.Focus();
            return;
        }
        if (e.Key is not (Key.Enter or Key.Space)) return;
        e.Handled = true;
        if (item.Header is BlockNode { IsFolder: true } or DeviceNode { IsFolder: true }) item.IsExpanded = !item.IsExpanded;
        else
        {
            if (item.Header is BlockNode { Row: { } row })
            {
                if (DataContext is not MainViewModel { Busy: false }) return;
                if (e.Key == Key.Space) row.Selected = !row.Selected;
                _anchor = row;
            }
            await ActivateAsync(item);
        }
    }

    internal void SelectRangeOrToggle(EngineeringViewModel engineering, BlockRow row, ModifierKeys modifiers)
    {
        if ((modifiers & ModifierKeys.Shift) != 0)
        {
            var rows = engineering.BlockTree.SelectMany(node => node.Leaves()).ToList();
            int first = _anchor == null ? -1 : rows.IndexOf(_anchor);
            int last = rows.IndexOf(row);
            if (last < 0) return;
            if (first < 0) first = last;
            foreach (var member in rows.GetRange(Math.Min(first, last), Math.Abs(first - last) + 1)) member.Selected = true;
            return;
        }
        _anchor = row;
        if ((modifiers & ModifierKeys.Control) != 0) row.Selected = !row.Selected;
    }

    private void OnContextRow(object sender, MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(null, e.OriginalSource as DependencyObject) is TreeViewItem item) item.Focus();
    }
    private void OnContextOpening(object sender, RoutedEventArgs e)
    {
        _contextGroup = Nodes.SelectedItem as BlockNode;
        Nodes.ContextMenu.IsEnabled = DataContext is MainViewModel { Busy: false };
        SelectGroup.IsEnabled = ClearGroup.IsEnabled = _contextGroup?.IsFolder == true;
    }
    private void OnAll(object sender, RoutedEventArgs e) => ((MainViewModel)DataContext).Engineering.SelectAll(true);
    private void OnClear(object sender, RoutedEventArgs e) => ((MainViewModel)DataContext).Engineering.SelectAll(false);
    private void OnGroup(object sender, RoutedEventArgs e)
    {
        if (_contextGroup != null) _contextGroup.IsSelected = sender == SelectGroup;
    }
    private async Task ActivateAsync(TreeViewItem item)
    {
        if (DataContext is not MainViewModel model) return;
        var engineering = model.Engineering;
        switch (item.Header)
        {
            case ProjectLink { IsVersionControl: true } vcs when !model.Busy:
                if (OpenPage("VersionControl", false)) engineering.SelectedDevice = vcs.Device;
                break;
            case ProjectLink link when !model.Busy:
                if (!OpenPage("Blocks", false)) return;
                engineering.SelectedDevice = link.Device;
                engineering.Document = ProjectDocument.Tags;
                await engineering.ReadTagsAsync();
                break;
            case BlockNode { Row: { } row }:
                if (!OpenPage("Blocks", false)) return;
                engineering.Document = ProjectDocument.Render;
                engineering.FocusedBlock = row;
                await engineering.ReadFocusedBlockAsync();
                break;
            case DeviceNode { Device.Category: "Plc" } device when !model.Busy:
                if (!OpenPage("Blocks", true)) return;
                item.Focus();
                engineering.SelectedDevice = device.Device;
                device.IsExpanded = true;
                engineering.Document = ProjectDocument.Render;
                break;
        }
    }
}
