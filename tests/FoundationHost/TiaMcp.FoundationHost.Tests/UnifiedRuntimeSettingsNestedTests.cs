extern alias toolpipeline;
using System.Text.Json;
using TiaMcp.Adapters.Native.Engineering;
using TiaMcp.Adapters.Native.Hmi;
using Xunit;

public sealed class UnifiedRuntimeSettingsNestedTests : IDisposable
{
    private readonly Func<Dictionary<string, object?>, string>? _renderer = HmiValues.Renderer;
    private readonly Action<string, string>? _traceWriter = HmiValues.TraceWriter;
    private readonly Func<string, TiaMcp.Adapters.Contracts.UnifiedValue>? _decoder = UnifiedRequestValues.Decoder;

    public UnifiedRuntimeSettingsNestedTests()
    {
        HmiValues.Renderer = value => JsonSerializer.Serialize(value);
        HmiValues.TraceWriter = (_, _) => { };
        UnifiedRequestValues.Decoder = text => {
            using var document = JsonDocument.Parse(text);
            return toolpipeline::TiaMcpServer.Siemens.Services.UnifiedInputCodec.Decode(document.RootElement);
        };
    }

    [Fact]
    public void Nested_scalars_write_and_read_back_without_replacing_their_owners()
    {
        var hmi = new Software();
        const string json = "{\"OpcUaServerRuntimeSettings.MaxSessionCount\":12," +
            "\"MaxLoginRuntimeSettings.MaxLoginErrors\":4294967295," +
            "\"HmiReportingSettings.ReportingMainStorage\":\"SD\"," +
            "\"MaxLoginRuntimeSettings.EnableLockAfterNumberOfAttempts\":true," +
            "\"HmiReportingSettings.ReportingMainStoragePath\":\"/reports\",\"GMPEnabled\":true}";
        var preview = Update(hmi, json, true);
        Assert.Equal(3u, hmi.RuntimeSettings.MaxLoginRuntimeSettings.MaxLoginErrors);
        var result = Update(hmi, json, false, (string)preview["token"]!);
        Assert.True((bool)result["operationSuccess"]!);
        Assert.Equal("Verified", result["status"]);
        Assert.Equal(12, hmi.RuntimeSettings.OpcUaServerRuntimeSettings.MaxSessionCount);
        Assert.Equal(uint.MaxValue, hmi.RuntimeSettings.MaxLoginRuntimeSettings.MaxLoginErrors);
        Assert.Equal(Storage.SD, hmi.RuntimeSettings.HmiReportingSettings.ReportingMainStorage);
        Assert.Equal("/reports", hmi.RuntimeSettings.HmiReportingSettings.ReportingMainStoragePath);
        Assert.True(hmi.RuntimeSettings.GMPEnabled);
        Assert.True(hmi.RuntimeSettings.MaxLoginRuntimeSettings.EnableLockAfterNumberOfAttempts);
        Assert.Equal(0, hmi.RuntimeSettings.OwnerSetterCalls);
        var read = new HmiObject();
        UnifiedRuntimeSettingsAccess.Read(hmi, new[] { "OpcUaServerRuntimeSettings.MaxSessionCount",
            "MaxLoginRuntimeSettings.MaxLoginErrors", "HmiReportingSettings.ReportingMainStorage" }, read, new HmiReadTrace());
        Assert.True((bool)read["dataComplete"]!);
        var rows = (HmiObject)read["settings"]!;
        Assert.Equal(12, rows["OpcUaServerRuntimeSettings.MaxSessionCount"]!.Field("value"));
        Assert.Equal(uint.MaxValue, rows["MaxLoginRuntimeSettings.MaxLoginErrors"]!.Field("value"));
        Assert.Equal("/RuntimeSettings/MaxLoginRuntimeSettings/MaxLoginErrors",
            rows["MaxLoginRuntimeSettings.MaxLoginErrors"]!.Field("path"));
        Assert.Contains(((HmiArray)read["capabilities"]!).Cast<HmiObject>(),
            row => (string?)row["field"] == "HmiReportingSettings.ReportingMainStorage" && (bool)row["writable"]!
                && ((HmiArray)row["allowedValues"]!).Contains("SD"));
    }

