using System.Collections;
using System.Text.Json;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Hardware;
using TiaMcp.Adapters.Native.Optional;
using TiaMcp.Adapters.Native.Plc;
using Xunit;

namespace TiaMcp.Adapters.Contracts.Tests
{
    public sealed class WriteRollbackProductionTests
    {
        public static HardwareAddressingReply LegacyFailure(string tool, string failure)
        {
            var fixture = HardwareWriteFailureTests.NativeFixture();
            if (tool != "SetPlcCpuSettings")
            {
                fixture.Engine.RollbackNode = new Siemens.Engineering.HW.Node { Name = "PN", FailWrite = failure == "write", FailUndo = failure == "undo" };
                fixture.Engine.RollbackNode.Attached = subnet => fixture.Engine.RollbackSubnets.Add(subnet);
                if (tool == "AttachDeviceNodeToSubnet") fixture.Engine.RollbackSubnets.Add(new Siemens.Engineering.HW.Subnet { Name = "PN" });
                return tool == "EnsureSubnet" ? fixture.Engine.HardwareEnsureSubnet("PLC/CPU", "PN", "PN")
                    : fixture.Engine.HardwareAttachDeviceNodeToSubnet("PLC/CPU", 0, "PN");
            }
            if (failure == "unknown")
            {
                fixture.Engine.RollbackCpu = new CpuTarget();
                return fixture.Engine.HardwareLegacySetCpuCommonSettings("PLC/CPU", new() {
                    ["Value"] = new HardwareScalar { Kind = "number", Text = "2" } });
            }
            var target = new LostTarget(failure);
            return fixture.Engine.RunHmiStep(tool, meta => {
                if (failure == "unknown") meta["writeOutcomeUnknown"] = true;
                HardwareScalarEvidence.Apply(target, new() { (typeof(LostTarget).GetProperty(nameof(LostTarget.Value))!, 2) }, meta);
                return "unreachable";
            }, true);
        }

        public static string LegacyFailureJson(string tool, string failure) => JsonSerializer.Serialize(LegacyFailure(tool, failure));

        [Theory]
        [InlineData("EnsureSubnet", "write")]
        [InlineData("AttachDeviceNodeToSubnet", "undo")]
        [InlineData("SetPlcCpuSettings", "unknown")]
        public void Lost_legacy_write_or_undo_preserves_unknown_and_reset(string tool, string failure)
        {
            var result = LegacyFailure(tool, failure);
            Assert.Equal("unknown", result.Meta["outcome"]);
            Assert.True(result.MayHaveChanged);
            Assert.True(result.RequiresSessionReset);
            Assert.NotEmpty((IEnumerable)result.Meta["cleanupRequired"]!);
        }

        [Fact]
        public void Typed_refusal_after_reachable_readback_is_partial_without_reset()
        {
            var result = LegacyFailure("SetPlcCpuSettings", "refused");
            Assert.Equal("partial", result.Meta["outcome"]);
            Assert.True(result.MayHaveChanged);
            Assert.False(result.RequiresSessionReset);
            Assert.Equal(true, result.Meta["rollbackReachable"]);
            Assert.Equal(true, result.Meta["rollbackRefused"]);
        }

        [Fact]
        public void Optional_create_from_result_is_deleted_after_later_property_failure()
        {
            var fixture = HardwareWriteFailureTests.NativeFixture();
            var collection = new Copies();
            var result = fixture.Engine.RunHmiStep("ManageSivarcTableRule", meta => {
                var item = (Copy)OptionalNativeOperations.Call(collection, "CreateFrom", "source")!;
                HardwareScalarEvidence.Apply(item, new() { (typeof(Copy).GetProperty(nameof(Copy.Value))!, 2) }, meta);
                return "unreachable";
            }, true);
            Assert.Empty(collection);
            Assert.Equal(true, result.Meta["rollbackCompleted"]);
            Assert.False(result.MayHaveChanged);
            Assert.False(result.RequiresSessionReset);
        }

