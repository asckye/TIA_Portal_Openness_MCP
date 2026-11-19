using System.Text;
using Siemens.Engineering.HW;
using TiaMcp.Adapters;
using Xunit;

public sealed class HardwareDeviceTreeTests
{
    [Fact]
    public void Overlapping_child_is_rendered_once_with_its_descendants()
    {
        var root = new DeviceItem();
        var module = new DeviceItem { Name = "Module" };
        module.DeviceItems.Add(new DeviceItem { Name = "Port" });
        root.Items.Add(module);
        root.Items.Add(new HardwareObject { Name = "Component" });
        root.DeviceItems.Add(module);
        var text = new StringBuilder();
        PlcFoundationEngine.BuildDeviceItemTree(text, root, new(), 0, 2);
        Assert.Equal(new[] { "├── Component [Hardware Component]", "└── Module [DeviceItem]", "    └── Port [DeviceItem]" },
            text.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
    }
}
