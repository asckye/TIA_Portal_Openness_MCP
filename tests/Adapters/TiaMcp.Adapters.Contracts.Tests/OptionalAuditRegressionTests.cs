using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Hardware;
using TiaMcp.Adapters.Native.Optional;
using Xunit;

namespace TiaMcp.Adapters.Contracts.Tests;

public sealed class OptionalAuditRegressionTests
{
    public sealed class Telegram
    {
        public readonly List<string> Calls = new();
        public bool Allowed = true;
        private int number = 1;
        public bool CanChangeTelegram(int value) { Calls.Add("CanChangeTelegram(" + value + ")"); return Allowed; }
        public int TelegramNumber { get => number; set { Calls.Add("TelegramNumber=" + value); number = value; } }
    }

    [Fact]
    public void Legacy_telegram_number_change_checks_then_assigns_the_official_property()
    {
        var telegram = new Telegram();
        OptionalNativeOperations.ChangeTelegramNumber(telegram, 20, true, () => telegram.Calls.Add("write-started"));
        Assert.Equal(new[] { "CanChangeTelegram(20)", "write-started", "TelegramNumber=20" }, telegram.Calls);
        Assert.Equal(20, telegram.TelegramNumber);
    }

    [Fact]
    public void Negative_can_answer_and_preview_never_start_a_telegram_write()
    {
        var telegram = new Telegram { Allowed = false };
        var error = Assert.Throws<PlcSoftwareException>(() => OptionalNativeOperations.ChangeTelegramNumber(telegram, 20, true, () => Assert.Fail("Write started")));
        Assert.Equal("InvalidState", error.Code);
        Assert.Equal(new[] { "CanChangeTelegram(20)" }, telegram.Calls);
        telegram.Allowed = true;
        OptionalNativeOperations.ChangeTelegramNumber(telegram, 20, false, () => Assert.Fail("Preview wrote"));
        Assert.Equal(1, telegram.TelegramNumber);
    }

    public sealed class ParameterComposition
    {
        public string Find(ushort number, int index) => number + ":" + index;
    }

    [Fact]
    public void V14_parameter_numbers_bind_uint16_and_never_wrap()
    {
        var number = OptionalNativeOperations.ParameterNumber("14sp1", 65535);
        Assert.IsType<ushort>(number);
        Assert.Equal("65535:-1", OptionalNativeOperations.Call(new ParameterComposition(), "Find", number, -1));
        Assert.IsType<int>(OptionalNativeOperations.ParameterNumber("15.1", 65535));
        Assert.Throws<ArgumentException>(() => OptionalNativeOperations.ParameterNumber("14sp1", -1));
        Assert.Throws<ArgumentException>(() => OptionalNativeOperations.ParameterNumber("14sp1", 65536));
    }

    private sealed record Node(string Name, bool Device, Node? Parent);

    [Fact]
    public void Sivarc_generation_resolves_owning_device_when_software_name_differs()
    {
        var device = new Node("PLC station", true, null);
        var cpu = new Node("CPU", false, device);
        var software = new Node("PLC program", false, cpu);
        string name = OptionalNativeOperations.OwningDeviceName(software, value => ((Node)value).Parent,
            value => ((Node)value).Device ? ((Node)value).Name : null);
        Assert.Equal("PLC station", name);
        Assert.Throws<PlcSoftwareException>(() => OptionalNativeOperations.OwningDeviceName(new Node("orphan", false, null),
            value => ((Node)value).Parent, _ => null));
    }

    public sealed class Entry
    {
        public string Name { get; set; } = "p305";
        public int Number { get; set; } = 305;
        public object? Value { get; set; } = (float)1;
    }

    [Fact]
    public void Configuration_entries_use_the_current_native_type_before_applying_any_value()
    {
        var entry = new Entry();
        int starts = 0;
        OptionalNativeOperations.ApplyConfigurationEntries(new object[] { entry }, new Dictionary<string, object?> { ["305"] = 20L },
            ConvertScalar, () => starts++);
        Assert.IsType<float>(entry.Value);
        Assert.Equal(20f, entry.Value);
        Assert.Equal(1, starts);
        Assert.Throws<PlcSoftwareException>(() => OptionalNativeOperations.ApplyConfigurationEntries(new object[] { entry },
            new Dictionary<string, object?> { ["p305"] = 30L, ["missing"] = 40L }, ConvertScalar, () => starts++));
        Assert.Equal(20f, entry.Value);
        Assert.Equal(1, starts);
    }

    private static object? ConvertScalar(object? value, Type type) => HardwareAddressingPolicy.ConvertValue(
        new HardwareScalar { Kind = "number", Text = Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)! }, type);

    [Theory]
    [InlineData("14sp1", "ManageStartdriveParameter", "write")]
    [InlineData("14sp1", "ManageDriveTelegrams", "erase")]
    [InlineData("16", "ManageDriveFunctions", "setSimoGearMlfb")]
    [InlineData("17", "ManageDccChart", "showEditor")]
    public void Partial_writes_refuse_before_native_dispatch_even_for_preview(string release, string operation, string action)
    {
        foreach (bool preview in new[] { true, false })
        {
            var request = new OptionalEngineeringRequest { Operation = operation, Action = action, DryRun = preview, TelegramType = "SupplementaryTelegram" };
            Assert.NotNull(OptionalEngineeringCapabilities.Unsupported(release, request));
        }
    }

    [Theory]
    [InlineData("15.1")]
    [InlineData("16")]
    [InlineData("17")]
    [InlineData("18")]
    [InlineData("19")]
    public void Torque_telegram_uses_the_documented_legacy_surface(string release)
    {
        var request = new OptionalEngineeringRequest { Operation = "ManageDriveTelegrams", Action = "insert", TelegramType = "TorqueTelegram" };
        Assert.Null(OptionalEngineeringCapabilities.Unsupported(release, request));
        request.TelegramType = "EdgeTelegram";
        Assert.NotNull(OptionalEngineeringCapabilities.Unsupported(release, request));
    }

    [Theory]
    [InlineData("17", "UserCreatedRules", true)]
    [InlineData("17", "None|AllRules", true)]
    [InlineData("18", "EnergySuiteRules", false)]
    [InlineData("19", "AllTags, AdvancedTags", true)]
    [InlineData("20", "AdvancedTags", false)]
    public void Sivarc_generation_flags_are_checked_before_dispatch(string release, string options, bool refused)
        => Assert.Equal(refused, OptionalEngineeringCapabilities.Unsupported(release,
            new OptionalEngineeringRequest { Operation = "GenerateSivarc", GenerationOptions = options }) != null);

    [Theory]
    [InlineData("18", "DriveCliqMotor", true)]
    [InlineData("19", "DriveCliqMotor", false)]
    [InlineData("15.1", "InductionMotor", false)]
    public void Motor_type_is_guarded_by_the_sdk_release(string release, string motorType, bool refused)
    {
        var request = new OptionalEngineeringRequest { Operation = "ManageDriveFunctions", Action = "setMotorType", ValueJson = "value" };
        request.Inputs["value"] = new OptionalEngineeringValue { Properties = new() { ["motorType"] = new() { Kind = "string", Text = motorType } } };
        Assert.Equal(refused, OptionalEngineeringCapabilities.Unsupported(release, request) != null);
    }
}
