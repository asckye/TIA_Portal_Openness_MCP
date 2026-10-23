using System;
using System.Linq;
using System.Text;
using TiaMcp.Adapters.Contracts;
using Xunit;

namespace TiaMcp.Adapters.Contracts.Tests;

// P8-81d (API audit 2026-10-19, PLC software): the SimaticML import window (F6), the move, instance-DB and import-order rules
// (F18, F21, F22, F9), the rename gates (F20), document messages (F10) and the V20 composer withdrawal (2026-10-20 decision).
public sealed class PlcSoftwareAuditRulesTests
{
    private static byte[] Document(string? version) => Encoding.UTF8.GetBytes("<Document>" + (version == null ? "" : "<Engineering version=\"" + version + "\" />")
        + "<SW.Blocks.FC ID=\"0\"><AttributeList><Name>FC_1</Name><Number>1</Number></AttributeList></SW.Blocks.FC></Document>");

    // Each release's own header spelling ("V14 SP1", "V15.1", "V16" ...) parses and is inside its own window.
    [Theory]
    [InlineData("14sp1", "V14 SP1")][InlineData("15.1", "V15.1")][InlineData("16", "V16")][InlineData("17", "V17")]
    [InlineData("18", "V18")][InlineData("19", "V19")][InlineData("20", "V20")][InlineData("21", "V21")]
    public void Every_release_header_form_parses_and_imports_on_its_own_release(string release, string header)
    {
        Assert.True(SimaticMlImportWindow.TryParse(header, out var version));
        Assert.Equal(SimaticMlImportWindow.ApiVersion(release), version);
        Assert.Null(SimaticMlImportWindow.Refusal(release, header));
        SimaticMlImportWindow.Require(release, Document(header), "importPath");
    }

    [Theory]
    [InlineData("V13 SP1", 13, 1)][InlineData("V14", 14, 0)][InlineData("V14 SP1", 14, 1)][InlineData("V15", 15, 0)][InlineData("V15.1", 15, 1)][InlineData("V21", 21, 0)]
    public void Old_header_spellings_are_comparable(string header, int major, int minor)
    {
        Assert.True(SimaticMlImportWindow.TryParse(header, out var version));
        Assert.Equal(new Version(major, minor), version);
    }

    [Theory]
    [InlineData("")][InlineData("21")][InlineData("V")][InlineData("V15.1 SP1")][InlineData("v17")][InlineData("V17 Update 2")][InlineData("V9")]
    public void Unknown_header_text_is_not_a_version(string header) => Assert.False(SimaticMlImportWindow.TryParse(header, out _));

    // Up to V18 the API version must be at least the file's version; from V19 the file must lie within Vxx-3..Vxx.
    [Theory]
    [InlineData("18", "V14 SP1", true)][InlineData("18", "V13 SP1", true)][InlineData("18", "V18", true)][InlineData("18", "V19", false)]
    [InlineData("14sp1", "V14", true)][InlineData("14sp1", "V15", false)][InlineData("15.1", "V15", true)][InlineData("15.1", "V16", false)]
    [InlineData("19", "V15.1", false)][InlineData("19", "V16", true)][InlineData("19", "V19", true)][InlineData("19", "V20", false)]
    [InlineData("20", "V16", false)][InlineData("20", "V17", true)][InlineData("20", "V20", true)][InlineData("20", "V21", false)]
    [InlineData("21", "V17", false)][InlineData("21", "V18", true)][InlineData("21", "V21", true)]
    public void Release_boundaries_follow_the_documented_window(string release, string header, bool inside)
    {
        var refusal = SimaticMlImportWindow.Refusal(release, header);
        Assert.Equal(inside, refusal == null);
        if (inside) { SimaticMlImportWindow.Require(release, Document(header), "importPath"); return; }
        Assert.Contains(header, refusal, StringComparison.Ordinal);
        Assert.Contains(SimaticMlImportWindow.Window(release), refusal, StringComparison.Ordinal);
        Assert.Contains("never rewrites", refusal, StringComparison.Ordinal);
        var error = Assert.Throws<AdapterPreconditionException>(() => SimaticMlImportWindow.Require(release, Document(header), "importPath"));
        Assert.False(error.IsArgument);
        Assert.Equal("importPath", error.ParamName);
    }

