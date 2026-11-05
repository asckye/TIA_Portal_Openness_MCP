using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Hardware;
using Xunit;

namespace TiaMcp.Adapters.Contracts.Tests;

public sealed class HardwareWriteFailureTests
{
    [Fact]
    public void Archive_failure_without_hmi_recorder_retains_partial_file_evidence()
    {
        var fixture = NativeFixture();
        fixture.Project.ArchiveFailure = new IOException("Native archive failed after writing.");
        var previous = TiaMcp.Adapters.Native.Hmi.HmiValues.ExportFailure;
        TiaMcp.Adapters.Native.Hmi.HmiValues.ExportFailure = null;
        string directory = Path.Combine(AppContext.BaseDirectory, "archive-route-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var result = fixture.Engine.ArchiveSavedProject(Path.Combine(directory, "project.zap21"), false);
            Assert.Equal(1, fixture.Project.ArchiveCalls);
            Assert.Equal(false, result.Meta["operationSuccess"]);
            Assert.Equal(true, result.Meta["mayHaveWrittenFiles"]);
            Assert.Equal(true, result.Meta["partialArtifactExists"]);
            Assert.Contains("Native archive failed after writing.", result.Meta["error"]!.ToString());
            Assert.Single(Directory.GetFiles(directory, ".tia-export-*.zap21"));
        }
        finally
        {
            TiaMcp.Adapters.Native.Hmi.HmiValues.ExportFailure = previous;
            foreach (string file in Directory.GetFiles(directory)) File.Delete(file);
            Directory.Delete(directory);
        }
    }

    public HardwareWriteFailureTests() => ConfigureCodecs();

    private static void ConfigureCodecs()
    {
        TiaMcp.Adapters.Native.Hmi.HmiValues.Renderer = values => System.Text.Json.JsonSerializer.Serialize(values);
        TiaMcp.Adapters.Native.Hmi.HmiValues.ExportFailure = _ => { };
        TiaMcp.Adapters.Native.Hmi.HmiValues.TraceWriter = (_, _) => { };
    }

    private static Dictionary<string, HardwareScalar> AttributeChange() => new() {
        ["Channel"] = new HardwareScalar { Kind = "number", Text = "4" } };

    [Fact]
    public void Applied_scalar_property_is_not_cleared_by_unchanged_attribute_failure()
    {
        var target = new Target();
        var meta = new Dictionary<string, object?> { ["mayHaveChanged"] = false };
        Assert.Throws<InvalidOperationException>(() => HardwareAddressPrimitives.ApplyScalarsAndAttributes(target,
            new() { ["Name"] = new HardwareScalar { Kind = "string", Text = "Changed" } }, AttributeChange(), meta, true));
        Assert.Equal("Changed", target.Name);
        Assert.Equal(new[] { "Name" }, Assert.IsType<List<string>>(meta["appliedProperties"]));
        Assert.Equal(true, meta["mayHaveChanged"]);
        Assert.Empty(Assert.IsType<List<string>>(meta["appliedAttributes"]));
        Assert.Equal(2, target.AttributeReads);
    }

    [Theory, InlineData("EnableSystemLogging"), InlineData("TransportProtocol")]
    public void Syslog_property_write_is_not_cleared_by_unchanged_attribute_failure(string property)
    {
        var target = new Target();
        var meta = new Dictionary<string, object?> { ["mayHaveChanged"] = true };
        object requested = property == "EnableSystemLogging" ? true : Siemens.Engineering.HW.SysLogTransportProtocolType.UDP;
        typeof(Target).GetProperty(property)!.SetValue(target, requested);
        Assert.Throws<InvalidOperationException>(() => HardwareAddressPrimitives.SetDynamicAttributes(target, new() {
            ["SysLogAutoAcceptClient"] = new HardwareScalar { Kind = "boolean", Text = "true" } }, meta));
        Assert.Equal(requested, typeof(Target).GetProperty(property)!.GetValue(target));
        Assert.Equal(true, meta["mayHaveChanged"]);
        Assert.Equal(1, target.AttributeWrites);
        Assert.Equal(2, target.AttributeReads);
    }

