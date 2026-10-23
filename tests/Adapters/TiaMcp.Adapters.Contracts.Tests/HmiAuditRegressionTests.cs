using System.Collections;
using TiaMcp.Adapters.Native.Hmi;
using Xunit;

namespace TiaMcp.Adapters.Contracts.Tests;

public sealed class HmiAuditRegressionTests
{
    [Theory]
    [InlineData("Hmi.Screen.Screen")]
    [InlineData("Hmi.Tag.TagTable")]
    [InlineData("Hmi.Communication.Connection")]
    public void Import_without_overwrite_refuses_the_XML_identity_before_the_native_call(string kind)
    {
        string file = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".xml");
        try
        {
            File.WriteAllText(file, "<Document><" + kind + "><AttributeList><Name>Existing</Name></AttributeList></" + kind + "></Document>");
            var targets = new[] { new Named { Name = "Existing" } };
            int nativeCalls = 0;
            void Import(bool overwrite)
            {
                HmiExchangePolicy.BeforeImport(targets, file, kind, overwrite);
                nativeCalls++;
            }
            var refusal = Assert.Throws<PlcSoftwareException>(() => Import(false));
            Assert.Equal("InvalidState", refusal.Code);
            Assert.Contains("overwrite=true", refusal.Message);
            Assert.Equal(0, nativeCalls);
            Import(true);
            Assert.Equal(1, nativeCalls);
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void Import_does_not_guess_an_identity_from_a_filename()
    {
        string file = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".xml");
        try
        {
            File.WriteAllText(file, "<Document><Hmi.Screen.Screen><AttributeList/></Hmi.Screen.Screen></Document>");
            var refusal = Assert.Throws<PlcSoftwareException>(() => HmiExchangePolicy.BeforeImport(Array.Empty<object>(), file, "Hmi.Screen.Screen", false));
            Assert.Equal("InvalidParams", refusal.Code);
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void Opaque_native_packages_require_explicit_overwrite_when_the_target_is_not_empty()
    {
        var evidence = new Dictionary<string, object?>();
        Assert.Throws<PlcSoftwareException>(() => HmiExchangePolicy.BeforeOpaqueImport(new[] { "Existing" }, false, evidence));
        Assert.Equal("PRECONDITION_FAILED", evidence["v4Rejection"]);
        HmiExchangePolicy.BeforeOpaqueImport(new[] { "Existing" }, true, evidence);
        HmiExchangePolicy.BeforeOpaqueImport(Array.Empty<string>(), false, evidence);
    }

    [Theory]
    [InlineData("Folders")]
    [InlineData("ScreenGroups")]
    [InlineData("TagTableGroups")]
    public void An_unresolved_parent_never_falls_back_to_root(string collection)
    {
        var root = new Folder();
        Assert.Same(root, HmiExchangePolicy.Folder(root, "", collection));
        var refusal = Assert.Throws<PlcSoftwareException>(() => HmiExchangePolicy.Folder(root, "/Missing", collection));
        Assert.Equal("NotFound", refusal.Code);
    }

    [Fact]
    public void Omitted_script_options_preserve_globals_and_async_while_explicit_values_can_clear_them()
    {
        var script = new Script { ScriptCode = "old", GlobalDefinitionAreaScriptCode = "const keep = 1;", Async = true };
        int writes = 0;
        HmiExchangePolicy.UpdateScript(script, "new", null, null, () => writes++);
        Assert.Equal("new", script.ScriptCode);
        Assert.Equal("const keep = 1;", script.GlobalDefinitionAreaScriptCode);
        Assert.True(script.Async);
        Assert.Equal(1, writes);
        HmiExchangePolicy.UpdateScript(script, "newer", "", false, () => writes++);
        Assert.Equal("", script.GlobalDefinitionAreaScriptCode);
        Assert.False(script.Async);
        Assert.Equal(4, writes);
    }

    [Fact]
    public void Text_without_a_culture_refuses_instead_of_silently_dropping_the_caption()
    {
        var refusal = Assert.Throws<NotSupportedException>(() => HmiExchangePolicy.RequireLegacyText("Start"));
        Assert.Contains("SetUnifiedMultilingualProperty", refusal.Message);
        HmiExchangePolicy.RequireLegacyText("");
    }

    [Fact]
    public void Tag_creation_passes_the_requested_table_and_never_tries_another_overload()
    {
        var tags = new Tags();
        var software = new Software { Tags = tags };
        var tag = Assert.IsType<Tag>(HmiExchangePolicy.EnsureTag(software, new Named { Name = "Requested" }, "Start", () => { }));
        Assert.Equal("Start", tag.Name);
        Assert.Equal("Requested", tag.TagTableName);
        Assert.Equal(new[] { "Start:Requested" }, tags.Calls);
        var refusal = Assert.Throws<PlcSoftwareException>(() => HmiExchangePolicy.EnsureTag(software, new Named { Name = "Other" }, "Start", () => { }));
        Assert.Contains("Requested", refusal.Message);
        Assert.Single(tags.Calls);
    }

    [Fact]
    public void Legacy_and_exact_tag_table_lookup_refuse_ambiguous_names_and_accept_exact_paths()
    {
        var a = new Named { Name = "Shared" };
        var b = new Named { Name = "Shared" };
        var root = new Folder { TagTables = [a], TagTableGroups = [new Folder { Name = "Nested", TagTables = [b] }] };
        Assert.Throws<PlcSoftwareException>(() => HmiExchangePolicy.TagTable(root, "Shared"));
        Assert.Same(a, HmiExchangePolicy.TagTable(root, "/Shared"));
        Assert.Same(b, HmiExchangePolicy.TagTable(root, "/Nested/Shared"));
        Assert.Throws<PlcSoftwareException>(() => HmiExchangePolicy.TagTable(root, "/Missing/Shared"));
    }

    [Fact]
    public void Undocumented_property_names_refuse_before_any_scalar_write()
    {
        var script = new Script { ScriptCode = "keep" };
        int writes = 0;
        Assert.Throws<NotSupportedException>(() => HmiExchangePolicy.WriteScalars(script,
            new Dictionary<string, object?> { ["ScriptCode"] = "changed", ["ControllerTag"] = "guessed" }, () => writes++));
        Assert.Equal("keep", script.ScriptCode);
        Assert.Equal(0, writes);
    }

    [Fact]
    public void Connection_verification_never_probes_or_writes_driver_properties_by_substring()
    {
        var connection = new Connection();
        HmiExchangePolicy.RequireConnection(connection, "PLC_1");
        Assert.Throws<NotSupportedException>(() => HmiExchangePolicy.RequireConnection(connection, "OtherPLC"));
        Assert.Equal(0, connection.Writes);
    }

    [Theory]
    [InlineData("None")]
    [InlineData("WithDefaults")]
    [InlineData("WithReadOnly")]
    public void Export_choices_are_exact_and_the_request_default_includes_defaults(string option)
    {
        Assert.Equal(option, HmiExchangePolicy.ExportOption(option));
        Assert.Equal("WithDefaults", new HmiRequest().ExportOptions);
        Assert.False(new HmiRequest().Overwrite);
        Assert.Null(new UnifiedHmiRequest().Async);
        Assert.Null(new UnifiedHmiRequest().GlobalDefinitionAreaScriptCode);
        Assert.Throws<ArgumentException>(() => HmiExchangePolicy.ExportOption("WithDefault"));
    }

    [Theory]
    [InlineData("14sp1")]
    [InlineData("15.1")]
    [InlineData("16")]
    [InlineData("17")]
    [InlineData("18")]
    public void Classic_only_releases_do_not_register_screen_item_description(string release)
    {
        Assert.False(PortedFamilies.Available(release, "DescribeHmiScreenItem"));
        Assert.NotNull(HmiCapabilities.Unsupported(release, "DescribeHmiScreenItem"));
        Assert.True(PortedFamilies.Available(release, "ManageClassicHmiGraphic"));
        Assert.True(PortedFamilies.Available(release, "GetClassicHmiGlobalization"));
    }

    [Theory]
    [InlineData("19")]
    [InlineData("20")]
    [InlineData("21")]
    public void Unified_releases_register_screen_item_description_and_project_graphics(string release)
    {
        Assert.True(PortedFamilies.Available(release, "DescribeHmiScreenItem"));
        Assert.Null(HmiCapabilities.Unsupported(release, "DescribeHmiScreenItem"));
        Assert.True(PortedFamilies.Available(release, "ManageClassicHmiGraphic"));
        Assert.True(PortedFamilies.Available(release, "GetClassicHmiGlobalization"));
    }

    [Theory]
    [InlineData("BuildClassicHmiTagTable")]
    [InlineData("BuildClassicHmiMinimalPackage")]
    public void V21_header_builders_are_withheld_from_V20(string tool)
    {
        Assert.False(PortedFamilies.Available("20", tool));
        Assert.True(PortedFamilies.Available("21", tool));
    }

    public class Named { public string Name { get; set; } = ""; }
    public sealed class Folder : Named
    {
        public object[] Folders { get; set; } = [];
        public object[] ScreenGroups { get; set; } = [];
        public object[] TagTableGroups { get; set; } = [];
        public object[] Groups { get; set; } = [];
        public object[] TagTables { get; set; } = [];
    }
    public sealed class Script
    {
        public string ScriptCode { get; set; } = "";
        public string GlobalDefinitionAreaScriptCode { get; set; } = "";
        public bool Async { get; set; }
    }
    public sealed class Software { public Tags Tags { get; set; } = new(); }
    public sealed class Connection
    {
        public int Writes { get; private set; }
        public string Partner => "PLC_1";
        public string Station => "Station_1";
        public string Node => "PN/IE_1";
        public string CommunicationDriver { get => "Configured driver"; set { Writes++; throw new InvalidOperationException(value); } }
        public string InitialAddress { get => "Rack=0;ExpansionSlot=2;PlcAddress=192.0.2.1"; set { Writes++; throw new InvalidOperationException(value); } }
        public IEnumerable DriverProperties => throw new InvalidOperationException("DriverProperties must not be enumerated.");
    }
    public sealed class Tag : Named { public string TagTableName { get; set; } = ""; }
    public sealed class Tags : IEnumerable
    {
        private readonly List<Tag> tags = [];
        public List<string> Calls { get; } = [];
        public Tag Create(string name) => throw new InvalidOperationException("One-string overload must not be called.");
        public Tag Create(string name, string table)
        {
            Calls.Add(name + ":" + table);
            var tag = new Tag { Name = name, TagTableName = table };
            tags.Add(tag);
            return tag;
        }
        public IEnumerator GetEnumerator() => tags.GetEnumerator();
    }
}
