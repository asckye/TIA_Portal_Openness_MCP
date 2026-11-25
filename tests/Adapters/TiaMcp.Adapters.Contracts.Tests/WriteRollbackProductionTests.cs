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

        [Theory, InlineData("create"), InlineData("rename"), InlineData("deleteEmpty")]
        public void Device_group_failure_after_native_change_keeps_unknown(string action)
        {
            var engine = HardwareWriteFailureTests.NativeFixture().Engine;
            var root = new FaultGroup();
            var original = new FaultGroup("Group", root.Groups);
            root.Groups.Add(original);
            var result = engine.RunHmiStep("ManageDeviceUserGroup", meta => {
                meta["mayHaveChanged"] = false;
                meta["result"] = HardwareGroupOperations.Manage(root, action == "create" ? "New" : "Group", action, "New", false, "Devices");
                return "unreachable";
            }, true);
            Assert.True(result.OutcomeUnknown);
            Assert.True(result.MayHaveChanged);
            if (action == "create") Assert.Contains(root.Groups, group => group.Name == "New");
            else if (action == "rename") Assert.Equal("New", original.Name);
            else Assert.DoesNotContain(original, root.Groups);
        }

        [Theory, InlineData("create"), InlineData("rename"), InlineData("deleteEmpty")]
        public void Refused_device_group_write_preserves_the_unchanged_inventory(string action)
        {
            var engine = HardwareWriteFailureTests.NativeFixture().Engine;
            var root = new FaultGroup();
            root.Groups.Refuse = true;
            root.Groups.Add(new FaultGroup("Group", root.Groups));
            var result = engine.RunHmiStep("ManageDeviceUserGroup", meta => {
                meta["mayHaveChanged"] = false;
                HardwareGroupOperations.Manage(root, action == "create" ? "New" : "Group", action, "New", false, "Devices");
                return "unreachable";
            }, true);
            Assert.Equal("Group", Assert.Single(root.Groups).Name);
            Assert.False(result.MayHaveChanged);
            Assert.False(result.OutcomeUnknown);
        }

        [Fact]
        public void Refusing_a_nested_create_keeps_the_created_parent_in_the_result()
        {
            var engine = HardwareWriteFailureTests.NativeFixture().Engine;
            var root = new FaultGroup();
            root.Groups.CreateParent = true;
            var result = engine.RunHmiStep("ManageDeviceUserGroup", meta => {
                HardwareGroupOperations.Manage(root, "Parent/Child", "create", "", false, "Devices");
                return "unreachable";
            }, true);
            Assert.Equal("Parent", Assert.Single(root.Groups).Name);
            Assert.Empty(root.Groups[0].Groups);
            Assert.True(result.MayHaveChanged);
            Assert.True(result.OutcomeUnknown);
            Assert.Equal("ManageDeviceUserGroup failed", result.Message);
            Assert.Equal("Type group creation failed at 'Parent/Child'. Groups already created (not rolled back): [\"Parent\"]. Native operation refused.",
                result.Meta["nativeMessage"]);
        }

        [Fact]
        public void Refused_group_with_unreadable_fresh_inventory_is_unknown()
        {
            var engine = HardwareWriteFailureTests.NativeFixture().Engine;
            var root = new FaultGroup();
            var groups = root.Groups;
            groups.Refuse = true;
            groups.OnRefusal = () => root.FailGroupRead = true;
            var result = engine.RunHmiStep("ManageDeviceUserGroup", meta => {
                HardwareGroupOperations.Manage(root, "Child", "create", "", false, "Devices");
                return "unreachable";
            }, true);
            Assert.Empty(groups);
            Assert.True(result.OutcomeUnknown);
        }

        private sealed class FaultGroups : List<FaultGroup>
        {
            public bool Refuse;
            public Action? OnRefusal;
            public bool CreateParent;
            public void Check()
            {
                if (!Refuse) return;
                OnRefusal?.Invoke();
                throw new InvalidOperationException("Native operation refused.");
            }
            public FaultGroup Create(string name)
            {
                Check();
                var child = new FaultGroup(name, this);
                Add(child);
                if (!CreateParent) throw new InvalidOperationException("Create reply lost.");
                child.Groups.Refuse = true;
                return child;
            }
        }
        private sealed class FaultGroup(string originalName = "Root", FaultGroups? parent = null)
        {
            private string name = originalName;
            public string Name { get => name; set { parent!.Check(); name = value; throw new InvalidOperationException("Rename reply lost."); } }
            private readonly FaultGroups groups = new();
            public bool FailGroupRead;
            public FaultGroups Groups => !FailGroupRead ? groups : throw new IOException("Group inventory unavailable.");
            public object[] Devices => Array.Empty<object>();
            public void Delete() { parent!.Check(); parent.Remove(this); throw new InvalidOperationException("Delete reply lost."); }
        }

        [Fact]
        public void Cpu_setting_readback_must_match_the_requested_value()
        {
            var engine = HardwareWriteFailureTests.NativeFixture().Engine;
            engine.RollbackCpu = new IgnoredCpuWrite();
            var result = engine.HardwareLegacySetCpuCommonSettings("PLC/CPU", new() {
                ["Value"] = new HardwareScalar { Kind = "number", Text = "2" } });
            Assert.False(Equals(result.Meta["success"], true));
            Assert.Empty((IEnumerable)result.Meta["applied"]!);
        }

        [Fact]
        public void Cpu_normalization_preserves_the_other_applied_setting()
        {
            var engine = HardwareWriteFailureTests.NativeFixture().Engine;
            var cpu = new NormalizingCpu();
            engine.RollbackCpu = cpu;
            var result = engine.HardwareLegacySetCpuCommonSettings("PLC/CPU", new() {
                ["Accepted"] = new HardwareScalar { Kind = "number", Text = "2" },
                ["Masked"] = new HardwareScalar { Kind = "number", Text = "3" } });
            Assert.Equal(2, cpu.Accepted);
            Assert.Single(((IEnumerable)result.Meta["applied"]!).Cast<object>());
            var rejected = Assert.IsType<Dictionary<string, object?>>(Assert.Single(((IEnumerable)result.Meta["rejected"]!).Cast<object>()));
            Assert.Equal("3", rejected["requestedValue"]);
            Assert.Equal("1", rejected["newValue"]);
            Assert.Equal(true, result.Meta["postStateKnown"]);
            Assert.False(result.OutcomeUnknown);
        }

        [Fact]
        public void Cpu_setting_with_an_equivalent_numeric_readback_is_applied()
        {
            var engine = HardwareWriteFailureTests.NativeFixture().Engine;
            engine.RollbackCpu = new NormalizingCpu();
            var result = engine.HardwareLegacySetCpuCommonSettings("PLC/CPU", new() { ["Accepted"] = new HardwareScalar { Kind = "number", Text = "2" } });
            Assert.Equal(true, result.Meta["success"]);
            Assert.Single(((IEnumerable)result.Meta["applied"]!).Cast<object>());
        }

        private sealed class NormalizingCpu : Siemens.Engineering.HW.HardwareObject
        {
            internal int Accepted;
            public override IEnumerable<Siemens.Engineering.EngineeringAttributeInfo> GetAttributeInfos()
                => new[] { "Accepted", "Masked" }.Select(name => new Siemens.Engineering.EngineeringAttributeInfo { Name = name });
            public override object GetAttribute(string name) => name == "Accepted" ? (long)Accepted : 1L;
            public override void SetAttribute(string name, object? value) { if (name == "Accepted") Accepted = Convert.ToInt32(value); }
        }

        [Fact]
        public void Cpu_refused_setter_with_verified_original_value_is_definite()
        {
            var engine = HardwareWriteFailureTests.NativeFixture().Engine;
            engine.RollbackCpu = new IgnoredCpuWrite { Refuse = true };
            var result = engine.HardwareLegacySetCpuCommonSettings("PLC/CPU", new() { ["Value"] = new HardwareScalar { Kind = "number", Text = "2" } });
            Assert.Empty((IEnumerable)result.Meta["applied"]!);
            Assert.False(Equals(result.Meta["mayHaveChanged"], true));
            Assert.False(Equals(result.Meta.GetValueOrDefault("writeOutcomeUnknown"), true));
        }

        private sealed class IgnoredCpuWrite : Siemens.Engineering.HW.HardwareObject
        {
            public override IEnumerable<Siemens.Engineering.EngineeringAttributeInfo> GetAttributeInfos()
                => new[] { new Siemens.Engineering.EngineeringAttributeInfo { Name = "Value" } };
            public override object GetAttribute(string name) => 1;
            public bool Refuse;
            public override void SetAttribute(string name, object? requested) { if (Refuse) throw new InvalidOperationException("Setter refused."); }
        }

        private sealed class PlantNodes : List<object>
        {
            public int Attempts { get; private set; }
            public object Create(string name, string type)
            {
                Attempts++;
                throw NativeRefusal("Unknown plant object type.");
            }
        }

        private static Siemens.Engineering.EngineeringException NativeRefusal(string message)
        {
            var error = new Siemens.Engineering.EngineeringException(message);
            error.MessageData.Text = message;
            error.MessageData.DetailText = "";
            return error;
        }

        [Fact]
        public void A_refused_child_plant_node_create_verifies_absence_and_preserves_the_native_reason()
        {
            var nodes = new PlantNodes();
            var step = HardwareWriteFailureTests.NativeFixture().Engine.RunHmiStep("ManageUnifiedPlantNode", meta => {
                meta["mayHaveChanged"] = true;
                PlcGroupOperations.CreateNamed(nodes, "/G/Node", "Node", new[] { typeof(string), typeof(string) }, "MCP_B5_Unknown");
                return "Created.";
            }, true);
            Assert.Equal(1, nodes.Attempts);
            Assert.Empty(nodes);
            Assert.False(step.OutcomeUnknown);
            Assert.False(step.MayHaveChanged);
            Assert.Equal("restored", step.Meta["rollbackOutcome"]);
            Assert.Equal("Unknown plant object type.", step.Meta["nativeMessage"]);
            Assert.Equal("NATIVE_OPERATION_FAILED", step.Meta["v4FailureCode"]);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Refused_network_lease_records_the_failure_before_subnet_lookup(bool lost)
        {
            var engine = HardwareWriteFailureTests.NativeFixture().Engine;
            engine.NetworkLeaseError = lost ? new System.Runtime.InteropServices.COMException("The portal connection was lost.")
                : new InvalidOperationException("Fixture lease refused.");
            var report = engine.HardwareProbeConnectDeviceNodesToSubnet("PLC", "HMI", "LAN");
            Assert.Contains("Rollback evidence:", report);
            using var document = JsonDocument.Parse(report.Split("Rollback evidence: ")[1]);
            var evidence = document.RootElement;
            Assert.Equal(lost ? "CONNECTION_UNAVAILABLE" : "PRECONDITION_FAILED", evidence.GetProperty("failureCode").GetString());
            Assert.False(evidence.GetProperty("mayHaveChanged").GetBoolean());
            Assert.Contains(lost ? "connection unavailable" : "exclusive access refused", report);
            foreach (var reply in new[] { engine.HardwareEnsureSubnet("CPU", "PN", "LAN"), engine.HardwareAttachDeviceNodeToSubnet("CPU", 0, "LAN") })
            {
                Assert.Equal(lost ? "CONNECTION_UNAVAILABLE" : "PRECONDITION_FAILED", reply.Meta["failureCode"]);
                Assert.Equal(lost, reply.Meta.ContainsKey("connectionUnavailable"));
                Assert.False(reply.MayHaveChanged);
                Assert.False(reply.OutcomeUnknown);
                Assert.False(reply.Meta.ContainsKey("readback"));
            }
        }

        public static HardwareAddressingReply RefusedWatchImport()
            => HardwareWriteFailureTests.NativeFixture().Engine.RunHmiStep("ImportPlcWatchTableOffline", meta => {
                meta["mayHaveChanged"] = true;
                TiaMcp.Adapters.Native.Engineering.EngineeringWriteJournal.Creating(() => null, "G/T");
                throw NativeRefusal("Watch table import refused.");
            }, true);

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
            Assert.True(result.OutcomeUnknown);
            Assert.NotEmpty((IEnumerable)result.Meta["cleanupRequired"]!);
        }

        [Fact]
        public void Typed_refusal_after_reachable_readback_is_partial_without_reset()
        {
            var result = LegacyFailure("SetPlcCpuSettings", "refused");
            Assert.Equal("partial", result.Meta["outcome"]);
            Assert.True(result.MayHaveChanged);
            Assert.False(result.OutcomeUnknown);
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
            Assert.False(result.OutcomeUnknown);
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
            Assert.True(result.OutcomeUnknown);
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
            Assert.False(result.OutcomeUnknown);
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
            Assert.False(result.OutcomeUnknown);
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
            Assert.False(result.OutcomeUnknown);
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
        public Siemens.Engineering.SW.PlcSoftware ExactPlcForEngineering(string path, bool writing) =>
            path == software.Name ? software : throw new ArgumentException("Unknown PLC.", nameof(path));
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
        private Siemens.Engineering.HW.Subnet? HardwareFindSubnet(string name)
            => project?.Subnets.SingleOrDefault(s => s.Name == name) ?? RollbackSubnets.SingleOrDefault(s => s.Name == name);
        private object HardwareBuildSubnetReadbackJson(string name) => new Dictionary<string, object?> { ["name"] = name };
        private string HardwareExceptionDetail(Exception error) => error.ToString();
        private sealed class NetworkNodeInfo { public object? Node; }
        private IEnumerable<NetworkNodeInfo> FindNetworkNodes(object root) => new[] { new NetworkNodeInfo { Node = RollbackNode } };
        private static string HardwareFormatNodeInfo(NetworkNodeInfo node) => "PN";
        private static List<object?> HardwareLineArray(IEnumerable<string> lines) => lines.Cast<object?>().ToList();
    }
}
