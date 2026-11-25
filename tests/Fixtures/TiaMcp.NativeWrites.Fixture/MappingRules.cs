using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Native.Engineering;
using TiaMcp.Adapters.Native.Plc;

namespace Siemens.Engineering.HW
{
    internal sealed class DeviceItem { public string Name => "Device/module"; }
}
namespace Siemens.Engineering.HW.Features
{
    internal sealed class TransferArea { public Rules TransferAreaMappingRules { get; } = new(); }
    internal sealed class Rules : List<TransferAreaMappingRule>
    {
        internal bool RefuseCreate;
        public TransferAreaMappingRule Create()
        {
            if (RefuseCreate) throw new InvalidOperationException("Mapping rule creation refused.");
            var rule = new TransferAreaMappingRule();
            Add(rule);
            return rule;
        }
    }
    internal sealed class TransferAreaMappingRule
    {
        public int Begin { get; set; }
        internal bool RefuseTarget;
        public DeviceItem? Target
        {
            get => target;
            set
            {
                if (RefuseTarget) throw new InvalidOperationException("Target setter refused.");
                target = value;
                throw new IOException("Target reply lost.");
            }
        }
        private DeviceItem? target;
        public void Delete() => throw new NotSupportedException();
    }
}
namespace TiaMcp.Adapters.Hardware
{
    internal static class HardwareAddressPrimitives
    {
        internal static string ProjectOwnerPath(object project, DeviceItem item) => item.Name;
        public static DeviceItem HardwareRequireDeviceItem(DeviceItem item, string parameter) => item;
    }
    internal static class HardwareGroupOperations
    {
        internal static IEnumerable<object> Items(object value) => PlcGroupOperations.Items(value);
    }
}
namespace TiaMcp.Adapters
{
    public sealed partial class PlcFoundationEngine
    {
        private readonly object project = new();
        private DeviceItem ExactHardware(string[] path, string[] item) => new DeviceItem();
        private string HardwareOwnerPath(DeviceItem item) => item.Name;
        private object HardwareMappingRuleRow(TransferAreaMappingRule rule, int index) => new { index, rule.Begin };
        private int? HardwareCountOnFresh(Func<object> read, Dictionary<string, object?> meta, string key)
            => PlcGroupOperations.Items(read()).Count();
    }
}