        [Fact]
        public void Runtime_sequence_move_without_inverse_cannot_claim_full_restore()
        {
            var fixture = HardwareWriteFailureTests.NativeFixture();
            var item = new RuntimeItem();
            var result = fixture.Engine.RunHmiStep("ManageDccChart", meta => {
                HardwareScalarEvidence.Apply(item, new() { (typeof(RuntimeItem).GetProperty(nameof(RuntimeItem.Value))!, 2) }, meta);
                OptionalNativeOperations.Call(item, "MoveInRuntimeSequence", (uint)3);
                throw new InvalidOperationException("Later readback failed.");
            }, true);
            Assert.Equal(1, item.Value);
            Assert.Equal((uint)3, item.Sequence);
            Assert.Equal("unknown", result.Meta["outcome"]);
            Assert.True(result.MayHaveChanged);
            Assert.True(result.RequiresSessionReset);
            Assert.Contains("MoveInRuntimeSequence", JsonSerializer.Serialize(result.Meta["cleanupRequired"]));
        }

        [Fact]
        public void Successful_runtime_move_is_not_mistaken_for_failed_compensation()
        {
            var fixture = HardwareWriteFailureTests.NativeFixture();
            var item = new RuntimeItem();
            var result = fixture.Engine.RunHmiStep("ManageDccChart", meta => {
                meta["mayHaveChanged"] = true;
                OptionalNativeOperations.Call(item, "MoveInRuntimeSequence", (uint)3);
                return "Moved.";
            }, true);
            Assert.Equal((uint)3, item.Sequence);
            Assert.Equal(true, result.Meta["operationSuccess"]);
            Assert.False(result.RequiresSessionReset);
            Assert.False(result.Meta.ContainsKey("rollbackCompleted"));
        }

        [Fact]
        public void Optional_bulk_attributes_restore_earlier_changes_after_native_refusal()
        {
            var fixture = HardwareWriteFailureTests.NativeFixture();
            var item = new Attributes();
            var result = fixture.Engine.RunHmiStep("ManageSivarcTableRule", meta => {
                OptionalNativeOperations.Call(item, "SetAttributes", new List<KeyValuePair<string, object>> {
                    new("A", true), new("B", true) });
                return "unreachable";
            }, true);
            Assert.False(item.A);
            Assert.Equal(true, result.Meta["rollbackCompleted"]);
            Assert.False(result.MayHaveChanged);
            Assert.False(result.RequiresSessionReset);
        }

        public sealed class Attributes
        {
            public bool A { get; private set; }
            public object GetAttribute(string name) => name == "A" && A;
            public void SetAttribute(string name, object value) { if (name == "A") A = (bool)value; }
            public void SetAttributes(IEnumerable<KeyValuePair<string, object>> values)
            {
                foreach (var value in values)
                {
                    if (value.Key == "B") throw new NotSupportedException("Attribute B refused.");
                    SetAttribute(value.Key, value.Value);
                }
            }
        }

        public static HardwareAddressingReply GroupFailure()
        {
            var fixture = HardwareWriteFailureTests.NativeFixture();
            var software = new Siemens.Engineering.SW.PlcSoftware();
            var adapter = new PlcOrganisationAdapter(new GroupSession(fixture.Engine, software));
            var result = adapter.ManagePlcUserGroup("PLC", "blocks", "A/B/C", "create", dryRun: false);
            Assert.Empty(software.BlockGroup.Groups);
            return result;
        }

        public static string GroupFailureJson() => JsonSerializer.Serialize(GroupFailure());

        [Fact]
        public void Group_creation_propagates_verified_restore_through_production_step()
        {
            var result = GroupFailure();
            Assert.Equal(true, result.Meta["rollbackCompleted"]);
            Assert.False(result.MayHaveChanged);
            Assert.False(result.RequiresSessionReset);
        }

        public sealed class LostTarget(string failure)
        {
            private int value = 1;
            private bool dispatched;
            public int Value
            {
                get
                {
                    if (dispatched && failure == "write") throw new ObjectDisposedException("portal");
                    return value;
                }
                set
                {
                    if (dispatched && failure == "undo") throw new ObjectDisposedException("portal");
                    if (dispatched && failure == "refused") throw new NotSupportedException("Restoration refused.");
                    this.value = value;
                    dispatched = true;
                    if (failure == "write") throw new ObjectDisposedException("portal");
                    if (value == 2) throw new InvalidOperationException("Write outcome uncertain.");
                }
            }
        }