    internal static (PlcFoundationEngine Engine, Siemens.Engineering.TiaPortal Portal, Siemens.Engineering.Project Project,
        Siemens.Engineering.HW.DeviceItem Cpu, Siemens.Engineering.HW.Features.SysLogConfigurationManager Syslog) NativeFixture(string release = "21",
            bool bindProject = true)
    {
        ConfigureCodecs();
        var engine = new PlcFoundationEngine(release);
        engine.ConfigureFixture();
        engine.ConfigureWorkerFamilyCallbacks(text => System.Text.Json.JsonSerializer.Deserialize<object>(text)!,
            path => System.Text.Json.JsonSerializer.Deserialize<string[]>(path)!);
        var portal = new Siemens.Engineering.TiaPortal();
        engine.ProjectConnectionCreated!(portal);
        var project = engine.NewProject("NativeFixture");
        if (bindProject) engine.ProjectRetrieved!(project);
        var device = new Siemens.Engineering.HW.Device { Name = "PLC" };
        var cpu = new Siemens.Engineering.HW.DeviceItem { Name = "CPU", Parent = device };
        device.DeviceItems.Add(cpu);
        project.Devices.Add(device);
        var syslog = new Siemens.Engineering.HW.Features.SysLogConfigurationManager { OwnedBy = cpu };
        cpu.Services[typeof(Siemens.Engineering.HW.Features.SysLogConfigurationManager)] = syslog;
        return (engine, portal, project, cpu, syslog);
    }

    public static string AttachedWithoutProjectReply(string family)
    {
        var fixture = NativeFixture(bindProject: false);
        HardwareAddressingReply? hardware = null;
        SecurityResult? security = null;
        if (family == "security") security = SyslogUpdate(fixture.Engine, new());
        else if (family == "hardware") hardware = fixture.Engine.HardwareManageIoSystem(new[] { "PLC" }, new[] { "CPU" }, "create", dryRun: false);
        else hardware = fixture.Engine.RunHmiStep("GetHmiProgramInfo", _ => throw new InvalidOperationException("Unbound HMI body must not run."), true);
        Assert.Equal(0, fixture.Portal.ExclusiveCalls);
        var meta = security?.Meta ?? hardware!.Meta;
        Assert.Equal("InvalidState", meta["status"]);
        Assert.False(meta.ContainsKey("mayHaveChanged"));
        return System.Text.Json.JsonSerializer.Serialize(new { message = security?.Message ?? hardware!.Message, meta });
    }