    [Theory]
    [InlineData("OpcUaServerRuntimeSettings.MaxSessionCount", "2147483648")]
    [InlineData("OpcUaServerRuntimeSettings.MaxSessionCount", "-2147483649")]
    [InlineData("OpcUaServerRuntimeSettings.MaxSessionCount", "5.0")]
    [InlineData("OpcUaServerRuntimeSettings.MaxSessionCount", "5e0")]
    [InlineData("OpcUaServerRuntimeSettings.MaxSessionCount", "\"5\"")]
    [InlineData("MaxLoginRuntimeSettings.MaxLoginErrors", "-1")]
    [InlineData("MaxLoginRuntimeSettings.MaxLoginErrors", "4294967296")]
    [InlineData("MaxLoginRuntimeSettings.MaxLoginErrors", "false")]
    [InlineData("HmiReportingSettings.ReportingMainStorage", "1")]
    [InlineData("HmiReportingSettings.ReportingMainStorage", "\"sd\"")]
    public void Invalid_numbers_and_enums_refuse_before_any_setter(string field, string value)
    {
        var hmi = new Software();
        Assert.Throws<ArgumentException>(() => Update(hmi, "{\"GMPEnabled\":true,\"" + field + "\":" + value + "}", true));
        Assert.False(hmi.RuntimeSettings.GMPEnabled);
        Assert.Equal(2, hmi.RuntimeSettings.OpcUaServerRuntimeSettings.MaxSessionCount);
        Assert.Equal(3u, hmi.RuntimeSettings.MaxLoginRuntimeSettings.MaxLoginErrors);
        Assert.Equal(0, hmi.RuntimeSettings.OwnerSetterCalls);
    }

    [Theory]
    [InlineData("OpcUaServerRuntimeSettings.MaxSessionCount", "-2147483648")]
    [InlineData("OpcUaServerRuntimeSettings.MaxSessionCount", "2147483647")]
    [InlineData("MaxLoginRuntimeSettings.MaxLoginErrors", "0")]
    [InlineData("MaxLoginRuntimeSettings.MaxLoginErrors", "4294967295")]
    public void Integer_boundaries_are_accepted(string field, string value)
    {
        var hmi = new Software();
        string json = "{\"" + field + "\":" + value + "}";
        var preview = Update(hmi, json, true);
        Assert.True((bool)Update(hmi, json, false, (string)preview["token"]!)["operationSuccess"]!);
    }

    [Fact]
    public void Readback_mismatch_restores_each_owner_and_coupled_values_captured_before_any_setter()
    {
        var hmi = new Software();
        var settings = hmi.RuntimeSettings;
        settings.HmiReportingSettings.CoupledChange = () => settings.MaxLoginRuntimeSettings.MaxLoginErrors = 99;
        settings.OpcUaServerRuntimeSettings.RejectNewValue = true;
        const string json = "{\"GMPEnabled\":true,\"HmiReportingSettings.ReportingMainStorage\":\"SD\"," +
            "\"MaxLoginRuntimeSettings.MaxLoginErrors\":5,\"OpcUaServerRuntimeSettings.MaxSessionCount\":12}";
        var preview = Update(hmi, json, true);
        var result = Update(hmi, json, false, (string)preview["token"]!);
        Assert.Equal("ReadbackMismatch", result["status"]);
        Assert.True((bool)result["rollbackCompleted"]!);
        Assert.False((bool)result["mayHaveChanged"]!);
        Assert.False(settings.GMPEnabled);
        Assert.Equal(Storage.Internal, settings.HmiReportingSettings.ReportingMainStorage);
        Assert.Equal(3u, settings.MaxLoginRuntimeSettings.MaxLoginErrors);
        Assert.Equal(2, settings.OpcUaServerRuntimeSettings.MaxSessionCount);
        Assert.Equal(0, settings.OwnerSetterCalls);
    }

    [Fact]
    public void Coupled_change_to_an_initially_unchanged_requested_field_is_restored()
    {
        var hmi = new Software();
        hmi.RuntimeSettings.MaxLoginRuntimeSettings.CoupledChange = () => hmi.RuntimeSettings.GMPEnabled = true;
        const string json = "{\"GMPEnabled\":false,\"MaxLoginRuntimeSettings.MaxLoginErrors\":5}";
        var preview = Update(hmi, json, true);
        var result = Update(hmi, json, false, (string)preview["token"]!);
        Assert.Equal("ReadbackMismatch", result["status"]);
        Assert.True((bool)result["rollbackCompleted"]!);
        Assert.False(hmi.RuntimeSettings.GMPEnabled);
        Assert.Equal(3u, hmi.RuntimeSettings.MaxLoginRuntimeSettings.MaxLoginErrors);
    }

