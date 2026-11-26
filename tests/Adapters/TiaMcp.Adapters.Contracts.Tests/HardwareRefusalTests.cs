using TiaMcp.Adapters.Contracts;
using Siemens.Engineering;
using Siemens.Engineering.HW;
using Xunit;

namespace TiaMcp.Adapters.Contracts.Tests
{
    public sealed class HardwareRefusalTests
    {
        [Theory]
        [InlineData("unchanged")]
        [InlineData("changed")]
        [InlineData("unreadable-before")]
        [InlineData("unreadable-after")]
        [InlineData("timeout")]
        [InlineData("abort")]
        public void Attribute_set_requires_the_original_value(string failure)
        {
            var engine = HardwareWriteFailureTests.NativeFixture().Engine;
            var item = new AttributeTarget(failure);
            engine.RollbackCpu = item;
            var reply = engine.HardwareLegacySetDeviceItemAttribute("PLC/CPU", "Value", "8");
            Assert.Equal(1, item.Writes);
            Assert.Equal(failure != "unchanged", reply.OutcomeUnknown);
            Assert.Equal(failure != "unchanged", reply.MayHaveChanged);
        }

        [Theory]
        [InlineData("The name must contain a character.", "The name must contain a character.")]
        [InlineData("", "An empty name is not allowed.")]
        [InlineData(null, "TIA refused the name without a reason.")]
        [InlineData(null, "An empty name is not allowed.", " \t\u3000")]
        [InlineData("Names cannot consist of spaces.", "Names cannot consist of spaces.", "   ")]
        public void Empty_device_name_preserves_the_native_refusal(string? inner, string expected, string name = "")
        {
            var engine = HardwareWriteFailureTests.NativeFixture().Engine;
            var item = new NameTarget(inner);
            engine.RollbackCpu = item;
            var reply = engine.HardwareLegacySetDeviceItemAttribute("PLC/CPU", "Name", name);
            Assert.Equal(name, reply.Meta["requestedValue"]);
            Assert.Equal(1, item.Writes);
            Assert.False(reply.MayHaveChanged);
            Assert.Contains(expected, Assert.IsType<string>(reply.Meta["nativeMessage"]));
        }

        private sealed class NameTarget(string? inner) : HardwareObject
        {
            internal int Writes;
            public override IEnumerable<EngineeringAttributeInfo> GetAttributeInfos() => [new() { Name = "Name" }];
            public override object GetAttribute(string name) => "MCP_RC_PLC";
            public override void SetAttribute(string name, object? value)
            {
                Writes++;
                throw new EngineeringException("Error when calling method 'set_Name' of type 'Siemens.Engineering.HW.DeviceItemImpl'.",
                    inner == null ? null : new Exception(inner));
            }
        }

        [Fact]
        public void Aborted_cpu_write_with_matching_value_stays_unknown()
        {
            var engine = HardwareWriteFailureTests.NativeFixture().Engine;
            var item = new AttributeTarget("abort");
            engine.RollbackCpu = item;
            var reply = engine.HardwareLegacySetCpuCommonSettings("PLC/CPU", new() { ["Value"] = new HardwareScalar { Kind = "number", Text = "8" } });
            Assert.Equal(1, item.Writes);
            Assert.True(Equals(true, reply.Meta.GetValueOrDefault("writeOutcomeUnknown")));
        }

        private sealed class AttributeTarget(string failure) : HardwareObject
        {
            internal int Writes;
            public override IEnumerable<EngineeringAttributeInfo> GetAttributeInfos() => [new() { Name = "Value" }];
            public override object GetAttribute(string name) {
                if (failure == "unreadable-before" && Writes == 0 || failure == "unreadable-after" && Writes > 0)
                    throw new IOException("Value unavailable");
                return failure == "changed" && Writes > 0 ? 8 : 1;
            }
            public override void SetAttribute(string name, object? value) { Writes++; throw Failure(failure); }
        }

        [Theory]
        [InlineData("unchanged")]
        [InlineData("sibling-moved")]
        [InlineData("unreadable-after")]
        [InlineData("timeout")]
        [InlineData("abort")]
        public void Start_address_refusal_reads_all_rows(string failure)
        {
            var engine = HardwareWriteFailureTests.NativeFixture().Engine;
            var target = new Address { IoType = AddressIoType.Input, StartAddress = 2, Length = 1 };
            var sibling = new Address { IoType = AddressIoType.Output, StartAddress = 4, Length = 1 };
            engine.FamilyItem.Addresses.Add(target);
            engine.FamilyItem.Addresses.Add(sibling);
            int calls = 0;
            target.SettingStart = _ => {
                calls++;
                if (failure == "sibling-moved") sibling.StartAddress = 8;
                if (failure == "unreadable-after") sibling.ReadingStart = () => throw new IOException("Row unavailable");
                throw Failure(failure);
            };
            var reply = engine.SetHardwareIoAddress("PLC/CPU", "Input", 8);
            Assert.Equal(1, calls);
            Assert.False(reply.Ok);
            Assert.Equal(failure != "unchanged", reply.OutcomeUnknown);
            Assert.Equal(failure == "unchanged" ? 2 : (int?)null, reply.After?.StartAddress);
            Assert.Equal(failure != "unchanged", reply.MayHaveChanged);
        }

        private static Exception Failure(string failure) => failure switch {
            "timeout" => new TimeoutException(),
            "abort" => new EngineeringUserAbortException(),
            _ => new InvalidOperationException("Setter refused.")
        };

        private sealed class EngineeringUserAbortException : Exception { }
    }
}

namespace TiaMcp.Adapters
{
    public sealed partial class PlcFoundationEngine
    {
        private static List<object?> HardwareToArray(IEnumerable<string> values) => values.Cast<object?>().ToList();
    }
}