    public static string FailedArchiveReply()
    {
        var fixture = NativeFixture();
        fixture.Project.ArchiveFailure = new System.IO.IOException("Synthetic archive failure after writing a partial artifact.");
        string directory = System.IO.Path.Combine(AppContext.BaseDirectory, "archive-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(directory);
        try
        {
            var result = fixture.Engine.ArchiveSavedProject(System.IO.Path.Combine(directory, "project.zap21"), dryRun: false);
            Assert.Equal(1, fixture.Project.ArchiveCalls);
            Assert.Equal(false, result.Meta["operationSuccess"]);
            Assert.Equal(true, result.Meta["partialArtifactExists"]);
            Assert.Equal(true, result.Meta["mayHaveWrittenFiles"]);
            Assert.False(result.MayHaveChanged);
            return System.Text.Json.JsonSerializer.Serialize(new { message = result.Message, meta = result.Meta });
        }
        finally
        {
            foreach (string file in System.IO.Directory.GetFiles(directory)) System.IO.File.Delete(file);
            System.IO.Directory.Delete(directory);
        }
    }

    [Theory]
    [InlineData("hmi")]
    [InlineData("security")]
    [InlineData("hardware")]
    public void Attached_without_project_does_not_seed_mutation_evidence(string family)
        => AttachedWithoutProjectReply(family);

    [Fact]
    public void Failed_archive_records_possible_file_effects() => FailedArchiveReply();

    private static SecurityResult SyslogUpdate(PlcFoundationEngine engine, Dictionary<string, HardwareScalar> properties,
        Dictionary<string, HardwareScalar>? attributes = null)
    {
        var request = new SecurityDeepRequest { Properties = properties, Attributes = attributes ?? new() };
        var adapter = new TiaMcp.Adapters.Native.Security.SecurityDeepAdapter(
            new TiaMcp.Adapters.Native.Security.SecuritySession(engine, new Dictionary<string, SecurityValidationFault>()), request);
        return adapter.ManageSyslogServers(scope: "plc", action: "update", devicePathJson: new[] { "PLC" }, itemPathJson: new[] { "CPU" }, dryRun: false);
    }

    [Fact]
    public void Syslog_non_boolean_value_is_refused_before_the_native_setter()
    {
        var fixture = NativeFixture();
        var result = SyslogUpdate(fixture.Engine, new() { ["EnableSystemLogging"] = new HardwareScalar { Kind = "string", Text = "not-a-boolean" } });
        Assert.Equal(false, result.Meta["operationSuccess"]);
        Assert.Equal(false, result.Meta["apiCallSuccess"]);
        Assert.False(result.MayHaveChanged);
        Assert.False(result.RequiresSessionReset);
        Assert.Empty(fixture.Syslog.Calls);
        Assert.False(fixture.Syslog.EnableSystemLogging);
        Assert.Equal(1, fixture.Portal.ExclusiveCalls);
    }

    [Theory]
    [InlineData("EnableSystemLogging", "boolean", "true")]
    [InlineData("TransportProtocol", "string", "UDP")]
    public void Syslog_native_setter_failure_is_changed_and_requires_reset(string property, string kind, string value)
    {
        var fixture = NativeFixture();
        fixture.Syslog.SetterFailure = new InvalidOperationException("Native setter failed after applying the property.");
        var result = SyslogUpdate(fixture.Engine, new() { [property] = new HardwareScalar { Kind = kind, Text = value } });
        Assert.Equal(false, result.Meta["operationSuccess"]);
        Assert.Equal(false, result.Meta["apiCallSuccess"]);
        Assert.Equal("ReadOrWriteFailed", result.Meta["status"]);
        Assert.True(result.MayHaveChanged);
        Assert.True(result.RequiresSessionReset);
        Assert.Equal(new[] { property }, fixture.Syslog.Calls);
        if (property == "EnableSystemLogging") Assert.True(fixture.Syslog.EnableSystemLogging);
        else Assert.Equal(Siemens.Engineering.HW.SysLogTransportProtocolType.UDP, fixture.Syslog.TransportProtocol);
    }

    [Fact]
    public void Syslog_keeps_an_earlier_property_write_when_the_next_setter_fails()
    {
        var fixture = NativeFixture();
        fixture.Syslog.FailingProperty = "TransportProtocol";
        fixture.Syslog.SetterFailure = new InvalidOperationException("The second native setter failed.");
        var result = SyslogUpdate(fixture.Engine, new() {
            ["EnableSystemLogging"] = new HardwareScalar { Kind = "boolean", Text = "true" },
            ["TransportProtocol"] = new HardwareScalar { Kind = "string", Text = "UDP" }
        });
        Assert.Equal(new[] { "EnableSystemLogging", "TransportProtocol" }, fixture.Syslog.Calls);
        Assert.True(fixture.Syslog.EnableSystemLogging);
        Assert.Equal(false, result.Meta["operationSuccess"]);
        Assert.Equal("ReadOrWriteFailed", result.Meta["status"]);
        Assert.True(result.MayHaveChanged);
        Assert.True(result.RequiresSessionReset);
    }

    [Fact]
    public void Syslog_unapproved_attribute_is_unchanged_without_native_write_or_reset()
    {
        var fixture = NativeFixture();
        var result = SyslogUpdate(fixture.Engine, new(), new() { ["SysLogAutoAcceptClient"] = new HardwareScalar { Kind = "boolean", Text = "true" } });
        Assert.Equal(false, result.Meta["operationSuccess"]);
        Assert.False(result.MayHaveChanged);
        Assert.False(result.RequiresSessionReset);
        Assert.Empty(fixture.Cpu.Calls);
        Assert.Empty(fixture.Syslog.Calls);
        Assert.Equal(0, fixture.Portal.ExclusiveCalls);
        Assert.Empty(SecurityAttributePolicy.SyslogNames);
    }

    [Theory]
    [InlineData("io-system")]
    [InlineData("channel")]
    [InlineData("device-group")]
    [InlineData("port")]
    [InlineData("syslog")]
    public void Real_step_paths_preserve_typed_edit_refusals(string operation)
    {
        var fixture = NativeFixture();
        fixture.Portal.ExclusiveFailure = new InvalidOperationException("Native ExclusiveAccess refused.");
        IWorkerOperationReply result = operation switch {
            "io-system" => fixture.Engine.HardwareManageIoSystem(new[] { "PLC" }, new[] { "CPU" }, "create", dryRun: false),
            "channel" => fixture.Engine.HardwareUpdateDeviceItemChannel(new[] { "PLC" }, new[] { "CPU" }, "Digital", "Input", 0, AttributeChange(),
                dryRun: false),
            "device-group" => fixture.Engine.HardwareManageDeviceUserGroup("Group", "create", dryRun: false),
            "port" => fixture.Engine.HardwareManagePortInterconnection(new[] { "PLC" }, new[] { "CPU" }, "connect", dryRun: false),
            _ => SyslogUpdate(fixture.Engine, new() { ["EnableSystemLogging"] = new HardwareScalar { Kind = "boolean", Text = "true" } })
        };
        var meta = result is SecurityResult security ? security.Meta : ((HardwareAddressingReply)result).Meta;
        Assert.Equal("PreconditionFailed", meta["status"]);
        Assert.Equal("exclusive-access", meta["precondition"]);
        Assert.Equal(false, meta["operationSuccess"]);
        Assert.Equal(false, meta["apiCallSuccess"]);
        Assert.False(result.MayHaveChanged);
        Assert.False(result.RequiresSessionReset);
        Assert.Equal(1, fixture.Portal.ExclusiveCalls);
        Assert.Empty(fixture.Syslog.Calls);
        Assert.Empty(fixture.Cpu.Calls);
    }

    [Theory]
    [InlineData("14sp1")]
    [InlineData("15.1")]
    [InlineData("16")]
    [InlineData("17")]
    [InlineData("18")]
    [InlineData("19")]
    [InlineData("20")]
    public void Real_aml_step_preserves_typed_edit_refusal(string release)
    {
        var fixture = NativeFixture(release);
        var cax = new Siemens.Engineering.Cax.CaxProvider();
        fixture.Project.Services[typeof(Siemens.Engineering.Cax.CaxProvider)] = cax;
        fixture.Portal.ExclusiveFailure = new InvalidOperationException("Native ExclusiveAccess refused.");
        string directory = System.IO.Path.Combine(AppContext.BaseDirectory, "aml-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(directory);
        string source = System.IO.Path.Combine(directory, "input.aml");
        System.IO.File.WriteAllText(source, "<CAEXFile SchemaVersion=\"2.15\" FileName=\"input.aml\"><InstanceHierarchy Name=\"Project\" /></CAEXFile>");
        try
        {
            var result = fixture.Engine.ImportHardwareAml(source, System.IO.Path.Combine(directory, "import.log"), confirmImport: true, dryRun: false);
            Assert.Equal("PreconditionFailed", result.Meta["status"]);
            Assert.Equal("exclusive-access", result.Meta["precondition"]);
            Assert.Equal(false, result.Meta["operationSuccess"]);
            Assert.Equal(false, result.Meta["apiCallSuccess"]);
            Assert.False(result.MayHaveChanged);
            Assert.False(result.RequiresSessionReset);
            Assert.Equal(1, fixture.Portal.ExclusiveCalls);
            Assert.Equal(0, cax.ImportCalls);
        }
        finally { System.IO.File.Delete(source); System.IO.Directory.Delete(directory); }
    }

    [Fact]
    public void Attribute_only_failure_can_still_use_unchanged_readback()
    {
        var target = new Target();
        var meta = new Dictionary<string, object?> { ["mayHaveChanged"] = false };
        Assert.Throws<InvalidOperationException>(() => HardwareAddressPrimitives.SetDynamicAttributes(target, AttributeChange(), meta));
        Assert.Equal(false, meta["mayHaveChanged"]);
        Assert.Equal(1, target.AttributeWrites);
    }

    public sealed class Target : Siemens.Engineering.IEngineeringObject
    {
        public string Name { get; set; } = "Before";
        public bool EnableSystemLogging { get; set; }
        public Siemens.Engineering.HW.SysLogTransportProtocolType TransportProtocol { get; set; }
        public int AttributeWrites;
        public int AttributeReads;
        public object GetAttribute(string name) { AttributeReads++; return name == "SysLogAutoAcceptClient" ? (object)false : 3; }
        public void SetAttribute(string name, object? value) { AttributeWrites++; throw new InvalidOperationException("Attribute refused."); }
    }
}