    [Fact]
    public void Window_text_names_each_release_range()
    {
        Assert.Equal("engineering versions up to V18", SimaticMlImportWindow.Window("18"));
        Assert.Equal("engineering versions up to V14 SP1", SimaticMlImportWindow.Window("14sp1"));
        Assert.Equal("engineering versions V16 to V19", SimaticMlImportWindow.Window("19"));
        Assert.Equal("engineering versions V17 to V20", SimaticMlImportWindow.Window("20"));
        Assert.Equal("engineering versions V18 to V21", SimaticMlImportWindow.Window("21"));
    }

    [Fact]
    public void A_missing_or_ambiguous_header_is_refused_and_never_added()
    {
        Assert.Throws<AdapterPreconditionException>(() => SimaticMlImportWindow.Require("21", Document(null), "importPath"));
        var twice = Encoding.UTF8.GetBytes("<Document><Engineering version=\"V21\" /><Engineering version=\"V20\" /><SW.Blocks.FC><AttributeList><Name>A</Name></AttributeList></SW.Blocks.FC></Document>");
        Assert.Contains("no single Engineering version", Assert.Throws<AdapterPreconditionException>(() => SimaticMlImportWindow.Require("21", twice, "dir")).Message, StringComparison.Ordinal);
        var malformed = Assert.Throws<AdapterPreconditionException>(() => SimaticMlImportWindow.Require("21", Encoding.UTF8.GetBytes("<Document><Engineering"), "importPath"));
        Assert.True(malformed.IsArgument);
        var entity = Encoding.UTF8.GetBytes("<!DOCTYPE Document [<!ENTITY x 'V21'>]><Document><Engineering version=\"&x;\" /></Document>");
        Assert.Throws<AdapterPreconditionException>(() => SimaticMlImportWindow.Require("21", entity, "importPath"));
    }

    [Fact]
    public void The_object_kind_and_name_come_from_the_first_software_object()
    {
        Assert.Equal(("FC", "FC_1"), SimaticMlImportWindow.ReadObject(SimaticMlImportWindow.Load(Document("V21"))));
        var table = SimaticMlImportWindow.Load(Encoding.UTF8.GetBytes("<Document><Engineering version=\"V20\" /><SW.Tags.PlcTagTable><AttributeList><Name> Tags </Name></AttributeList></SW.Tags.PlcTagTable></Document>"));
        Assert.Equal(("PlcTagTable", "Tags"), SimaticMlImportWindow.ReadObject(table));
        Assert.Null(SimaticMlImportWindow.ReadObject(SimaticMlImportWindow.Load(Encoding.UTF8.GetBytes("<Document><Engineering version=\"V20\" /></Document>"))));
    }

    [Fact]
    public void Know_how_protected_and_inconsistent_blocks_are_refused_before_a_move()
    {
        Assert.Contains("know-how protected", PlcMovePolicy.Refusal(true, true), StringComparison.Ordinal);
        Assert.Contains("know-how protected", PlcMovePolicy.Refusal(true, false), StringComparison.Ordinal);
        Assert.Contains("inconsistent", PlcMovePolicy.Refusal(false, false), StringComparison.Ordinal);
        Assert.Null(PlcMovePolicy.Refusal(false, true));
    }

    [Fact]
    public void A_moved_block_keeps_its_number_or_the_change_is_reported()
    {
        Assert.Equal(17, PlcMovePolicy.Number(17));
        Assert.Equal(17, PlcMovePolicy.Number("17"));
        Assert.Null(PlcMovePolicy.Number(null));
        Assert.Null(PlcMovePolicy.Number("DB17"));
        Assert.Null(PlcMovePolicy.NumberChange(17, 17));
        Assert.Contains("before 17, after 18", PlcMovePolicy.NumberChange(17, 18), StringComparison.Ordinal);
        Assert.Contains("before 17, after (unreadable)", PlcMovePolicy.NumberChange(17, null), StringComparison.Ordinal);
        Assert.Contains("before (unreadable), after 3", PlcMovePolicy.NumberChange(null, 3), StringComparison.Ordinal);
    }

