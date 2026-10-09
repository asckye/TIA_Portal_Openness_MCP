using System.Text.Json;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Hardware;
using TiaMcp.PlcWorker;
using Xunit;

namespace TiaMcp.Adapters.Contracts.Tests;

public sealed class HardwareAddressingTests
{
    [Theory]
    [InlineData("14sp1")]
    [InlineData("15.1")]
    [InlineData("16")]
    [InlineData("17")]
    [InlineData("18")]
    [InlineData("19")]
    [InlineData("20")]
    [InlineData("21")]
    public void All_five_tools_are_explicitly_available(string release)
    {
        foreach (var tool in PortedFamilies.ForTool("GetDeviceAddressing").Tools)
            HardwareAddressingPolicy.RequireAvailable(release, tool);
        Assert.Equal(6, WorkerOperations.FamilyNames("F19").Length);
        Assert.True(WorkerOperations.IsReadOnly("hardware-addressing.ReadHardwareAddressing"));
        Assert.False(WorkerOperations.IsReadOnly("hardware-addressing.UpdateHardwareAddress"));
        Assert.False(WorkerOperations.IsReadOnly("hardware-addressing.ReadAnything"));
    }

    [Fact]
    public void Partial_actions_fail_closed_per_release()
    {
        var family = new PortedFamilies.Family("test", "test", new[] { "20", "21" }, new[] { "Tool" },
            new Dictionary<string, string[]> { ["read"] = new[] { "20", "21" }, ["write"] = new[] { "21" } });
        Assert.True(family.SupportsAction("20", "read"));
        Assert.False(family.SupportsAction("20", "write"));
        Assert.True(family.SupportsAction("21", "write"));
        Assert.False(family.SupportsAction("21", "Write"));
        Assert.False(family.SupportsAction("19", "read"));
        Assert.Throws<NotSupportedException>(() => HardwareAddressingPolicy.RequireAvailable("22", "SetDeviceAddress"));
    }

    [Fact]
    public void Shared_family_identity_keeps_legacy_checks_and_avoids_extra_native_reads()
    {
        int nativeReads = 0, capturedChecks = 0;
        Action<string> native = _ => nativeReads++;
        Action<string> captured = _ => capturedChecks++;
        WorkerOperations.MutationIdentity("hardware-addressing.UpdateHardwareAddress", native, captured)("project");
        WorkerOperations.MutationIdentity("hardware-addressing.SetHardwareIoAddress", native, captured)("project");
        Assert.Equal(0, nativeReads);
        Assert.Equal(2, capturedChecks);
        WorkerOperations.MutationIdentity("hardware-addressing.UpdateHardwareAddress", native, null)("project");
        WorkerOperations.MutationIdentity("ImportBlocks", native, captured)("project");
        WorkerOperations.MutationIdentity("hardware-addressing.Unknown", native, captured)("project");
        Assert.Equal(3, nativeReads);
        Assert.Equal(2, capturedChecks);
    }

    [Theory]
    [InlineData(-1, 100)]
    [InlineData(0, 0)]
    [InlineData(0, 501)]
    public void Invalid_page_refuses_before_lookup(int offset, int limit)
        => Assert.Throws<ArgumentException>(() => HardwareAddressingPolicy.ValidatePage(offset, limit));

    [Theory]
    [InlineData("number", "2", 2)]
    [InlineData("number", "0", 0)]
    [InlineData("number", "-2", -2)]
    public void Native_integer_scalars_keep_raw_values(string kind, string text, int expected)
        => Assert.Equal(expected, HardwareAddressingPolicy.ConvertValue(new HardwareScalar { Kind = kind, Text = text }, typeof(int)));

    [Theory]
    [InlineData("number", "2.0")]
    [InlineData("number", "2e0")]
    [InlineData("string", "2")]
    [InlineData("number", "2147483648")]
    public void Integers_reject_coercion_and_overflow(string kind, string text)
        => Assert.ThrowsAny<Exception>(() => HardwareAddressingPolicy.ConvertValue(new HardwareScalar { Kind = kind, Text = text }, typeof(int)));

