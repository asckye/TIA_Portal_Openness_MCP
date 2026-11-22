using Siemens.Engineering.Hmi;
using Siemens.Engineering.HmiUnified;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Native.Hmi;
using TiaMcp.Adapters.Native.Optional;
using Xunit;

namespace TiaMcp.Adapters.Contracts.Tests
{
    public sealed class SivarcNativeTests
    {
        [Theory, InlineData("update", "Name"), InlineData("create", "Name"), InlineData("delete", "Enabled")]
        public void Rule_property_refusal_identifies_the_requested_action(string action, string key)
        {
            var changes = new Dictionary<string, object?> { [key] = key == "Name" ? "ReviewedRule" : true };
            var refusal = Assert.Throws<ArgumentException>(() => OptionalNativeOperations.ValidateRuleProperties(action, changes));
            Assert.Equal("properties", refusal.ParamName);
            Assert.Contains(action, refusal.Message);
            if (action != "delete") Assert.DoesNotContain("delete", refusal.Message);
            OptionalNativeOperations.ValidateRuleProperties("update", new Dictionary<string, object?> { ["Enabled"] = true });
        }

        private sealed record RuleFolder(string Name);
        private sealed class Folders : List<RuleFolder>
        {
            internal Action Creating = () => { };
            public RuleFolder Create(string name)
            {
                Creating();
                throw new InvalidOperationException("Native SiVArc licence refused.");
            }
        }

        private sealed class CreatableFolders : List<RuleFolder>
        {
            public RuleFolder Create(string name)
            {
                var folder = new RuleFolder(name);
                Add(folder);
                return folder;
            }
        }

        [Fact]
        public void Later_observation_failure_keeps_the_completed_legacy_create_in_the_journal()
        {
            var folders = new CreatableFolders();
            var reply = MutationEvidenceTests.RunStep("18", "ManageSivarcRuleContainer", meta => {
                OptionalNativeOperations.CreateLegacySivarcContainer(meta, () => folders, folders, "New", null);
                throw new InvalidOperationException("Could not read the created folder.");
            });
            Assert.Equal("New", Assert.Single(folders).Name);
            Assert.Equal(true, reply.Meta["unrestoredWrite"]);
        }

        [Fact]
        public void Unverified_legacy_create_retains_its_journal_cleanup()
        {
            var reply = ContainerRefusal(true, "added");
            Assert.Equal(true, reply.Meta["unrestoredWrite"]);
        }

        public static IEnumerable<object[]> ContainerCases()
        {
            foreach (bool legacy in new[] { true, false })
            foreach (string outcome in new[] { "unchanged", "renamed", "added", "unreadable-before", "unreadable-after" })
                yield return new object[] { legacy, outcome };
        }

        [Theory, MemberData(nameof(ContainerCases))]
        public void Container_create_refusal_requires_two_complete_matching_inventories(bool legacy, string outcome)
            => _ = ContainerRefusal(legacy, outcome);

        public static HardwareAddressingReply ContainerRefusal(bool legacy, string outcome)
        {
            var original = new Folders { new("Existing") };
            var fresh = new Folders { new(outcome == "renamed" ? "Other" : "Existing") };
            if (outcome == "added") fresh.Add(new("New"));
            int calls = 0, reads = 0;
            original.Creating = () => calls++;
            object Resolve()
            {
                reads++;
                if (calls == 0 && outcome == "unreadable-before" || calls == 1 && outcome == "unreadable-after")
                    throw new InvalidOperationException("Names unavailable.");
                return calls == 0 ? original : fresh;
            }

            var reply = MutationEvidenceTests.RunStep(legacy ? "18" : "20", "ManageSivarcRuleContainer", meta => {
                if (legacy) OptionalNativeOperations.CreateLegacySivarcContainer(meta, Resolve, original, "New", null);
                else OptionalNativeOperations.CreateSivarcContainer(meta, Resolve, () => original.Create("New"));
                return "Created";
            });
            Assert.Equal(1, calls);
            Assert.Equal(outcome == "unreadable-before" ? 1 : 2, reads);
            Assert.Equal("Native SiVArc licence refused.", reply.Meta["nativeMessage"]);
            Assert.Equal(outcome == "unchanged", MutationEvidence.ProvesNoMutation(reply.Meta));
            Assert.Equal(outcome != "unchanged", reply.OutcomeUnknown);
            Assert.Equal(outcome != "unchanged", reply.MayHaveChanged);
            return reply;
        }

        [Theory, InlineData(true), InlineData(false)]
        public void Unconnected_HMI_refuses_generation_before_preview_or_apply(bool preview)
        {
            var station = new Device { Name = "PLC station" };
            var cpu = new DeviceItem { Name = "CPU_1", Parent = station };
            var plc = SivarcGeneration.Plc(new SoftwareContainer { Parent = cpu }, "PLC program");
            Assert.Equal(("PLC station", "CPU_1"), plc);
            var hmi = Panel("Panel", new HmiTarget());
            int generateCalls = 0;
            var reply = MutationEvidenceTests.RunStep("18", "GenerateSivarc", meta => {
                SivarcGeneration.CheckConnections([hmi], [plc.Partner], new HmiObject(meta));
                if (!preview) generateCalls++;
                return "Generation admitted";
            });
            Assert.Equal("PreconditionFailed", reply.Meta["status"]);
            Assert.Equal("missing-plc-connection", reply.Meta["precondition"]);
            Assert.Contains("CPU_1", reply.Meta["nativeMessage"]!.ToString());
            Assert.False(reply.MayHaveChanged);
            Assert.Equal(0, generateCalls);
        }

