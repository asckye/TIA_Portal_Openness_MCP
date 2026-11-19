using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using TiaMcp.Adapters.Native.Security;
using TiaMcp.Adapters.Reflection;
using Xunit;

namespace TiaMcp.Adapters.Contracts.Tests;

public sealed class NativeRefusalProbeTests
{
    [Theory, InlineData("before-read"), InlineData("after-read"), InlineData("timeout"), InlineData("cancelled"), InlineData("disposed")]
    public void Unavailable_proof_never_certifies_a_refusal(string failure)
    {
        int reads = 0, writes = 0;
        var meta = new Dictionary<string, object?>();
        var error = Record.Exception(() => NativeRefusalProbe.Run(meta, () => {
            reads++;
            if (failure == "before-read" || failure == "after-read" && reads == 2) throw new IOException("Cannot snapshot.");
            return [7];
        }, () => {
            writes++;
            throw failure switch {
                "timeout" => new TimeoutException(), "cancelled" => new OperationCanceledException(), "disposed" => new ObjectDisposedException("session"),
                _ => new InvalidOperationException("Native refusal.")
            };
        }));
        Assert.NotNull(error);
        Assert.Equal(1, writes);
        Assert.Equal(failure == "after-read" ? 2 : 1, reads);
        Assert.True((bool)meta["writeOutcomeUnknown"]!);
        Assert.False(MutationEvidence.ProvesNoMutation(meta));
    }

    [Theory, InlineData("unchanged"), InlineData("source"), InlineData("partner"), InlineData("missing"), InlineData("returned")]
    public void Port_connect_compares_both_fresh_endpoints(string outcome) => _ = PortConnect(outcome);

    public static IReadOnlyDictionary<string, object?> PortConnect(string outcome)
    {
        var fixture = HardwareWriteFailureTests.NativeFixture();
        var device = fixture.Project.Devices.Single();
        var partner = new DeviceItem { Name = "P2", Parent = device };
        device.DeviceItems.Add(partner);
        NetworkPort Install(DeviceItem owner)
        {
            var proxy = new NetworkPort { OwnedBy = owner };
            owner.Services[typeof(NetworkPort)] = proxy;
            return proxy;
        }
        var sourcePort = Install(fixture.Cpu);
        var partnerPort = Install(partner);
        var other = new DeviceItem { Name = "P3", Parent = device };
        device.DeviceItems.Add(other);
        sourcePort.ConnectedPorts.Add(new NetworkPort { OwnedBy = other });
        int calls = 0;
        sourcePort.Connecting = p => {
            calls++;
            Assert.Same(partnerPort, p);
            if (outcome == "returned") return;
            var fresh = Install(fixture.Cpu);
            fresh.ConnectedPorts.Add(new NetworkPort { OwnedBy = outcome == "source" ? partner : other });
            var freshPartner = Install(partner);
            if (outcome == "partner") freshPartner.ConnectedPorts.Add(new NetworkPort { OwnedBy = other });
            if (outcome == "missing") device.DeviceItems.Remove(partner);
            throw new InvalidOperationException("Native port refused.");
        };
        var reply = fixture.Engine.HardwareManagePortInterconnection(["PLC"], ["CPU"], "connect", ["PLC"], ["P2"], dryRun: false);
        Assert.Equal(1, calls);
        Assert.Equal(outcome != "unchanged", reply.OutcomeUnknown);
        Assert.Equal(outcome != "unchanged", reply.MayHaveChanged);
        Assert.Equal(outcome == "unchanged", MutationEvidence.ProvesNoMutation(reply.Meta));
        return reply.Meta;
    }

    [Theory, InlineData("create", "unchanged"), InlineData("create", "added"), InlineData("create", "missing"),
        InlineData("setPermissions", "unchanged"), InlineData("setPermissions", "added"), InlineData("setPermissions", "missing")]
    public void Device_user_refusals_resolve_the_hardware_path_and_compare_membership(string action, string outcome) => _ = DeviceUser(action, outcome);

