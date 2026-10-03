using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using TiaOpenness.Contracts.Models;
using TiaOpenness.Gui.Common;

namespace TiaOpenness.Gui.ViewModels;

/// <summary>
/// One row of the device tree: the project at the root, the device groups the engineer filed
/// devices into, and the devices themselves — the shape TIA's own project tree has.
///
/// A flat list loses the grouping, and on a real plant that grouping is how people find anything.
/// </summary>
public sealed class DeviceNode : ObservableObject
{
    private bool _isExpanded = true;

    private DeviceNode(string name, DeviceInfo? device, bool isRoot = false)
    {
        Name = name;
        Device = device;
        IsRoot = isRoot;
    }

    public string Name { get; }

    /// <summary>The device this row stands for, or null for the project root and for groups.</summary>
    public DeviceInfo? Device { get; }

    public bool IsRoot { get; }

    public ObservableCollection<DeviceNode> Children { get; } = [];

    public bool IsFolder => Device is null;

    public bool IsExpanded
    {
        get => _isExpanded;
        set => Set(ref _isExpanded, value);
    }

    /// <summary>The name TIA shows: the module's, not the station's.</summary>
    public string Label => Device?.DisplayName is { Length: > 0 } display ? display : Name;

    /// <summary>What TIA prints after the name: the module type in brackets, or a count for a group.</summary>
    public string Detail
    {
        get
        {
            if (Device is null) return "(" + Devices().Count() + ")";
            return Device.TypeName is { Length: > 0 } type ? "[" + type + "]" : Device.ArticleNumber ?? string.Empty;
        }
    }

    /// <summary>"Plc", "Hmi", "Drive" or "Other"; empty for the root and for groups.</summary>
    public string Category => Device?.Category ?? string.Empty;

    private IEnumerable<DeviceInfo> Devices()
    {
        if (Device is not null)
        {
            yield return Device;
            yield break;
        }
        foreach (var device in Children.SelectMany(c => c.Devices())) yield return device;
    }

    /// <summary>
    /// Builds the tree. <paramref name="projectName"/> is the root, as in TIA; when nothing is
    /// open there is no root and the caller shows its empty state instead.
    /// </summary>
    public static IReadOnlyList<DeviceNode> Build(IEnumerable<DeviceInfo> devices, string projectName)
    {
        var list = devices.ToList();
        if (list.Count == 0) return [];

        var root = new DeviceNode(
            string.IsNullOrWhiteSpace(projectName) ? "—" : projectName, null, isRoot: true);

        foreach (var device in list) Place(root, device);
        return [root];
    }

    private static void Place(DeviceNode root, DeviceInfo device)
    {
        var parent = root;

        if (!string.IsNullOrEmpty(device.GroupPath))
        {
            foreach (var name in device.GroupPath.Split('/'))
            {
                var group = parent.Children.FirstOrDefault(c => c.IsFolder && c.Name == name);
                if (group is null)
                {
                    group = new DeviceNode(name, null);
                    parent.Children.Add(group);
                }
                parent = group;
            }
        }

        parent.Children.Add(new DeviceNode(device.Name, device));
    }

    // There is deliberately no sort here. The tree used to put folders first and order each
    // level alphabetically, which was wrong twice over: it inverted the project's own order, and
    // it sorted on Name - the station - while showing Label - the module - so a list that really
    // was ordered looked scrambled (HMI_RT_1, HMI_RT_3, HMI_RT_4, HMI_RT_2).
    //
    // Openness returns devices in the order the project holds them, which is the order TIA's own
    // tree shows. Mirroring that is the whole point of this view, so the order is left alone.
}
