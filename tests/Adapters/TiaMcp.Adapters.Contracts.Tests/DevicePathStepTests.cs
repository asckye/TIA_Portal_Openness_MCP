using System.Text.Json;
using TiaMcp.Adapters;
using TiaMcp.Adapters.Contracts;
using Xunit;

namespace TiaMcp.Adapters.Contracts.Tests;

public sealed class DevicePathStepTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Device_failures_keep_their_evidence_on_both_step_routes(bool callbacks)
    {
        var engine = new PlcFoundationEngine("21");
        engine.ConfigureFixture();
        if (callbacks) engine.ConfigureWorkerFamilyCallbacks(text => JsonSerializer.Deserialize<object>(text)!,
            path => JsonSerializer.Deserialize<string[]>(path)!);
        engine.ProjectConnectionCreated!(new Siemens.Engineering.TiaPortal());
        var project = engine.NewProject("Fixture"); engine.ProjectRetrieved!(project);
        project.Devices.Add(new() { Name = "Dup" });
        var group = new Siemens.Engineering.HW.DeviceUserGroup { Name = "G" };
        group.Devices.Add(new() { Name = "Dup" }); project.DeviceGroups.Add(group);
        foreach (string target in new[] { "Dup", "Missing" })
        {
            var reply = engine.ReadHardwareAddressing(new[] { target }, Array.Empty<string>());
            Assert.Equal(target == "Dup" ? "Ambiguous" : "NotFound", reply.Meta["status"]);
            Assert.Equal(target, reply.Meta["target"]);
            if (target == "Missing") Assert.Equal(target, reply.Meta["missingDeviceTarget"]);
            else Assert.False(reply.Meta.ContainsKey("missingDeviceTarget"));
            Assert.Equal(new[] { "Dup", "G/Dup" }, Assert.IsType<string[]>(reply.Meta["candidates"]));
            Assert.Contains(target, Assert.IsType<string>(reply.Meta["nativeMessage"]));
            Assert.False(reply.MayHaveChanged);
            Assert.False(reply.RequiresSessionReset);
        }
    }

    [Fact]
    public void Lookup_evidence_never_claims_refusal_after_a_write()
    {
        var error = new PlcSoftwareException("Ambiguous", "Duplicate devices.", new[] { "A", "G/A" });
        error.Data["target"] = "A"; error.Data["candidates"] = new[] { "A", "G/A" };
        foreach (var meta in new[] {
            new Dictionary<string, object?> { ["mayHaveChanged"] = true },
            new Dictionary<string, object?> { ["mayHaveChanged"] = false, ["mayHaveWrittenFiles"] = true } })
        {
            StepFailureStatus.RecordTarget(error, meta);
            Assert.False(meta.ContainsKey("target"));
            Assert.False(meta.ContainsKey("candidates"));
            Assert.False(meta.ContainsKey("missingDeviceTarget"));
        }
    }
}
