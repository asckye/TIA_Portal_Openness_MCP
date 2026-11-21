using TiaMcp.Adapters.Contracts;
using Xunit;

namespace TiaMcp.Adapters.Contracts.Tests;

public sealed class HardwareFeatureReadTests
{
    public static IEnumerable<object[]> Releases => new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" }
        .Select(release => new object[] { release });

    [Theory]
    [InlineData("read", false, false)]
    [InlineData("deleteService", true, false)]
    [InlineData("deleteService", false, true)]
    public void Device_service_read_and_preview_clear_unknown_write_outcomes(string action, bool preview, bool writing)
    {
        var engine = HardwareWriteFailureTests.NativeFixture().Engine;
        engine.EngineeringStep = (_, _) => new HardwareAddressingReply { OutcomeUnknown = true };
        var result = engine.HardwareManageDeviceServiceObjects(new[] { "PLC" }, new[] { "CPU" }, "certificate", action, dryRun: preview);
        Assert.Equal(writing, result.OutcomeUnknown);
    }

    [Theory, MemberData(nameof(Releases))]
    public void Unprobed_feature_is_not_reported_absent(string release)
    {
        var fixture = HardwareWriteFailureTests.NativeFixture(release);
        var reply = fixture.Engine.HardwareReadHardwareFeatures(new[] { "PLC" }, new[] { "CPU" });
        var rows = Rows(reply);
        var absentType = rows.Single(row => Equals(row["feature"], "AddressController"));
        Assert.Null(absentType["present"]);
        Assert.Equal(false, absentType["typeAvailableInApi"]);
        Assert.Equal("type absent on this version", absentType["skipped"]);
        var notService = rows.Single(row => Equals(row["feature"], "SysLogServerConfiguration"));
        Assert.Null(notService["present"]);
        Assert.Equal(true, notService["typeAvailableInApi"]);
        Assert.Equal("not a concrete service type", notService["skipped"]);
        Assert.Equal(false, rows.Single(row => Equals(row["feature"], "NetworkInterface"))["present"]);
        Assert.Equal(true, rows.Single(row => Equals(row["feature"], "SysLogConfigurationManager"))["present"]);
        Assert.Equal(1, reply.Meta["presentCount"]);
        Assert.Equal(true, reply.Meta["dataComplete"]);
        Assert.Contains(release, Assert.IsType<string>(reply.Meta["scope"]), StringComparison.Ordinal);
        Assert.DoesNotContain("V21 XML", Assert.IsType<string>(reply.Meta["scope"]), StringComparison.Ordinal);
    }

    [Theory, MemberData(nameof(Releases))]
    public void Feature_page_is_incomplete_when_rows_are_omitted(string release)
    {
        var fixture = HardwareWriteFailureTests.NativeFixture(release);
        foreach (var (offset, limit) in new[] { (0, 1), (1, 100), (100, 100) })
        {
            var reply = fixture.Engine.HardwareReadHardwareFeatures(new[] { "PLC" }, new[] { "CPU" }, offset, limit);
            Assert.Equal(false, reply.Meta["dataComplete"]);
        }
    }

    [Theory, MemberData(nameof(Releases))]
    public void Failed_feature_probe_keeps_presence_unknown_and_marks_data_incomplete(string release)
    {
        var fixture = HardwareWriteFailureTests.NativeFixture(release);
        fixture.Cpu.ServiceFailures[typeof(Siemens.Engineering.HW.Features.NetworkInterface)] =
            new InvalidOperationException("Service probe failed.");
        var reply = fixture.Engine.HardwareReadHardwareFeatures(new[] { "PLC" }, new[] { "CPU" });
        var row = Rows(reply).Single(row => Equals(row["feature"], "NetworkInterface"));
        Assert.Null(row["present"]);
        Assert.Equal("Service probe failed.", row["probeError"]);
        Assert.Equal(false, reply.Meta["dataComplete"]);
        Assert.Equal(true, reply.Meta["apiCallSuccess"]);
    }

    [Theory, MemberData(nameof(Releases))]
    public void Failed_feature_value_read_marks_data_incomplete(string release)
    {
        var fixture = HardwareWriteFailureTests.NativeFixture(release);
        fixture.Cpu.Services[typeof(Siemens.Engineering.HW.Features.NetworkInterface)] =
            new Siemens.Engineering.HW.Features.NetworkInterface { FailScalarRead = true };
        var reply = fixture.Engine.HardwareReadHardwareFeatures(new[] { "PLC" }, new[] { "CPU" });
        var row = Rows(reply).Single(row => Equals(row["feature"], "NetworkInterface"));
        Assert.Equal(true, row["present"]);
        var values = Assert.IsType<Dictionary<string, object?>>(row["values"]);
        Assert.Equal(false, values["dataComplete"]);
        var failure = Assert.IsType<Dictionary<string, object?>>(Assert.Single(Assert.IsType<List<object?>>(values["failures"])));
        Assert.Equal("InterfaceType", failure["property"]);
        Assert.Equal(false, reply.Meta["dataComplete"]);
    }

    [Fact]
    public void Missing_service_on_selected_item_carries_target_refusal()
    {
        var fixture = HardwareWriteFailureTests.NativeFixture("21");
        var method = typeof(PlcFoundationEngine).GetMethod("HardwareRequireHardwareService",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        var invocation = Assert.Throws<System.Reflection.TargetInvocationException>(() =>
            method.MakeGenericMethod(typeof(Siemens.Engineering.HW.Features.NetworkInterface)).Invoke(null, new object[] { fixture.Cpu, "itemPathJson" }));
        var error = Assert.IsType<NotSupportedException>(invocation.InnerException);
        Assert.Equal("target", error.Data["unsupportedBy"]);
        Assert.Contains("itemPath", error.Message, StringComparison.Ordinal);
    }

    private static Dictionary<string, object?>[] Rows(HardwareAddressingReply reply)
        => Assert.IsType<List<object?>>(reply.Meta["records"]).Cast<Dictionary<string, object?>>().ToArray();
}