    [Fact]
    public void Changed_nested_value_invalidates_preview_before_any_write()
    {
        var hmi = new Software();
        const string json = "{\"GMPEnabled\":true,\"MaxLoginRuntimeSettings.MaxLoginErrors\":5}";
        var preview = Update(hmi, json, true);
        hmi.RuntimeSettings.MaxLoginRuntimeSettings.MaxLoginErrors = 4;
        Assert.Throws<TiaMcp.Adapters.Contracts.PlcSoftwareException>(() =>
            Update(hmi, json, false, (string)preview["token"]!));
        Assert.False(hmi.RuntimeSettings.GMPEnabled);
        Assert.Equal(4u, hmi.RuntimeSettings.MaxLoginRuntimeSettings.MaxLoginErrors);
    }

    [Fact]
    public void Expanded_allow_list_does_not_expand_the_twelve_field_limit()
    {
        string[] fields = TiaMcp.Adapters.Contracts.UnifiedRuntimeSettingsFields.Nested.Take(13).ToArray();
        Assert.Throws<ArgumentException>(() => UnifiedRuntimeSettingsAccess.Fields(JsonSerializer.Serialize(fields)));
        Assert.Throws<ArgumentException>(() => UnifiedRuntimeSettingsAccess.Changes(
            JsonSerializer.Serialize(fields.ToDictionary(field => field, _ => true, StringComparer.Ordinal))));
        Assert.Equal(12, UnifiedRuntimeSettingsAccess.Fields(JsonSerializer.Serialize(fields.Take(12))).Length);
    }

    private static HmiObject Update(Software hmi, string json, bool preview, string token = "")
    {
        var meta = new HmiObject();
        EngineeringWriteJournal.Run(meta, () => UnifiedRuntimeSettingsAccess.Update(hmi, "Fixture", "HMI",
            UnifiedRuntimeSettingsAccess.Changes(json), preview, token, meta, new HmiReadTrace()));
        return meta;
    }

    public void Dispose()
    {
        HmiValues.Renderer = _renderer;
        HmiValues.TraceWriter = _traceWriter;
        UnifiedRequestValues.Decoder = _decoder;
    }

    public sealed class Software { public Settings RuntimeSettings { get; } = new(); }
    public sealed class Settings
    {
        private readonly OpcUa _opc = new();
        private readonly Login _login = new();
        private readonly Reporting _reporting = new();
        public bool GMPEnabled { get; set; }
        public int OwnerSetterCalls { get; private set; }
        public OpcUa OpcUaServerRuntimeSettings { get => _opc; set { OwnerSetterCalls++; throw new InvalidOperationException(); } }
        public Login MaxLoginRuntimeSettings { get => _login; set { OwnerSetterCalls++; throw new InvalidOperationException(); } }
        public Reporting HmiReportingSettings { get => _reporting; set { OwnerSetterCalls++; throw new InvalidOperationException(); } }
    }
    public sealed class OpcUa
    {
        private int _sessions = 2;
        public bool RejectNewValue { get; set; }
        public int MaxSessionCount { get => _sessions; set { if (!RejectNewValue) _sessions = value; } }
    }
    public sealed class Login
    {
        private uint _errors = 3;
        public Action? CoupledChange { get; set; }
        public bool EnableLockAfterNumberOfAttempts { get; set; }
        public uint MaxLoginErrors {
            get => _errors;
            set { _errors = value; if (value == 5) CoupledChange?.Invoke(); }
        }
    }
    public enum Storage { Internal, SD }
    public sealed class Reporting
    {
        private Storage _storage;
        public Action? CoupledChange { get; set; }
        public Storage ReportingMainStorage {
            get => _storage;
            set { _storage = value; if (value == Storage.SD) CoupledChange?.Invoke(); }
        }
        public string ReportingMainStoragePath { get; set; } = "";
    }
}
