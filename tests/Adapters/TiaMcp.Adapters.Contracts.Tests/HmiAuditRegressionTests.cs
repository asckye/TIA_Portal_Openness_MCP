using System.Collections;
using TiaMcp.Adapters.Native.Hmi;
using Xunit;

namespace TiaMcp.Adapters.Contracts.Tests;

public sealed class HmiAuditRegressionTests
{
    [Theory]
    [InlineData("Group/Sub/Table", "/Group/Sub/Table")]
    [InlineData("Group\\Sub\\Table", "/Group/Sub/Table")]
    [InlineData("/Group\\Sub/Table", "/Group/Sub/Table")]
    [InlineData("\\Group\\Sub\\Table", "/Group/Sub/Table")]
    public void Relative_and_backslash_table_paths_are_refused_with_the_corrected_absolute_form(string path, string corrected)
    {
        var root = new TableFolder(); int writes = 0;
        var error = Assert.Throws<ArgumentException>(() => HmiExchangePolicy.EnsureTagTable(root, path, () => writes++));
        Assert.Contains("Corrected slash form: " + corrected, error.Message);
        Assert.Equal(0, writes);
        Assert.Empty(root.TagTables.Calls);
    }

    [Theory]
    [InlineData("Group", "/Group")]
    [InlineData("Group/Sub", "/Group/Sub")]
    [InlineData("Group\\Sub", "/Group/Sub")]
    [InlineData("/Group\\Sub", "/Group/Sub")]
    public void Folder_paths_use_the_same_absolute_convention_without_silent_normalization(string path, string corrected)
    {
        var root = new TableFolder();
        var error = Assert.Throws<ArgumentException>(() => HmiExchangePolicy.Folder(root, path, "TagTableGroups"));
        Assert.Contains("Corrected slash form: " + corrected, error.Message);
        Assert.Empty(root.TagTables.Calls);
        Assert.Same(root, HmiExchangePolicy.Folder(root, "/", "TagTableGroups"));
    }

    [Theory]
    [InlineData("//")]
    [InlineData("/A//B")]
    [InlineData("/A/")]
    public void Invalid_folder_paths_cannot_normalize_to_a_different_import_destination(string path)
    {
        var root = new TableFolder();
        Assert.Throws<ArgumentException>(() => HmiExchangePolicy.Folder(root, path, "TagTableGroups"));
        Assert.Empty(root.TagTables.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Table_creation_and_lookup_select_the_nested_existing_folder_and_unescape_each_segment(bool classic)
    {
        var leaf = new TableFolder { Name = "Diagnostic Data" };
        var parent = new TableFolder { Name = "Hmi Tags", Groups = [leaf], Folders = [leaf] };
        var root = new TableFolder { TagTableGroups = [parent], Folders = [parent] };
        object software = classic ? new ClassicSoftware { TagFolder = root } : root;
        int writes = 0;
        const string path = "/Hmi%20Tags/Diagnostic%20Data/Diag%20Info";
        var table = HmiExchangePolicy.EnsureTagTable(software, path, () => writes++);
        Assert.Equal("Diag Info", ((Named)table).Name);
        Assert.Same(table, HmiExchangePolicy.TagTable(software, path));
        Assert.Same(table, HmiExchangePolicy.EnsureTagTable(software, path, () => writes++));
        Assert.Equal(1, writes);
        Assert.Equal(new[] { "Diag Info" }, leaf.TagTables.Calls);
        Assert.Empty(root.TagTables.Calls);
        Assert.Empty(parent.TagTables.Calls);
    }

    [Theory]
    [InlineData(false, "%2F", "/")]
    [InlineData(true, "%2F", "/")]
    [InlineData(false, "%5C", "\\")]
    [InlineData(true, "%5C", "\\")]
    public void Escaped_separators_remain_inside_each_exact_folder_and_table_name(bool classic, string escaped, string decoded)
    {
        var parent = new TableFolder { Name = "Group" + decoded + "Sub" };
        var root = new TableFolder { TagTableGroups = [parent], Folders = [parent] };
        object software = classic ? new ClassicSoftware { TagFolder = root } : root;
        string path = "/Group" + escaped + "Sub/Table" + escaped + "Name";
        int writes = 0;
        var table = HmiExchangePolicy.EnsureTagTable(software, path, () => writes++);
        Assert.Equal("Table" + decoded + "Name", ((Named)table).Name);
        Assert.Same(table, HmiExchangePolicy.TagTable(software, path));
        Assert.Same(parent, HmiExchangePolicy.Folder(root, "/Group" + escaped + "Sub", classic ? "Folders" : "TagTableGroups"));
        Assert.Equal(1, writes);
        Assert.Equal(new[] { "Table" + decoded + "Name" }, parent.TagTables.Calls);
        Assert.Empty(root.TagTables.Calls);
    }

    public static IEnumerable<object[]> InvalidTablePaths => new[] { "Group/Table", "/Group/Table/", "/Group//Table", "/", "/Group/A%2FB/",
        "/" + string.Join("/", Enumerable.Repeat("Group", 65)) + "/Table" }.Select(path => new object[] { path });

    [Theory, MemberData(nameof(InvalidTablePaths))]
    public void Invalid_slash_names_never_create_a_table(string path)
    {
        var root = new TableFolder(); int writes = 0;
        var error = Assert.Throws<ArgumentException>(() => HmiExchangePolicy.EnsureTagTable(root, path, () => writes++));
        Assert.Contains("/Group/Sub/Table", error.Message);
        Assert.Equal(0, writes);
        Assert.Empty(root.TagTables.Calls);
    }

    [Fact]
    public void Missing_folder_never_creates_and_plain_names_keep_unique_lookup_and_root_create()
    {
        var root = new TableFolder(); int writes = 0;
        Assert.Equal("NotFound", Assert.Throws<PlcSoftwareException>(() => HmiExchangePolicy.EnsureTagTable(root,
            "/Missing/Table", () => writes++)).Code);
        Assert.Equal(0, writes); Assert.Empty(root.TagTables.Calls);
        var table = HmiExchangePolicy.EnsureTagTable(root, "Plain", () => writes++);
        Assert.Same(table, HmiExchangePolicy.EnsureTagTable(root, "Plain", () => writes++));
        Assert.Equal(1, writes); Assert.Equal(new[] { "Plain" }, root.TagTables.Calls);
        root.TagTableGroups = [new TableFolder { TagTables = new Tables { new Named { Name = "Plain" } } }];
        Assert.Throws<PlcSoftwareException>(() => HmiExchangePolicy.EnsureTagTable(root, "Plain", () => writes++));
        Assert.Equal(1, writes);
    }

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
    public sealed class Tables : List<Named>
    {
        public List<string> Calls { get; } = [];
        public Named Create(string name) { Calls.Add(name); var value = new Named { Name = name }; Add(value); return value; }
    }
    public sealed class TableFolder : Named
    {
        public object[] Folders { get; set; } = [];
        public object[] TagTableGroups { get; set; } = [];
        public object[] Groups { get; set; } = [];
        public Tables TagTables { get; set; } = new();
    }
    public sealed class ClassicSoftware { public TableFolder TagFolder { get; set; } = new(); }
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
