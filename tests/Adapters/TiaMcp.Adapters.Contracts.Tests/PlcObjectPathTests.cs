extern alias plcpathfixture;
using PathEngine = plcpathfixture::TiaMcp.Adapters.PlcFoundationEngine;
using ExportOptions = plcpathfixture::Siemens.Engineering.ExportOptions;
using Block = plcpathfixture::Siemens.Engineering.SW.Blocks.PlcBlock;
using Group = plcpathfixture::Siemens.Engineering.SW.Blocks.PlcBlockUserGroup;
using TypeGroup = plcpathfixture::Siemens.Engineering.SW.Types.PlcTypeGroup;
using PlcType = plcpathfixture::Siemens.Engineering.SW.Types.PlcType;
using TiaMcp.Adapters.Contracts;
using Xunit;

namespace TiaMcp.Adapters.Contracts.Tests;

public sealed class PlcObjectPathTests
{
    [Fact]
    public void Verified_exports_select_the_bound_block_and_check_consistency_for_both_styles()
    {
        var engine = new PathEngine("19");
        var block = AddBlock(AddGroup(engine.Plc.BlockGroup, "Group"), "Data");
        block.Language = "DB";
        var sibling = AddBlock(AddGroup(engine.Plc.BlockGroup, "Other"), "Data");
        var request = new PlcSoftwareRequest { SoftwarePath = "PLC", Path = "Group/Data" };
        var candidate = new PlcVerifiedCandidate { Name = "Data", Xml = """
            <Document><Engineering version="V19"/><SW.Blocks.GlobalDB ID="0"><AttributeList>
            <Name>Data</Name><ProgrammingLanguage>DB</ProgrammingLanguage><Interface><Sections/></Interface>
            </AttributeList></SW.Blocks.GlobalDB></Document>
            """ };
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".xml");
        try
        {
            engine.VerifiedImport(request, candidate, execution => {
                execution.Export(path);
                execution.ExportNone!(path);
                block.IsConsistent = false;
                Assert.Equal("BlockInconsistent", Assert.Throws<PlcSoftwareException>(() => execution.ExportNone(path)).Code);
                return "";
            });
            Assert.Equal(new[] { ExportOptions.WithDefaults | ExportOptions.WithReadOnly, ExportOptions.None }, block.ExportOptionsUsed);
            Assert.Empty(sibling.ExportOptionsUsed);
        }
        finally { File.Delete(path); }
    }

    public static IEnumerable<object[]> Releases() => new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" }
        .Select(key => new object[] { key });

    public static IEnumerable<object[]> NamedReleases() => Releases().SelectMany(row =>
        new[] { "A/B", "中文块", "A%2FB", "A #?" }.Select(name => new object[] { row[0], name }));

    [Theory, MemberData(nameof(Releases))]
    public void Saved_encoded_and_readable_paths_select_the_same_block(string release)
    {
        var engine = new PathEngine(release);
        var group = AddGroup(engine.Plc.BlockGroup, "组 一");
        foreach (string name in new[] { "中文块", "A/B", "50%", "A%2FB", "A #?" })
        {
            var block = AddBlock(group, name);
            string readable = "组 一/" + ObjectPathEncoding.EncodeSegment(name);
            string saved = Uri.EscapeDataString(group.Name) + "/" + Uri.EscapeDataString(name);
            Assert.Equal(readable, engine.BlockPath(block));
            Assert.Same(block, engine.ExactBlock(readable));
            Assert.Same(block, engine.ExactBlock(saved));
            Assert.Same(block, Export(engine, saved));
        }
        Assert.Same(engine.ExactBlock("组 一/50%25"), engine.ExactBlock("组 一/50%"));
    }

    [Theory, MemberData(nameof(Releases))]
    public void Directory_exports_keep_file_name_punctuation_encoded(string release)
    {
        var engine = new PathEngine(release);
        var block = AddBlock(AddGroup(engine.Plc.BlockGroup, "G"), "中文块 :*?\"<>|/%\\");
        Assert.Same(block, Export(engine, "G/" + ObjectPathEncoding.EncodeSegment(block.Name), directory: true));
        Assert.Equal(1, block.Exports);
    }

    [Theory, InlineData(false), InlineData(true)]
    public void V14_SCL_move_refuses_before_creating_groups_or_exporting(bool apply)
    {
        var engine = new PathEngine("14sp1");
        var block = AddBlock(AddGroup(engine.Plc.BlockGroup, "From"), "SCL_FC");
        block.Language = "SCL";
        var error = Assert.Throws<AdapterPreconditionException>(() => apply
            ? engine.ApplyMove("From/SCL_FC", "New", "", "stale") : engine.MovePlcBlockToGroup("From/SCL_FC", "New"));
        Assert.Equal("blockName", error.ParamName);
        Assert.Equal(0, block.Exports);
        Assert.Same(block, engine.ExactBlock("From/SCL_FC"));
        Assert.Null(engine.BlockGroup("New"));
    }

    [Theory, InlineData("14sp1", true), InlineData("14sp1", false), InlineData("15.1", true), InlineData("15.1", false)]
    public void Verified_Override_admits_the_live_SCL_block_before_export_or_import(string release, bool preview)
    {
        var engine = new PathEngine(release);
        var block = AddBlock(AddGroup(engine.Plc.BlockGroup, "From"), "FC");
        block.Language = "SCL";
        var request = new PlcSoftwareRequest { SoftwarePath = "PLC", Path = "From/FC", DryRun = preview };
        var candidate = new PlcVerifiedCandidate { Name = "FC", Xml = """
            <Document><Engineering version="V14 SP1"/><SW.Blocks.FC ID="0"><AttributeList>
            <Name>FC</Name><Number>1</Number><Interface><Sections/></Interface></AttributeList></SW.Blocks.FC></Document>
            """ };
        int executions = 0;
        var error = Assert.Throws<AdapterPreconditionException>(() => engine.VerifiedImport(request, candidate, _ => { executions++; return ""; }));
        Assert.Equal("incomplete-block-xml", error.Condition);
        Assert.Equal(0, executions);
        Assert.Equal(0, block.Exports);
    }

    [Fact]
    public void Verified_import_selects_a_unique_UDT_declaration_from_the_bound_PLC_type_groups()
    {
        var engine = new PathEngine("14sp1");
        var block = AddBlock(AddGroup(engine.Plc.BlockGroup, "MCP_RC_Grp"), "MCP_RC_DB");
        block.Language = "DB";
        var types = new TypeGroup { Name = "MCP_RC_TGrp" };
        engine.Plc.TypeGroup.Groups.Add(types);
        const string declaration = """
            <Document><Engineering version="V14 SP1"/><SW.Types.PlcStruct ID="0"><AttributeList><Interface>
            <Sections xmlns="http://www.siemens.com/automation/Openness/SW/Interface/v2"><Section Name="None">
            <Member Name="Run" Datatype="Bool"/><Member Name="Speed" Datatype="Int"/></Section></Sections>
            </Interface><Name>MCP_RC_UDT</Name></AttributeList></SW.Types.PlcStruct></Document>
            """;
        types.Types.Add(new PlcType { Name = "MCP_RC_UDT", Declaration = declaration });
        var request = new PlcSoftwareRequest { SoftwarePath = "PLC", Path = "MCP_RC_Grp/MCP_RC_DB", CompileAfterImport = true };
        var candidate = new PlcVerifiedCandidate { Name = "MCP_RC_DB", Xml = """
            <Document><Engineering version="V14 SP1"/><SW.Blocks.GlobalDB ID="0"><AttributeList>
            <Name>MCP_RC_DB</Name><ProgrammingLanguage>DB</ProgrammingLanguage><Interface><Sections/></Interface>
            </AttributeList></SW.Blocks.GlobalDB></Document>
            """ };
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".xml");
        try
        {
            engine.VerifiedImport(request, candidate, execution => {
                Assert.False(execution.ExportType("Missing", path));
                Assert.True(execution.ExportType("MCP_RC_UDT", path));
                Assert.Equal(declaration, File.ReadAllText(path));
                return "";
            });
            types.Types.Single().ExportFailure = new InvalidOperationException("Export failed on inconsistent UDT.");
            foreach (bool preview in new[] { false, true })
            {
                request.DryRun = preview;
                engine.VerifiedImport(request, candidate, execution => {
                    Assert.False(execution.ExportType("MCP_RC_UDT", path));
                    return "";
                });
            }
            types.Types.Single().ExportFailure = null;
            engine.Plc.TypeGroup.Types.Add(new PlcType { Name = "MCP_RC_UDT" });
            engine.VerifiedImport(request, candidate, execution => {
                Assert.False(execution.ExportType("MCP_RC_UDT", path));
                Assert.Equal(declaration, File.ReadAllText(path));
                return "";
            });
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Move_keeps_the_original_when_the_export_has_no_known_SCL_producer()
    {
        var engine = new PathEngine("15.1");
        var source = AddGroup(engine.Plc.BlockGroup, "From");
        AddGroup(engine.Plc.BlockGroup, "To");
        var block = AddBlock(source, "FC");
        block.Language = "SCL";
        var calls = new List<string>();
        source.OnCall = calls.Add;
        var preview = engine.MovePlcBlockToGroup("From/FC", "To");
        string file = Path.Combine(AppContext.BaseDirectory, "recovery-" + Guid.NewGuid().ToString("N") + ".xml");
        try
        {
            var error = Assert.Throws<IOException>(() => engine.ApplyMove("From/FC", "To", file, (string)preview.Meta["planHash"]!));
            var evidence = Assert.IsType<Dictionary<string, object?>>(error.Data["nativeResultEvidence"]);
            Assert.Equal("verify-export", evidence["stage"]);
            Assert.Equal(false, evidence["deleteStarted"]);
            Assert.Equal(true, evidence["mayHaveWrittenFiles"]);
            Assert.Contains(file, error.Message, StringComparison.Ordinal);
            Assert.False(evidence.ContainsKey("recoveryExportPath"));
            Assert.Equal(new[] { "export FC" }, calls);
            Assert.True(File.Exists(file));
            Assert.Same(block, engine.ExactBlock("From/FC"));
        }
        finally { File.Delete(file); }
    }

    [Theory, MemberData(nameof(NamedReleases))]
    public void Logic_reads_use_the_tree_encoded_slash_and_Chinese_names(string release, string name)
    {
        var engine = new PathEngine(release);
        var group = AddGroup(engine.Plc.BlockGroup, "G");
        var block = AddBlock(group, name);
        string path = engine.TreePath("G", name);
        Assert.Same(block, Export(engine, path));
        Assert.Same(block, engine.ExactBlock(path));
        Assert.Equal(1, block.Exports);
    }

    [Theory, MemberData(nameof(Releases))]
    public void Returned_block_paths_round_trip_every_segment(string release)
    {
        var engine = new PathEngine(release);
        var group = AddGroup(engine.Plc.BlockGroup, "组/一");
        var block = AddBlock(group, "A/B%");
        string path = engine.TreePath(ObjectPathEncoding.EncodeSegment(group.Name), block.Name);
        Assert.Equal(path, engine.BlockPath(block));
        Assert.Same(block, engine.ExactBlock(engine.BlockPath(block)));
        Assert.Same(group, engine.BlockGroup(Uri.EscapeDataString(group.Name)));
    }

    [Theory, MemberData(nameof(Releases))]
    public void Types_and_type_groups_decode_once(string release)
    {
        var engine = new PathEngine(release);
        var group = new TypeGroup { Name = "组/一" };
        engine.Plc.TypeGroup.Groups.Add(group);
        var type = new PlcType { Name = "UDT/A%" };
        group.Types.Add(type);
        Assert.Same(type, engine.ExactType(engine.TreePath(Uri.EscapeDataString(group.Name), type.Name)));
        Assert.Same(group, engine.TypeGroup(Uri.EscapeDataString(group.Name)));
    }

    [Theory, MemberData(nameof(Releases))]
    public void Raw_read_fallback_is_unique_and_exports_to_a_directory(string release)
    {
        var engine = new PathEngine(release);
        var group = AddGroup(engine.Plc.BlockGroup, "G");
        var block = AddBlock(group, "A/B");
        Assert.Same(block, Export(engine, "G/A/B", directory: true));
        Assert.Null(engine.ExactBlock("G/A/B"));
    }

    [Theory, MemberData(nameof(Releases))]
    public void Exact_read_wins_over_a_raw_name_candidate(string release)
    {
        var engine = new PathEngine(release);
        var group = AddGroup(engine.Plc.BlockGroup, "G");
        var slash = AddBlock(group, "A/B");
        var nested = AddBlock(AddGroup(group, "A"), "B");
        Assert.Same(nested, Export(engine, "G/A/B"));
        Assert.Same(slash, Export(engine, "G/A%2FB"));
        Assert.Same(nested, Export(engine, engine.BlockPath(nested)));
        Assert.Same(slash, Export(engine, engine.BlockPath(slash)));
    }

    [Theory, MemberData(nameof(Releases))]
    public void Fallback_decodes_each_remainder_segment_once(string release)
    {
        var engine = new PathEngine(release);
        var group = AddGroup(engine.Plc.BlockGroup, "G");
        var block = AddBlock(group, "A/中文%");
        var literalEscape = AddBlock(group, "A/%2F");
        var backslash = AddBlock(group, "A\\B/C");
        Assert.Same(block, Export(engine, "G/A/%E4%B8%AD%E6%96%87%25"));
        Assert.Same(literalEscape, Export(engine, "G/A/%252F"));
        Assert.Same(backslash, Export(engine, "G/A%5CB/C"));
    }

    [Theory, MemberData(nameof(Releases))]
    public void Encoded_backslashes_are_names_and_raw_backslashes_are_refused(string release)
    {
        var engine = new PathEngine(release);
        var group = AddGroup(engine.Plc.BlockGroup, "G\\H");
        var block = AddBlock(group, "A\\B");
        Assert.Same(block, engine.ExactBlock("G%5CH/A%5CB"));
        Assert.Same(block, Export(engine, "G%5CH/A%5CB"));
        Assert.Throws<AdapterPreconditionException>(() => engine.ExactBlock("G\\H/A%5CB"));
        Assert.Throws<AdapterPreconditionException>(() => engine.ExactBlock("G%5CH/A\\B"));
        var types = new TypeGroup { Name = "G\\H" };
        engine.Plc.TypeGroup.Groups.Add(types);
        var type = new PlcType { Name = "A\\B" };
        types.Types.Add(type);
        Assert.Same(type, engine.ExactType("G%5CH/A%5CB"));
        Assert.Throws<AdapterPreconditionException>(() => engine.ExactType("G\\H/A%5CB"));
        Assert.Throws<AdapterPreconditionException>(() => engine.ExactType("G%5CH/A\\B"));
    }

    [Theory, MemberData(nameof(Releases))]
    public void Group_paths_return_the_canonical_names_for_round_trip_and_callbacks(string release)
    {
        var engine = new PathEngine(release);
        var group = AddGroup(AddGroup(engine.Plc.BlockGroup, "组/A"), "B\\C%2F");
        string expected = "组%2FA/B%5CC%252F";
        Assert.Equal(expected, engine.GroupPath(group));
        Assert.Same(group, engine.BlockGroup(engine.GroupPath(group)));
        Assert.Equal("", engine.GroupPath(engine.Plc.BlockGroup));
    }

    [Theory, MemberData(nameof(Releases))]
    public void Explicit_group_and_object_resolution_preserves_decoded_segment_boundaries(string release)
    {
        var engine = new PathEngine(release);
        var group = AddGroup(AddGroup(engine.Plc.BlockGroup, "G/A"), "B\\C%2F");
        var block = AddBlock(group, "Block/A%2F");
        Func<string, string[]> parse = value => TiaMcp.Adapters.Native.Plc.PlcObjectPath.Parts(value, true);
        string groupPath = "G%2FA/B%5CC%252F";
        Assert.Same(group, TiaMcp.Adapters.Native.Plc.PlcGroupOperations.Group(engine.Plc.BlockGroup, groupPath, pathParts: parse));
        var parts = TiaMcp.Adapters.Native.Plc.PlcObjectPath.Parts(groupPath + "/Block%2FA%252F", parameter: "objectPath");
        var parent = TiaMcp.Adapters.Native.Plc.PlcGroupOperations.Group(engine.Plc.BlockGroup,
            TiaMcp.Adapters.Native.Plc.PlcObjectPath.Format(parts.Take(parts.Length - 1)), pathParts: parse);
        Assert.Same(block, TiaMcp.Adapters.Native.Plc.PlcGroupOperations.Find(
            TiaMcp.Adapters.Native.Plc.PlcGroupOperations.Get(parent, "Blocks"), parts.Last()));
    }

    [Theory, MemberData(nameof(Releases))]
    public void Ambiguous_raw_reads_list_both_canonical_candidates(string release)
    {
        var engine = new PathEngine(release);
        var group = AddGroup(engine.Plc.BlockGroup, "G");
        var slash = AddBlock(group, "A/B/C");
        var nested = AddBlock(AddGroup(group, "A"), "B/C");
        var error = Assert.Throws<AdapterPreconditionException>(() => Export(engine, "G/A/B/C"));
        Assert.Equal("blockPath", error.ParamName);
        Assert.Contains("G/A%2FB%2FC", error.Message, StringComparison.Ordinal);
        Assert.Contains("G/A/B%2FC", error.Message, StringComparison.Ordinal);
        Assert.Same(slash, Export(engine, "G/A%2FB%2FC"));
        Assert.Same(nested, Export(engine, "G/A/B%2FC"));
        Assert.Same(nested, engine.ExactBlock("G/A/B%2FC"));
    }

    [Fact]
    public void V21_direct_and_preview_queries_dispatch_to_the_selected_block_without_consent()
    {
        var engine = new PathEngine("21");
        var group = AddGroup(engine.Plc.BlockGroup, "Group");
        var block = AddBlock(group, "Target");
        const string path = "Group/Target";
        var rows = engine.BlockReferences.GetCrossReferences("PLC", path, "Block", "ObjectsWithReferences",
            out var reason, out bool queried, "", "unit");
        Assert.True(queried, reason);
        Assert.Null(reason);
        Assert.Equal("Caller", Assert.Single(rows!).ReferenceName);
        Assert.Equal("ObjectsWithReferences", Assert.Single(block.Service.Queries).ToString());

        var ordinaryPreview = engine.DeletePlcBlock(path, dryRun: true);
        Assert.Equal(false, ordinaryPreview["crossReferenceQueried"]);
        Assert.Single(block.Service.Queries);
        var preview = engine.DeletePlcBlock(path, dryRun: true, crossReferences: true);
        Assert.Equal(true, preview["crossReferenceQueried"]);
        Assert.Equal(1, preview["crossReferenceCount"]);
        Assert.Equal(new[] { "ObjectsWithReferences", "AllObjects" }, block.Service.Queries.Select(filter => filter.ToString()));
        Assert.Same(block, Assert.Single(group.Blocks));
    }

    [Fact]
    public void V21_consistency_failure_prevents_both_reference_queries()
    {
        var engine = new PathEngine("21");
        var group = AddGroup(engine.Plc.BlockGroup, "Group");
        var block = AddBlock(group, "Target");
        AddBlock(group, "Uncompiled").IsConsistent = false;
        var rows = engine.BlockReferences.GetCrossReferences("PLC", "Group/Target", "Block", "AllObjects",
            out var reason, out bool queried, "", "unit");
        Assert.False(queried);
        Assert.Null(rows);
        Assert.Contains("Group/Uncompiled", reason);
        var preview = engine.DeletePlcBlock("Group/Target", dryRun: true, crossReferences: true);
        Assert.Equal(false, preview["crossReferenceQueried"]);
        Assert.Null(preview["crossReferenceCount"]);
        Assert.Equal(reason, preview["crossReferenceUnavailableReason"]);
        Assert.Empty(block.Service.Queries);
    }

    [Theory, MemberData(nameof(Releases))]
    public void Delete_and_move_never_use_raw_name_fallback(string release)
    {
        var engine = new PathEngine(release);
        var group = AddGroup(engine.Plc.BlockGroup, "G");
        var slash = AddBlock(group, "A/B");
        Assert.Equal("NotFound", Assert.Throws<PlcSoftwareException>(() => engine.DeletePlcBlock("G/A/B")).Code);
        Assert.Equal("NotFound", Assert.Throws<PlcSoftwareException>(() => engine.MovePlcBlockToGroup("G/A/B", "G")).Code);
        var move = engine.MovePlcBlockToGroup("G/A%2FB", "G");
        var plan = Assert.IsType<Dictionary<string, object?>>(move.Meta["plan"]);
        Assert.Equal("G/A%2FB", plan["sourcePath"]);
        Assert.Contains(slash, group.Blocks);
        var nestedGroup = AddGroup(group, "A");
        var nested = AddBlock(nestedGroup, "B");
        var nestedMove = engine.MovePlcBlockToGroup("G/A/B", "G");
        Assert.Equal("G/A/B", Assert.IsType<Dictionary<string, object?>>(nestedMove.Meta["plan"])["sourcePath"]);
        var deleted = engine.DeletePlcBlock("G/A%2FB");
        Assert.Equal("G/A%2FB", deleted["resolvedBlockPath"]);
        Assert.Equal(true, deleted["verifiedAbsent"]);
        Assert.DoesNotContain(slash, group.Blocks);
        Assert.Contains(nested, nestedGroup.Blocks);
        engine.DeletePlcBlock("G/A/B");
        Assert.Empty(nestedGroup.Blocks);
    }

    [Theory, MemberData(nameof(Releases))]
    public void Missing_reads_preserve_the_declared_blockPath_target(string release)
    {
        var error = Assert.Throws<AdapterPreconditionException>(() => Export(new PathEngine(release), "G/Missing"));
        Assert.Equal("not-found", error.Condition);
        Assert.Equal("blockPath", error.ParamName);
        Assert.Equal("NotFound", StepFailureStatus.Of(error));
    }

    public static IEnumerable<object[]> InvalidPaths() => Releases().SelectMany(row =>
        new[] { "", "/A", "A/", "A//B", ".", "%2e%2e", "A\\B", "%20", string.Join("/", Enumerable.Repeat("G", 65)) }
            .Select(path => new object[] { row[0], path }));

    [Theory, MemberData(nameof(InvalidPaths))]
    public void Invalid_paths_are_rejected_after_decoding(string release, string path)
    {
        var engine = new PathEngine(release);
        Assert.Throws<AdapterPreconditionException>(() => engine.ExactBlock(path));
        Assert.Throws<AdapterPreconditionException>(() => engine.ExactType(path));
    }

    [Theory, MemberData(nameof(Releases))]
    public void Sixty_four_segments_remain_valid(string release)
    {
        var engine = new PathEngine(release);
        var root = engine.Plc.BlockGroup;
        Group? last = null;
        for (int index = 0; index < 63; index++) { last = AddGroup(root, "G"); root = last; }
        var block = AddBlock(last!, "B");
        string path = string.Join("/", Enumerable.Repeat("G", 63).Append("B"));
        Assert.Same(block, engine.ExactBlock(path));
    }

    [Theory, MemberData(nameof(Releases))]
    public void Group_edits_keep_relative_encoded_paths(string release)
    {
        Assert.Contains(Releases(), row => (string)row[0] == release);
        var root = new Siemens.Engineering.SW.Blocks.PlcBlockGroup();
        Func<string, string[]> parse = value => TiaMcp.Adapters.Native.Plc.PlcObjectPath.Parts(value, true);
        var created = TiaMcp.Adapters.Native.Plc.PlcGroupOperations.Manage(root, "G%2FA/Sub%25", "create", "", false, "Blocks", pathParts: parse);
        Assert.Equal("G%2FA/Sub%25", created["groupPath"]);
        var parent = Assert.Single(root.Groups);
        Assert.Equal("G/A", parent.Name);
        Assert.Equal("Sub%", Assert.Single(parent.Groups).Name);
        var refusal = Assert.Throws<AdapterPreconditionException>(() =>
            TiaMcp.Adapters.Native.Plc.PlcGroupOperations.Manage(root, "G%2FA", "deleteEmpty", "", true, "Blocks", pathParts: parse));
        var evidence = new Dictionary<string, object?> { ["mayHaveChanged"] = false };
        MutationEvidence.RecordStepFailure(refusal, evidence);
        Assert.Equal("PRECONDITION_FAILED", evidence["v4FailureCode"]);
        Assert.Equal("PreconditionFailed", evidence["status"]);
        Assert.Equal("group-not-empty", evidence["precondition"]);
        TiaMcp.Adapters.Native.Plc.PlcGroupOperations.Manage(root, "G%2FA/Sub%25", "rename", "Renamed", false, "Blocks", pathParts: parse);
        Assert.Equal("Renamed", Assert.Single(parent.Groups).Name);
        TiaMcp.Adapters.Native.Plc.PlcGroupOperations.Manage(root, "G%2FA/Renamed", "deleteEmpty", "", false, "Blocks", pathParts: parse);
        Assert.Empty(parent.Groups);
    }

    [Theory, MemberData(nameof(Releases))]
    public void Group_manager_resolves_empty_parent_for_root_group_edits(string release)
    {
        var engine = new TiaMcp.Adapters.PlcFoundationEngine(release);
        engine.ConfigureFixture();
        engine.ProjectConnectionCreated!(engine.Connection);
        engine.ProjectRetrieved!(engine.NewProject("PathFixture"));
        var software = new Siemens.Engineering.SW.PlcSoftware();
        var adapter = new TiaMcp.Adapters.Native.Plc.PlcOrganisationAdapter(new TiaMcp.Adapters.Native.Plc.GroupSession(engine, software));
        var created = adapter.ManagePlcUserGroup("PLC", "blocks", "G%2FA", "create", dryRun: false);
        Assert.True((bool)created.Meta["operationSuccess"]!);
        var renamed = adapter.ManagePlcUserGroup("PLC", "blocks", "G%2FA", "rename", "Renamed", false);
        Assert.True((bool)renamed.Meta["operationSuccess"]!);
        Assert.Equal("Renamed", Assert.Single(software.BlockGroup.Groups).Name);
        var deleted = adapter.ManagePlcUserGroup("PLC", "blocks", "Renamed", "deleteEmpty", dryRun: false);
        Assert.True((bool)deleted.Meta["operationSuccess"]!);
        Assert.Empty(software.BlockGroup.Groups);
    }

    private static Group AddGroup(plcpathfixture::Siemens.Engineering.SW.Blocks.PlcBlockGroup parent, string name)
    {
        var group = new Group { Name = name, Parent = parent };
        parent.Groups.Add(group);
        return group;
    }

    private static Block AddBlock(Group group, string name)
    {
        var block = new Block { Name = name, Parent = group };
        group.Blocks.Add(block);
        return block;
    }

    private static Block? Export(PathEngine engine, string path, bool directory = false)
    {
        string file = Path.Combine(Path.GetTempPath(), "plc-path-" + Guid.NewGuid().ToString("N") + ".xml");
        if (directory) Directory.CreateDirectory(file);
        try { return engine.DescribePlcBlockLogic(path, file); }
        finally
        {
            if (directory) Directory.Delete(file, true);
            else if (File.Exists(file)) File.Delete(file);
        }
    }
}