    public static IReadOnlyDictionary<string, object?> DeviceUser(string action, string outcome)
    {
        var fixture = HardwareWriteFailureTests.NativeFixture("16");
        var device = fixture.Project.Devices.Single();
        var users = new WebserverUserComposition();
        var existing = new WebserverUser { UserName = "Operator", Bits = WebserverUserPermissions.ReadTag };
        if (action == "setPermissions") users.Add(existing);
        fixture.Cpu.Services[typeof(WebserverUserManagement)] = new WebserverUserManagement { ReadUsers = () => users };
        int calls = 0;
        void Refuse()
        {
            calls++;
            device.DeviceItems.Remove(fixture.Cpu);
            if (outcome != "missing")
            {
                var replacement = new DeviceItem { Parent = device, Name = "CPU" };
                device.DeviceItems.Add(replacement);
                var refreshed = new WebserverUserComposition();
                if (action == "setPermissions") refreshed.Add(new WebserverUser { UserName = existing.UserName, Bits = existing.Bits });
                if (outcome == "added") refreshed.Add(new WebserverUser { UserName = "Another" });
                replacement.Services[typeof(WebserverUserManagement)] = new WebserverUserManagement { ReadUsers = () => refreshed };
            }
            throw new InvalidOperationException("Web server is disabled.");
        }
        users.Creating = Refuse;
        existing.Set = _ => Refuse();
        var request = new DeviceUserRequest { Permissions = ["ReadFiles"] };
        var adapter = new DeviceUsersAdapter(new SecuritySession(fixture.Engine, request.Faults), request);
        var reply = adapter.ManageDeviceUsers(["PLC"], ["CPU"], "webserver", action, "Operator", dryRun: false);
        Assert.Equal(1, calls);
        Assert.Equal("Web server is disabled.", reply.Meta["nativeMessage"]);
        Assert.Equal(outcome != "unchanged", reply.OutcomeUnknown);
        Assert.Equal(outcome != "unchanged", reply.MayHaveChanged);
        return reply.Meta;
    }

    public static HardwareAddressingReply SynchronizeSelection(bool preview, string selection)
    {
        var folder = new Siemens.Engineering.Library.Types.LibraryTypeFolder();
        var nested = new Siemens.Engineering.Library.Types.LibraryTypeUserFolder();
        folder.Folders.Add(nested);
        var type = new Siemens.Engineering.Library.Types.LibraryType();
        if (selection == "nested") nested.Types.Add(type);
        return MutationEvidenceTests.RunStep("15.1", "SynchronizeLibrary", meta => {
            PlcFoundationEngine.RequireSelectedTypes(selection == "direct" ? [type] : [folder]);
            meta["mayHaveChanged"] = !preview;
            return "Selection accepted";
        });
    }

    [Theory, InlineData(true), InlineData(false)]
    public void Library_checks_resolved_types_before_preview_or_native_update(bool preview)
    {
        var empty = SynchronizeSelection(preview, "empty");
        Assert.False(empty.MayHaveChanged);
        Assert.Equal("PreconditionFailed", empty.Meta["status"]);
        Assert.Equal("empty-type-selection", empty.Meta["precondition"]);
        Assert.Equal("selection", empty.Meta["parameter"]);
        Assert.Equal(true, SynchronizeSelection(preview, "direct").Meta["operationSuccess"]);
        Assert.Equal(true, SynchronizeSelection(preview, "nested").Meta["operationSuccess"]);
    }

    private sealed class AttributeTarget : Siemens.Engineering.IEngineeringObject
    {
        internal Action? Set;
        internal Func<string, object> Read = name => name;
        internal string Mode = "ReadWrite";
        public object GetAttribute(string name) => Read(name);
        public void SetAttribute(string name, object? value) => Set!();
        public object[] GetAttributeInfos() => [
            new { Name = "HeaderAuthor", AccessMode = Mode }, new { Name = "Revision", AccessMode = "Read" },
            new { Name = "Secret", AccessMode = "Write" }
        ];
    }

