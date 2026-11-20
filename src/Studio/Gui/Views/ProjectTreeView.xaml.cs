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
    public override Style SelectStyle(object item, DependencyObject container) => (Style)((FrameworkElement)container)
        .FindResource(item is ProjectLink ? "Primer.ProjectLinkNode" : "Primer.ProjectNode");
}

public partial class ProjectTreeView : UserControl
{
    private bool _updateQueued;
    internal Func<string, bool, bool> OpenPage { private get; set; } = null!;
    public ProjectTreeView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is MainViewModel old)
            {
                old.Engineering.PropertyChanged -= OnEngineeringChanged;
                old.VersionControl.VcStatusItems.CollectionChanged -= OnWorkspaceChanged;
            }
            if (e.NewValue is MainViewModel model)
            {
                model.Engineering.PropertyChanged += OnEngineeringChanged;
                model.VersionControl.VcStatusItems.CollectionChanged += OnWorkspaceChanged;
                QueueModifiedIndicators();
            }
        };
    }

    private void OnEngineeringChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(EngineeringViewModel.HasBlocks)) QueueModifiedIndicators();
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
            if (model.Engineering.Devices.Count(device => device.Category == "Plc") == 1)
            {
                changed.UnionWith(model.VersionControl.VcStatusItems.GroupBy(item => item.Name)
                    .Where(group => group.Count() == 1 && group.First().CompareState == VcCompareState.Unequal).Select(group => group.Key));
                changed.ExceptWith(model.Engineering.Blocks.GroupBy(row => row.Name).Where(group => group.Count() > 1).Select(group => group.Key));
            }
            foreach (var root in model.Engineering.BlockTree) Mark(root, changed);
        }));
    }

    private static void Mark(BlockNode node, HashSet<string> changed)
    {
        node.IsModified = node.Row != null && changed.Contains(node.Name);
        foreach (var child in node.Children) Mark(child, changed);
    }

    private async void OnNodeClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source || ItemsControl.ContainerFromElement(null, source) is not TreeViewItem item) return;
        for (var part = source; part != null && part != item; part = part is ContentElement content
            ? ContentOperations.GetParent(content) : VisualTreeHelper.GetParent(part))
        {
            if (part is CheckBox) return;
            if (part is not ToggleButton) continue;
            if (item.IsExpanded || item.Header is not DeviceNode { Device.Category: "Plc" }) return;
            // Opening the PLC selects it and expands it. Consume this click so the expander cannot toggle it back.
            if (DataContext is MainViewModel { Busy: false }) e.Handled = true;
            break;
        }
        item.Focus();
        await ActivateAsync(item);
    }

    private async void OnNodeKey(object sender, KeyEventArgs e)
    {
        if (e.OriginalSource is not TreeViewItem item) return;
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
        else await ActivateAsync(item);
    }

    private async Task ActivateAsync(TreeViewItem item)
    {
        if (DataContext is not MainViewModel model) return;
        var engineering = model.Engineering;
        switch (item.Header)
        {
            case ProjectLink { IsVersionControl: true } when !model.Busy:
                OpenPage("VersionControl", false);
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