        public sealed class CpuTarget : Siemens.Engineering.HW.HardwareObject
        {
            private int value = 1;
            private bool failRead;
            public override IEnumerable<Siemens.Engineering.EngineeringAttributeInfo> GetAttributeInfos()
                => new[] { new Siemens.Engineering.EngineeringAttributeInfo { Name = "Value" } };
            public override object GetAttribute(string name)
            {
                if (failRead) { failRead = false; throw new InvalidOperationException("Readback unavailable."); }
                return value;
            }
            public override void SetAttribute(string name, object? requested)
            {
                value = (int)requested!;
                failRead = value == 2;
            }
        }

        public sealed class Copies : List<Copy>
        {
            public Copy CreateFrom(string source) { var copy = new Copy(this); Add(copy); return copy; }
        }

        public sealed class Copy(Copies owner)
        {
            public string Name => "CopiedRule";
            public int Value { get => 1; set => throw new NotSupportedException("Property refused."); }
            public void Delete() => owner.Remove(this);
        }

        public sealed class RuntimeItem
        {
            public int Value { get; set; } = 1;
            public uint Sequence { get; private set; }
            public void MoveInRuntimeSequence(uint index) => Sequence = index;
        }
    }
}

namespace TiaMcp.Adapters.Native.Plc
{
    public sealed partial class PlcOrganisationAdapter
    {
        private readonly GroupSession _session;
        internal PlcOrganisationAdapter(GroupSession session) => _session = session;
        private static Dictionary<string, object?> UserGroupRow(object group) => new();
    }

    internal sealed class GroupSession(PlcFoundationEngine engine, Siemens.Engineering.SW.PlcSoftware software)
    {
        public IDisposable AcquireHmiEditAccess() => new MemoryStream();
        public object ResolveSoftwareContainerUncached(string path) => software;
        public T? ResolvePlcService<T>(string path, Siemens.Engineering.SW.PlcSoftware plc) where T : class
            => new Siemens.Engineering.Online.OnlineProvider { State = Siemens.Engineering.Online.OnlineState.Offline } as T;
        public HardwareAddressingReply RunHmiStepTool(string tool, Func<Dictionary<string, object?>, string> action)
            => engine.RunHmiStep(tool, action, true);
    }

    internal static class PlcOrganisationPrimitives
    {
        internal static object SoftwareOrNull(object value) => value;
        internal static string Name(Siemens.Engineering.SW.PlcSoftware software) => software.Name;
        internal static object? StateOrNull(Siemens.Engineering.Online.OnlineProvider? provider) => provider?.State;
    }
}

namespace TiaMcp.Adapters
{
    public sealed partial class PlcFoundationEngine
    {
        internal Siemens.Engineering.HW.Node? RollbackNode;
        internal List<Siemens.Engineering.HW.Subnet> RollbackSubnets = new();
        internal Siemens.Engineering.HW.HardwareObject? RollbackCpu;
        private Siemens.Engineering.HW.HardwareObject? HardwareResolveItem(string path) => (Siemens.Engineering.HW.HardwareObject?)RollbackNode ?? RollbackCpu;
        private static bool HardwareAttributeWritable(Siemens.Engineering.EngineeringAttributeInfo info) => true;
        private static string HardwareAttributeTypes(Siemens.Engineering.EngineeringAttributeInfo info) => "Int32";
        private static bool HardwareTryCoerceAttributeValue(string text, object? old, Siemens.Engineering.EngineeringAttributeInfo info,
            out object value, out string problem) { value = int.Parse(text); problem = ""; return true; }
        private object HardwareBuildDeviceItemNetworkReadbackJson(string path) => new Dictionary<string, object?>();
        private Siemens.Engineering.HW.Subnet? HardwareFindSubnet(string name) => RollbackSubnets.SingleOrDefault(s => s.Name == name);
        private object HardwareBuildSubnetReadbackJson(string name) => new Dictionary<string, object?> { ["name"] = name };
        private string HardwareExceptionDetail(Exception error) => error.ToString();
        private static bool HardwareIsSupportedProfinetSubnetType(string value) => value == "PN";
        private sealed class NetworkNodeInfo { public object? Node; }
        private IEnumerable<NetworkNodeInfo> FindNetworkNodes(object root) => new[] { new NetworkNodeInfo { Node = RollbackNode } };
        private static string HardwareFormatNodeInfo(NetworkNodeInfo node) => "PN";
        private static List<object?> HardwareLineArray(IEnumerable<string> lines) => lines.Cast<object?>().ToList();
    }
}
