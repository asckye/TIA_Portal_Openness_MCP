using Siemens.Engineering.HW;
using TiaMcp.Adapters;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Hardware;
using Xunit;

namespace Siemens.Engineering.HW
{
    public enum AddressIoType { None, Input, Output }
    public sealed class Address
    {
        public AddressIoType IoType { get; set; }
        public int Length { get; set; }
        private int start;
        internal Action? OnSet;
        public int StartAddress { get => start; set { OnSet?.Invoke(); start = value; } }
    }

    public sealed class AttributeInfo { public string Name => "Value"; public string AccessMode => "ReadWrite"; }

    public partial class DeviceItem
    {
        internal Func<int> ReadValue = () => 1;
        internal Action<object?> SetValue = _ => throw new InvalidOperationException("Attribute refused");
        public AttributeInfo[] GetAttributeInfos() => new[] { new AttributeInfo() };
        public object GetAttribute(string name) => ReadValue();
        public void SetAttribute(string name, object? value) => SetValue(value);
        public List<Address> Addresses { get; } = new();
    }
}

namespace TiaMcp.Adapters
{
    public sealed partial class PlcFoundationEngine
    {
        internal DeviceItem Module { get; } = new();
        private DeviceItem? HardwareLegacyItem(string path) => path == "PLC/IO" ? Module : null;
        private bool HardwareProjectMissing() => !Bound;
        private void Check() { }
        private DeviceItem? HardwareResolveItem(string path) => HardwareLegacyItem(path);
        private static Dictionary<string, object?> HardwareStepMeta(DateTime time, bool success) => new() { ["success"] = success };
        private static List<object?> HardwareToArray(IEnumerable<string> values) => values.Cast<object?>().ToList();
        private static string HardwareAttributeTypes(AttributeInfo info) => "System.Int32";
        private static bool HardwareAttributeWritable(AttributeInfo info) => info.AccessMode == "ReadWrite";
        private static string HardwareExceptionDetail(Exception error) => error.Message;
        private object HardwareBuildDeviceItemNetworkReadbackJson(string path) => new { path };
        private static bool HardwareTryCoerceAttributeValue(string value, object? oldValue, AttributeInfo info, out object typed, out string problem)
        {
            bool parsed = int.TryParse(value, out var number);
            typed = number;
            problem = parsed ? "" : "Expected an Int32";
            return parsed;
        }
    }
}

namespace TiaMcp.Engine.Tests
{
    public sealed class HardwareReadbackTests
    {
        [Theory, InlineData(false), InlineData(true)]
        public void Complete_module_readback_detects_a_moved_output(bool moved)
        {
            var engine = new PlcFoundationEngine();
            var input = new Address { IoType = AddressIoType.Input, StartAddress = 2, Length = 1 };
            var output = new Address { IoType = AddressIoType.Output, StartAddress = 4, Length = 1 };
            engine.Module.Addresses.AddRange(new[] { input, output });
            input.OnSet = () => {
                if (moved) output.StartAddress = 8;
                throw new InvalidOperationException("Start address refused");
            };
            var reply = engine.SetHardwareIoAddress("PLC/IO", "Input", 8);
            Assert.False(reply.Ok);
            Assert.Equal(moved, reply.MayHaveChanged);
            Assert.Equal(moved, reply.OutcomeUnknown);
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public void Scalar_setters_verify_the_stored_value_on_net48(bool cpu, bool lost)
        {
            var engine = new PlcFoundationEngine();
            engine.Module.SetValue = _ => throw (lost ? new TimeoutException() : new InvalidOperationException("Value refused"));
            var result = cpu
                ? engine.HardwareLegacySetCpuCommonSettings("PLC/IO", new() { ["Value"] = new HardwareScalar { Kind = "number", Text = "8" } })
                : engine.HardwareLegacySetDeviceItemAttribute("PLC/IO", "Value", "8");
            Assert.Equal(lost, result.OutcomeUnknown);
            Assert.Equal(lost, result.MayHaveChanged);
            if (!cpu && !lost) Assert.True(result.Meta.TryGetValue("apiCallSuccess", out var success) && success is false);
        }

        [Fact]
        public void Only_detached_matching_attribute_values_settle_a_failed_set()
        {
            var refused = new InvalidOperationException();
            var proxy = new object();
            Assert.True(HardwareAddressingPolicy.ScalarUnchanged(refused, true, 4, () => 4));
            Assert.False(HardwareAddressingPolicy.ScalarUnchanged(refused, true, 4, () => "4"));
            Assert.False(HardwareAddressingPolicy.ScalarUnchanged(refused, true, proxy, () => proxy));
            Assert.False(HardwareAddressingPolicy.ScalarUnchanged(refused, false, null, () => null));
            Assert.False(HardwareAddressingPolicy.ScalarUnchanged(refused, true, 4, () => throw new IOException()));
            Assert.False(HardwareAddressingPolicy.ScalarUnchanged(new TimeoutException(), true, 4, () => 4));
        }
    }
}