        [Fact]
        public void Classic_connection_names_cannot_verify_partners_and_do_not_hide_another_unconnected_HMI()
        {
            var classic = new HmiTarget();
            classic.Connections.Add("Connection_1");
            var connected = Panel("Panel_1", classic);
            var meta = new HmiObject();
            SivarcGeneration.CheckConnections([connected], ["CPU_1"], meta);
            Assert.Equal(false, meta["connectionPartnersVerified"]);
            Assert.Equal(new[] { "Panel_1" }, Assert.IsType<HmiArray>(meta["unverifiedHmiDeviceNames"]).Cast<string>());
            var refusal = Assert.Throws<AdapterPreconditionException>(() =>
                SivarcGeneration.CheckConnections([connected, Panel("Panel_2", new HmiTarget())], ["CPU_1"], meta));
            Assert.Contains("Panel_2", refusal.Message);
        }

        [Fact]
        public void Unreadable_partner_inventory_is_reported_and_a_lost_session_propagates()
        {
            var device = Panel("Panel", new HmiTarget());
            var faults = device.DeviceItems.Single().ServiceFailures;
            var meta = new HmiObject();
            faults[typeof(SoftwareContainer)] = new NotSupportedException("Partner inventory unavailable.");
            SivarcGeneration.CheckConnections([device], ["CPU_1"], meta);
            Assert.Equal(false, meta["connectionPartnersVerified"]);
            Assert.Equal("Panel", Assert.Single(Assert.IsType<HmiArray>(meta["unverifiedHmiDeviceNames"])));
            faults[typeof(SoftwareContainer)] = new ObjectDisposedException("session");
            Assert.Throws<ObjectDisposedException>(() => SivarcGeneration.CheckConnections([device], ["CPU_1"], new HmiObject()));
        }

        [Fact]
        public void Panel_with_unknown_software_is_admitted_and_identified_as_unverified()
        {
            var meta = new HmiObject();
            SivarcGeneration.CheckConnections([Panel("Unknown", new object()), ConnectedPanel("Unified", "CPU_1")], ["CPU_1"], meta);
            Assert.Equal(false, meta["connectionPartnersVerified"]);
            Assert.Equal("Unknown", Assert.Single(Assert.IsType<HmiArray>(meta["unverifiedHmiDeviceNames"])));
        }

        [Theory, InlineData(false), InlineData(true)]
        public void Each_panel_needs_only_one_of_the_selected_CPU_partners(bool includeSecondPanel)
        {
            var first = ConnectedPanel("HMI_1", "cpu_1");
            Device[] panels = includeSecondPanel ? [first, ConnectedPanel("HMI_2", "CPU_2")] : [first];
            var meta = new HmiObject();
            SivarcGeneration.CheckConnections(panels, ["CPU_1", "CPU_2"], meta);
            Assert.Equal(true, meta["connectionPartnersVerified"]);
            Assert.False(meta.ContainsKey("unverifiedHmiDeviceNames"));
        }

        [Fact]
        public void A_match_on_one_panel_does_not_cover_another_panel()
        {
            var panels = new[] { ConnectedPanel("HMI_1", "CPU_1"), ConnectedPanel("HMI_2", "CPU_3") };
            var refusal = Assert.Throws<AdapterPreconditionException>(() =>
                SivarcGeneration.CheckConnections(panels, ["CPU_1", "CPU_2"], new HmiObject()));
            Assert.Equal("missing-plc-connection", refusal.Condition);
            Assert.Contains("HMI_2", refusal.Message);
        }

        [Fact]
        public void A_device_with_only_PLC_software_has_no_HMI()
        {
            var refusal = Assert.Throws<AdapterPreconditionException>(() =>
                SivarcGeneration.CheckConnections([Panel("Station", new Siemens.Engineering.SW.PlcSoftware())], ["CPU_1"], new HmiObject()));
            Assert.Equal("hmi-software-missing", refusal.Condition);
            Assert.Equal("hmiDeviceName", refusal.ParamName);
        }

        private static Device ConnectedPanel(string name, string partner)
        {
            var software = new HmiSoftware();
            software.Connections.Add(new(partner));
            return Panel(name, software);
        }

        private static Device Panel(string name, object software)
        {
            var device = new Device { Name = name };
            var item = new DeviceItem { Name = "Runtime", Parent = device };
            device.DeviceItems.Add(item);
            item.Services[typeof(SoftwareContainer)] = new SoftwareContainer { Parent = item, Software = software };
            return device;
        }
    }
}

namespace Siemens.Engineering.HW.Features
{
    public sealed class SoftwareContainer : IEngineeringService
    {
        public DeviceItem? Parent { get; set; }
        public object? Software { get; set; }
    }
}

namespace Siemens.Engineering.Hmi
{
    public sealed partial class HmiTarget
    {
        public List<string> Connections { get; } = new();
    }
}

namespace Siemens.Engineering.HmiUnified
{
    public sealed class HmiSoftware
    {
        public List<HmiConnections.HmiConnection> Connections { get; } = new();
    }
}

namespace Siemens.Engineering.HmiUnified.HmiConnections
{
    public sealed record HmiConnection(string Partner);
}