    [Fact]
    public void Instance_db_numbers_and_sources_follow_the_sdk()
    {
        Assert.NotNull(PlcInstanceDbPolicy.Refusal(false, 0));
        Assert.NotNull(PlcInstanceDbPolicy.Refusal(true, -1));
        Assert.Null(PlcInstanceDbPolicy.Refusal(false, 1));
        Assert.Null(PlcInstanceDbPolicy.Refusal(true, 0));
        foreach (var release in new[] { "15.1", "16" })
        {
            Assert.Contains("ProDiag", PlcInstanceDbPolicy.SourceRefusal(release, "LAD"), StringComparison.Ordinal);
            Assert.Null(PlcInstanceDbPolicy.SourceRefusal(release, "ProDiag"));
        }
        foreach (var release in new[] { "17", "18", "19", "20", "21" }) Assert.Null(PlcInstanceDbPolicy.SourceRefusal(release, "LAD"));
    }

    [Fact]
    public void Blocks_import_in_the_official_order()
    {
        var kinds = new[] { "OB", "InstanceDB", "GlobalDB", "FC", "ArrayDB", "FB" };
        Assert.Equal(new[] { "FC", "FB", "GlobalDB", "ArrayDB", "InstanceDB", "OB" }, kinds.OrderBy(PlcImportOrder.BlockRank).ToArray());
        Assert.True(PlcImportOrder.BlockRank("OB") < PlcImportOrder.BlockRank("Unknown"));
    }

    // SDK DLL metadata: the user-group Name setter exists from V21 (blocks, types, watch tables) and V20 (tag tables).
    [Theory]
    [InlineData("blocks", "20", false)][InlineData("blocks", "21", true)][InlineData("types", "19", false)][InlineData("types", "21", true)]
    [InlineData("watchTables", "20", false)][InlineData("watchTables", "21", true)][InlineData("tags", "19", false)][InlineData("tags", "20", true)]
    [InlineData("technology", "19", true)][InlineData("externalSources", "20", false)][InlineData("externalSources", "21", true)]
    public void Group_rename_needs_the_public_name_setter(string family, string release, bool available)
    {
        Assert.Equal(available, PlcSoftwareCapabilities.Unsupported(release, "ManagePlcUserGroup", "rename", family) == null);
        Assert.Null(PlcSoftwareCapabilities.Unsupported(release, "ManagePlcUserGroup", "deleteEmpty", family));
    }

    [Theory]
    [InlineData("20", "fc", false)][InlineData("20", "fb", false)][InlineData("21", "fc", true)][InlineData("21", "fb", true)]
    [InlineData("20", "udt", true)][InlineData("20", "tagtable", true)][InlineData("20", "globaldb", true)][InlineData("17", "fc", false)]
    public void Build_and_import_writes_fc_and_fb_documents_on_v21_only(string release, string kind, bool available)
        => Assert.Equal(available, PlcSoftwareCapabilities.Unsupported(release, "BuildAndImportPlcArtifact", family: kind) == null);

    [Fact]
    public void V20_withdraws_the_four_v21_whole_block_composers()
    {
        foreach (var tool in new[] { "BuildPlcFcBlock", "BuildPlcFbBlock", "BuildPlcLadFcBlock", "BuildPlcAliasAlarmLad" })
        {
            Assert.False(PortedFamilies.Available("20", tool), tool);
            Assert.True(PortedFamilies.Available("21", tool), tool);
        }
        Assert.True(PortedFamilies.Available("20", "BuildFlgNetCall"));
    }

    [Fact]
    public void Document_messages_travel_bounded_with_the_failure()
    {
        var many = Enumerable.Range(1, 10).Select(i => "message " + i).Append(new string('x', 600)).ToArray();
        var error = new NativeResultException(NativeResultStates.Documents, "PartialSuccess", many);
        Assert.StartsWith("Native operation returned PartialSuccess. Native messages: message 1 | ", error.Message, StringComparison.Ordinal);
        Assert.Contains("message 8", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("message 9", error.Message, StringComparison.Ordinal);
        Assert.EndsWith("(3 more not shown)", error.Message, StringComparison.Ordinal);
        Assert.Equal("PartialSuccess", error.Evidence.State);
        Assert.Equal("Native operation returned Failure.", new NativeResultException(NativeResultStates.Documents, "Failure", Array.Empty<string>()).Message);
        Assert.Equal(512 + 3, NativeResultException.BoundedMessages(new[] { new string('y', 700) }).Length - " Native messages: ".Length);
        Assert.False(NativeResultStates.Succeeded(NativeResultStates.Documents, "PartialSuccess"));
    }
}