    [Fact]
    public void All_properties_are_prepared_before_any_write()
    {
        var target = new Writable();
        Assert.Throws<NotSupportedException>(() => HardwareAddressingPolicy.Prepare(target.GetType(), new() {
            ["StartAddress"] = new HardwareScalar { Kind = "number", Text = "2" },
            ["Missing"] = new HardwareScalar { Kind = "number", Text = "0" } }));
        Assert.Equal(0, target.StartAddress);
    }

    [Fact]
    public void Io_reply_has_stable_worker_json_and_uncertainty_flags()
    {
        var reply = new IoAddressWriteReply { Before = new IoAddressInfo { IoType = "Input", StartAddress = 2, Length = 1 },
            Message = "readback failed", RequiresSessionReset = true };
        const string golden = "{\"Ok\":false,\"Message\":\"readback failed\",\"Before\":{\"IoType\":\"Input\",\"StartAddress\":2,\"Length\":1},\"After\":null,\"RequiresSessionReset\":true,\"MayHaveChanged\":true,\"BlockReadsAfterUncertain\":true}";
        Assert.Equal(golden, WorkerJson.Serialize(reply));
        var restored = JsonSerializer.Deserialize<IoAddressWriteReply>(golden)!;
        Assert.True(restored.MayHaveChanged);
        Assert.True(restored.RequiresSessionReset);
        Assert.True(restored.BlockReadsAfterUncertain);
    }

    [Fact]
    public void Ip_reply_is_typed_and_contains_no_native_handles()
    {
        var reply = new HardwareIpReply { Found = true, Device = "PLC_1", IpAddress = "192.0.2.1",
            Nodes = new[] { new HardwareIpNode { NodeName = "IE", Address = "192.0.2.1", IsIndustrialEthernet = true } } };
        var restored = JsonSerializer.Deserialize<HardwareIpReply>(WorkerJson.Serialize(reply))!;
        Assert.Equal("192.0.2.1", Assert.Single(restored.Nodes).Address);
        Assert.True(restored.Nodes[0].IsIndustrialEthernet);
    }

    [Fact]
    public void Address_row_keeps_released_names_and_order()
    {
        var row = new HardwareAddressRow { StartAddress = 2, Length = 1, IoType = "Input",
            Controllers = new[] { new[] { "PLC_1", "CPU_1" } } };
        Assert.Equal("{\"startAddress\":2,\"length\":1,\"ioType\":\"Input\",\"controllers\":[[\"PLC_1\",\"CPU_1\"]],\"attributes\":{}}",
            WorkerJson.Serialize(row.ToEvidence()));
    }

    [Fact]
    public void Step_reply_keeps_uncertainty_after_a_worker_round_trip()
    {
        var reply = new HardwareAddressingReply { Message = "uncertain", RequiresSessionReset = true,
            Meta = new() { ["mayHaveChanged"] = true, ["writeOutcomeUnknown"] = true } };
        const string golden = "{\"Message\":\"uncertain\",\"Meta\":{\"mayHaveChanged\":true,\"writeOutcomeUnknown\":true},\"RequiresSessionReset\":true,\"MayHaveChanged\":true,\"BlockReadsAfterUncertain\":true}";
        Assert.Equal(golden, WorkerJson.Serialize(reply));
        IWorkerOperationReply restored = JsonSerializer.Deserialize<HardwareAddressingReply>(golden)!;
        Assert.True(restored.MayHaveChanged);
        Assert.True(restored.RequiresSessionReset);
        Assert.True(restored.BlockReadsAfterUncertain);
    }

    [Fact]
    public void Attribute_evidence_keeps_scalar_values_and_refuses_reference_handles()
    {
        Assert.Equal("Input", HardwareAddressingPolicy.ScalarValue(SampleIo.Input));
        Assert.Equal("00:00:01", HardwareAddressingPolicy.ScalarValue(TimeSpan.FromSeconds(1)));
        Assert.Equal(2, HardwareAddressingPolicy.ScalarValue(2));
        Assert.Throws<NotSupportedException>(() => HardwareAddressingPolicy.ScalarValue(new Writable()));
    }

    private enum SampleIo { Input }
    private sealed class Writable { public int StartAddress { get; set; } }
}
