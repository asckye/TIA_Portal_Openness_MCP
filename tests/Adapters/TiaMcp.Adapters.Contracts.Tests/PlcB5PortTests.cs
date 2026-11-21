using System.Text.Json;
using TiaMcp.Adapters.Contracts;
using TiaMcp.PlcWorker;
using Xunit;
namespace TiaMcp.Adapters.Contracts.Tests;
public sealed class PlcB5PortTests
{
    [Theory]
    [InlineData("17", false)]
    [InlineData("18", true)]
    public void Missing_list_guidance_names_the_creation_tool_only_where_registered(string release, bool registered)
    {
        var hint = PlcAlarmExportPolicy.MissingTextListGuidance(release);
        Assert.Equal(registered, hint.Contains("ManagePlcAlarmTextList createFromMasterCopy", StringComparison.Ordinal));
        Assert.Contains("TIA", hint, StringComparison.Ordinal);
    }

    [Fact]
    public void Alarm_export_requires_the_selected_user_lists_and_recognises_the_native_empty_alarm_refusal()
    {
        Assert.Throws<AdapterPreconditionException>(() => PlcAlarmExportPolicy.RequireUserLists(Array.Empty<string>(), Array.Empty<string>()));
        Assert.Throws<AdapterPreconditionException>(() => PlcAlarmExportPolicy.RequireUserLists(new[] { "Line A" }, new[] { "Line B" }));
        PlcAlarmExportPolicy.RequireUserLists(new[] { "Line A" }, new[] { "Line A" });
        var recorded = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "AlarmExport", "rc-full-v20-raw-B-331.txt")).TrimEnd();
        Assert.True(PlcAlarmExportPolicy.NoAlarms(recorded));
        string diagnostic = recorded[(recorded.LastIndexOf(';') + 1)..^1].TrimStart();
        Assert.True(PlcAlarmExportPolicy.NoAlarms(diagnostic));
        for (int i = 0; i < diagnostic.Length; i++)
            Assert.False(PlcAlarmExportPolicy.NoAlarms(recorded.Replace(diagnostic, diagnostic.Remove(i, 1))));
        Assert.False(PlcAlarmExportPolicy.NoAlarms(string.Empty));
        Assert.False(PlcAlarmExportPolicy.NoAlarms("Access denied"));
        Assert.False(PlcAlarmExportPolicy.NoAlarms("No alarms to export"));
    }

    [Theory]
    [InlineData("zh-CN", "0012")]
    [InlineData("ZH-cn", "0012")]
    [InlineData("de-DE", "First")]
    [InlineData(null, "First")]
    public void Opc_ua_comments_are_strings_in_the_editing_language_or_first_culture(string? culture, string expected)
    {
        var texts = new[] { new { Culture = "en-US", Text = "First" }, new { Culture = "zh-CN", Text = "0012" } };
        string Read(bool empty) => TiaMcp.Adapters.Native.Plc.PlcB5Scalar.CommentText(empty ? texts.Take(0) : texts, culture,
            item => item.Culture, item => item.Text);
        Assert.Equal(expected, Read(false));
        Assert.Equal("", Read(true));
    }

    [Fact]
    public void Generated_interface_retains_its_model_name_when_the_file_is_renamed()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(root.FullName, "AGENTS.md"))) root = root.Parent!;
        string file = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".xml");
        try
        {
            string template = File.ReadAllText(Path.Combine(root.FullName, "third_party/SiemensOpcUaModelled/Phases/Phase1/InterfaceTemplate.xml"));
            File.WriteAllText(file, template.Replace("{insert_interfaceName}", "Packaging").Replace("{insert_namespaceUri}", "urn:fixture"));
            Assert.Equal("Packaging", PlcOpcUaImportDocument.InterfaceName(new FileInfo(file)));
        }
        finally { File.Delete(file); }
    }

    [Theory]
    [InlineData("14sp1", 1, 0, 0)]
    [InlineData("15.1", 1, 0, 6)]
    [InlineData("16", 1, 3, 6)]
    [InlineData("17", 1, 10, 6)]
    [InlineData("18", 1, 11, 6)]
    [InlineData("19", 1, 11, 6)]
    [InlineData("20", 7, 11, 6)]
    [InlineData("21", 7, 11, 8)]
    public void EveryToolHasAnExplicitRegistrationDecision(string release, int documents, int alarms, int opcUa)
    {
        foreach (var (family, expected) in new[] { ("F10", documents), ("F14", alarms), ("F15", opcUa) })
        {
            var tools = PortedFamilies.All.Single(f => f.Name == family).Tools;
            Assert.Equal(expected, tools.Count(t => PortedFamilies.Available(release, t)));
            Assert.All(tools, t => Assert.True(PortedFamilies.ToolReleases.ContainsKey(t)));
        }
    }
    [Theory]
    [InlineData("16", false)]
    [InlineData("17", true)]
    [InlineData("21", true)]
    public void AlarmXlsxExportsStartAtV17(string release, bool available)
    {
        Assert.Equal(available, PortedFamilies.Available(release, "ExportAlarmTextLists"));
        Assert.Equal(available, PortedFamilies.Available(release, "ExportAlarmInstanceTexts"));
        Assert.True(PortedFamilies.Available(release, "ImportPlcAlarmInstanceTexts"));
    }
    [Theory]
    [InlineData("16", "ExchangePlcAlarmTextLists", "export", "", "", "", true)]
    [InlineData("16", "ExchangePlcAlarmTextLists", "import", "", "", "", false)]
    [InlineData("17", "ExchangePlcAlarmTextLists", "export", "", "", "", false)]
    [InlineData("17", "ExchangePlcAlarmTextLists", "import", "", "", "", false)]
    [InlineData("21", "ExchangePlcAlarmTextLists", "export", "", "", "", false)]
    [InlineData("21", "ExchangePlcAlarmTextLists", "import", "", "", "", false)]
    [InlineData("15.1", "ManageOpcUaInterface", "read", "SimaticInterface", "", "", true)]
    [InlineData("16", "ManageOpcUaInterface", "read", "SimaticInterface", "", "", false)]
    [InlineData("15.1", "GenerateOpcUaModelledInterface", "", "", "Unit", "unit", true)]
    [InlineData("16", "GenerateOpcUaModelledInterface", "", "", "Unit", "unit", false)]
    [InlineData("17", "ExchangePlcAlarmTextLists", "export", "", "Safety", "safety", true)]
    [InlineData("18", "ExchangePlcAlarmTextLists", "export", "", "Safety", "safety", false)]
    [InlineData("20", "GetOpcUaAccessControl", "", "", "", "", true)]
    [InlineData("21", "GetOpcUaAccessControl", "", "", "", "", false)]
    public void PartialActionsRequireTheirNativeApi(string release, string tool, string action, string interfaceType, string unit, string kind, bool refused)
        => Assert.Equal(refused, PlcB5Capabilities.Unsupported(release, new PlcB5Request { Operation = tool, Action = action, InterfaceType = interfaceType, UnitName = unit, UnitKind = kind }) != null);
    [Theory]
    [InlineData("18", true)] [InlineData("19", true)] [InlineData("20", false)] [InlineData("21", false)]
    public void AlarmMasterCopyModesRequireV20(string release, bool refused)
        => Assert.Equal(refused, PlcB5Capabilities.Unsupported(release, new PlcB5Request { Operation = "ManagePlcAlarmTextList", Action = "createFromMasterCopy", CopyMode = "Replace" }) != null);
    [Theory]
    [InlineData("20", "createFromMasterCopy", true)] [InlineData("21", "createFromMasterCopy", false)]
    [InlineData("20", "createFromTypeVersion", true)] [InlineData("21", "createFromTypeVersion", false)]
    [InlineData("20", "read", false)]
    public void DocumentCreationRequiresV21(string release, string action, bool refused)
        => Assert.Equal(refused, PlcB5Capabilities.Unsupported(release, new PlcB5Request { Operation = "ManagePlcDocuments", Action = action, ObjectKind = "document" }) != null);
    [Theory]
    [InlineData("F10", "plc-documents")]
    [InlineData("F14", "plc-alarms")]
    [InlineData("F15", "plc-opcua")]
    public void DispatchAndPortalAdmissionUseTheGenericModule(string family, string prefix)
    {
        Assert.Equal(new[] { "ExecutePlcB5" }, WorkerOperations.FamilyNames(family));
        Assert.True(WorkerOperations.RequiresPortal(prefix + ".ExecutePlcB5"));
        Assert.True(WorkerOperations.IsFamilyOperation(prefix + ".ExecutePlcB5"));
        Assert.False(WorkerOperations.IsFamilyOperation(prefix + ".RunAnything"));
    }
    [Fact]
    public void DocumentEvidenceSurvivesTheWorkerJsonBoundary()
    {
        var original = new PlcB5Reply { Blocks = new[] { new PlcB5Block { Name = "Main", Attributes = new() { new() { Name = "Number", Value = 1, AccessMode = "Read" } } } },
            Events = new() { new() { Kind = "native", State = "Failed", ChangesProject = true, Value = new[] { "message" }, Name = "Main" } },
            Attempted = 1, Stopped = true, MayHaveChanged = true, OutcomeUnknown = true };
        var decoded = JsonSerializer.Deserialize<PlcB5Reply>(JsonSerializer.Serialize(original))!;
        Assert.Equal("Main", Assert.Single(decoded.Blocks!).Name);
        Assert.Equal("Failed", Assert.Single(decoded.Events).State);
        Assert.Equal("message", ((JsonElement)decoded.Events[0].Value!)[0].GetString());
        Assert.True(decoded.OutcomeUnknown);
    }
}
