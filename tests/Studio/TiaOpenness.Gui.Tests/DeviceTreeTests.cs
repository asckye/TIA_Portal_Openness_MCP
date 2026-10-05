using System.Collections.Generic;
using System.Linq;
using TiaOpenness.Contracts.Models;
using TiaOpenness.Gui.ViewModels;
using Xunit;

namespace TiaOpenness.Gui.Tests;

/// <summary>
/// The device tree exists to mirror TIA's own project tree, so the thing worth pinning down is
/// that it does not quietly rearrange what it was given.
///
/// It did, for a while: folders were forced to the top and every level was sorted alphabetically
/// on the station name while the module name was displayed, so a correctly ordered project came
/// out looking shuffled. Nothing in the suite noticed, because nothing tested this class.
/// </summary>
public class DeviceTreeTests
{
    private static DeviceInfo Device(string name, string display = null!, string group = "",
        string category = "Plc", string type = null!)
        => new()
        {
            Id = name,
            Name = name,
            DisplayName = display ?? name,
            Category = category,
            GroupPath = group,
            TypeName = type!,
        };

    private static DeviceNode Root(params DeviceInfo[] devices)
        => Assert.Single(DeviceNode.Build(devices, "Line"));

    private static IReadOnlyList<string> Labels(DeviceNode node)
        => node.Children.Select(c => c.Label).ToList();

    [Fact]
    public void An_empty_project_has_no_root_so_the_caller_can_show_its_empty_state()
    {
        Assert.Empty(DeviceNode.Build([], "Line"));
    }

    [Fact]
    public void The_root_is_the_project_and_carries_every_device_under_it()
    {
        var root = Root(Device("PLC_1"), Device("HMI_1", category: "Hmi"));

        Assert.True(root.IsRoot);
        Assert.Equal("Line", root.Label);
        Assert.Equal("(2)", root.Detail);
    }

    /// <summary>The defect this class was rebuilt for: the order given is the order shown.</summary>
    [Fact]
    public void Devices_keep_the_order_the_project_gave_them()
    {
        var root = Root(
            Device("HMI_1", "HMI_RT_1"),
            Device("HMI_3", "HMI_RT_3"),
            Device("HMI_4", "HMI_RT_4"),
            Device("HMI_2", "HMI_RT_2"));

        Assert.Equal(["HMI_RT_1", "HMI_RT_3", "HMI_RT_4", "HMI_RT_2"], Labels(root));
    }

    /// <summary>
    /// Sorting on the station while showing the module is what made an ordered project look
    /// shuffled, so a case where the two disagree is held explicitly.
    /// </summary>
    [Fact]
    public void A_station_named_differently_from_its_module_does_not_reorder_anything()
    {
        var root = Root(
            Device("S71500/ET200MP-Station_1", "FA3572"),
            Device("A_Station", "ZZ_Module"),
            Device("Z_Station", "AA_Module"));

        Assert.Equal(["FA3572", "ZZ_Module", "AA_Module"], Labels(root));
    }

    [Fact]
    public void A_group_appears_where_its_first_device_does_not_hoisted_above_the_others()
    {
        var root = Root(
            Device("PLC_1"),
            Device("SW_1", group: "Network", category: "Other"),
            Device("HMI_1", category: "Hmi"));

        Assert.Equal(["PLC_1", "Network", "HMI_1"], Labels(root));
    }

    [Fact]
    public void Nested_groups_become_nested_folders()
    {
        var root = Root(Device("SW_1", group: "Grouped_Devices/_Devices/OPMODE01", category: "Other"));

        var level1 = Assert.Single(root.Children);
        Assert.Equal("Grouped_Devices", level1.Label);
        Assert.True(level1.IsFolder);

        var level2 = Assert.Single(level1.Children);
        Assert.Equal("_Devices", level2.Label);

        var level3 = Assert.Single(level2.Children);
        Assert.Equal("OPMODE01", level3.Label);

        var device = Assert.Single(level3.Children);
        Assert.Equal("SW_1", device.Label);
        Assert.False(device.IsFolder);
    }

    [Fact]
    public void Devices_in_the_same_group_share_one_folder()
    {
        var root = Root(
            Device("SW_1", group: "Network", category: "Other"),
            Device("SW_2", group: "Network", category: "Other"));

        var group = Assert.Single(root.Children);
        Assert.Equal(["SW_1", "SW_2"], Labels(group));
        Assert.Equal("(2)", group.Detail);
    }

    [Fact]
    public void A_device_without_a_module_name_falls_back_to_the_station()
    {
        var root = Root(new DeviceInfo { Id = "PLC_1", Name = "PLC_1", DisplayName = "", Category = "Plc" });

        Assert.Equal("PLC_1", Assert.Single(root.Children).Label);
    }

    /// <summary>TIA prints the module type in brackets after the name.</summary>
    [Fact]
    public void A_device_shows_its_type_in_brackets()
    {
        var root = Root(Device("PLC_1", type: "CPU 1517F-3 PN/DP"));

        Assert.Equal("[CPU 1517F-3 PN/DP]", Assert.Single(root.Children).Detail);
    }

    [Fact]
    public void A_device_with_no_type_falls_back_to_its_article_number()
    {
        var device = Device("SW_1", category: "Other");
        device.ArticleNumber = "6GK5 208-0BA00-2AB2";

        Assert.Equal("6GK5 208-0BA00-2AB2", Assert.Single(Root(device).Children).Detail);
    }

    [Fact]
    public void The_category_is_carried_through_for_devices_and_blank_for_folders()
    {
        var root = Root(Device("PLC_1"), Device("SW_1", group: "Network", category: "Other"));

        Assert.Equal("Plc", root.Children[0].Category);
        Assert.Equal(string.Empty, root.Children[1].Category);
        Assert.Equal(string.Empty, root.Category);
    }
}