    private static HardwareScalar[] AuthorArgs() => [
        new HardwareScalar { Kind = "string", Text = "HeaderAuthor" }, new HardwareScalar { Kind = "string", Text = "New author" }
    ];

    [Theory, InlineData("unchanged"), InlineData("sibling"), InlineData("readError"), InlineData("lookup"), InlineData("connection")]
    public void SetAttribute_checks_all_readable_attributes_on_a_new_proxy(string outcome) => _ = SetAttribute(outcome);

    public static IReadOnlyDictionary<string, object?> SetAttribute(string outcome)
    {
        var initial = new AttributeTarget();
        var fresh = new AttributeTarget();
        var reads = new List<string>();
        var revision = new byte[] { 7, 1 };
        initial.Read = name => { reads.Add("before/" + name); return name == "Revision" ? revision : name; };
        fresh.Read = name => {
            reads.Add("after/" + name);
            if (name == "Revision" && outcome == "readError") throw new IOException("Attribute inaccessible.");
            return name == "Revision" ? (byte[])revision.Clone() : name;
        };
        bool called = false;
        initial.Set = () => {
            called = true;
            if (outcome == "sibling") revision[1]++;
            if (outcome == "connection") throw new ObjectDisposedException("session");
            throw new InvalidOperationException("TIA refused author.");
        };
        var session = new ReflectionSession((_, _, _, _) => !called ? initial : outcome == "lookup" ? null : fresh, () => null);
        var reply = new OpennessReflection(session).InvokeObject("Block", "FC", "SetAttribute", AuthorArgs(), allowWrite: true);
        Assert.True(called);
        Assert.Equal(outcome == "unchanged", MutationEvidence.ProvesNoMutation(reply.Meta));
        Assert.Equal(outcome != "unchanged", reply.Meta["mayHaveChanged"]);
        Assert.DoesNotContain(reads, r => r.EndsWith("/Secret"));
        Assert.Contains("before/Revision", reads);
        Assert.Equal(outcome is not ("connection" or "lookup"), reads.Contains("after/Revision"));
        return reply.Meta;
    }

    [Fact]
    public void Session_root_without_a_fresh_lookup_keeps_the_native_failure_unknown()
    {
        var cached = new AttributeTarget { Set = () => throw new InvalidOperationException("Native refusal.") };
        var session = new ReflectionSession((_, _, _, _) => cached, () => null, (_, _, _, _) => null);
        var reply = new OpennessReflection(session).InvokeObject("Portal", "", "SetAttribute", AuthorArgs(), allowWrite: true);
        Assert.Equal(true, reply.Meta["invocationAttempted"]);
        Assert.Equal(true, reply.Meta["writeOutcomeUnknown"]);
        Assert.False(MutationEvidence.ProvesNoMutation(reply.Meta));
    }

    [Theory, InlineData(true), InlineData(false)]
    public void Read_only_attribute_is_refused_before_invocation(bool preview)
    {
        var target = new AttributeTarget { Mode = "Read", Read = _ => throw new IOException("Read-only getter refused.") };
        bool called = false;
        target.Set = () => called = true;
        var adapter = new OpennessReflection(new ReflectionSession((_, _, _, _) => target, () => null));
        var reply = preview ? adapter.PreviewSetAttribute("Block", "FC", "PLC", "HeaderAuthor")
            : adapter.InvokeObject("Block", "FC", "SetAttribute", AuthorArgs(), allowWrite: true);
        Assert.False(called);
        Assert.Equal("PreconditionFailed", reply.Meta["status"]);
        Assert.Equal("args", reply.Meta["parameter"]);
        Assert.Equal(false, reply.Meta["invocationAttempted"]);
    }
}
