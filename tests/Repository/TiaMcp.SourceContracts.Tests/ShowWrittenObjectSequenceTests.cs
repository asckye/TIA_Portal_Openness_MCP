using System.Text.Json.Nodes;
using TiaMcp.BuildCommon;
using Xunit;

namespace TiaMcp.SourceContracts.Tests;

public sealed class ShowWrittenObjectSequenceTests
{
    private static bool WritesObject(JsonNode? step, string? kind = null)
    {
        if ((bool?)step!["arguments"]?["dryRun"] != false) return false;
        string tool = (string)step["tool"]!;
        return kind switch {
            "device" => tool is "EnsureSubnet" or "AttachDeviceNodeToSubnet" or "ConnectDeviceNodesToProfinetSubnet",
            "plcWatchTable" => tool is "SetPlcWatchTableModifyValue" or "ImportPlcWatchTableOffline",
            "plcType" => tool is "ImportPlcType" or "ImportPlcProgramFromDirectory" or "GenerateBlocksFromExternalSource",
            "plcTagTable" => tool is "ImportPlcTagTable" or "CreatePlcTagTable" or "CreatePlcTag" or "CreatePlcUserConstant",
            "plcBlock" => tool is "ImportPlcBlock" or "ImportPlcBlockDocuments" or "ImportPlcProgramFromDirectory"
                or "BuildAndImportPlcArtifact" or "GenerateBlocksFromExternalSource",
            null => tool is "ImportPlcBlock" or "ImportPlcBlockDocuments" or "ImportPlcProgramFromDirectory"
                or "BuildAndImportPlcArtifact" or "GenerateBlocksFromExternalSource" or "CreatePlcTag" or "ImportPlcTagTable"
                or "ImportPlcType" or "CreatePlcTagTable",
            _ => false
        };
    }

    [Fact]
    public void Every_automatic_show_has_a_real_write_of_that_object_kind_before_it()
    {
        var sequences = JsonNode.Parse(File.ReadAllText(Path.Combine(Repository.FindRoot(AppContext.BaseDirectory),
            "reference/tool-examples/sequences.json")))!.AsArray();
        foreach (var sequence in sequences)
        {
            var steps = sequence!["steps"]!.AsArray();
            for (int index = 0; index < steps.Count; index++)
                if ((string?)steps[index]!["tool"] == "ShowObjectInEditor")
                    Assert.Contains(steps.Take(index), step => WritesObject(step, (string?)steps[index]!["arguments"]!["kind"]));
        }
    }

    [Fact]
    public void Watch_table_and_network_sequences_show_the_completed_target_after_readback()
    {
        string root = Repository.FindRoot(AppContext.BaseDirectory);
        var sequences = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "reference/tool-examples/sequences.json")))!.AsArray();
        foreach (var (id, kind, readback) in new[] { ("watch-table", "plcWatchTable", "ManagePlcTableEntries"),
            ("hardware-network", "device", "GetDeviceItemNetworkInfo") })
        {
            var steps = sequences.Single(s => (string?)s!["id"] == "sequence/" + id)!["steps"]!.AsArray();
            Assert.Equal(readback, (string?)steps[^2]!["tool"]);
            Assert.Equal("ShowObjectInEditor", (string?)steps[^1]!["tool"]);
            Assert.Equal(kind, (string?)steps[^1]!["arguments"]!["kind"]);
            Assert.False((bool)steps[^1]!["arguments"]!["dryRun"]!);
            if (kind == "device") Assert.Equal("network", (string?)steps[^1]!["arguments"]!["view"]);
        }
        var calls = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "reference/tool-examples/calls.json")))!;
        foreach (string profile in new[] { "plc-foundation", "full-engine" })
        {
            var show = calls["profiles"]![profile]!["ShowObjectInEditor"]!;
            Assert.Empty(show["operations"]!.AsObject());
            Assert.Contains("sequence/watch-table", (string?)show["note"], StringComparison.Ordinal);
            Assert.Contains("sequence/hardware-network", (string?)show["note"], StringComparison.Ordinal);
            Assert.Equal("showInEditor", (string?)calls["profiles"]![profile]!["ManageTestSuiteCase"]!["operations"]!["showInEditor"]
                !["arguments"]!["action"]);
        }
    }

    public static IEnumerable<object[]> WritingSequences() => new[] {
            "plc-scl-block", "plc-s7dcl-import", "plc-builder", "export-import-block", "plc-scl-block-foundation",
            "foundation-write-tags", "foundation-tag-table-round-trip", "foundation-block-round-trip", "plc-xml-round-trip",
            "stage-scl-function-plc-foundation", "stage-scl-udt-plc-foundation", "stage-program-overwrite-plc-foundation",
            "stage-scl-function", "stage-scl-udt", "stage-program-overwrite" }.Select(id => new object[] { id });

    [Theory, MemberData(nameof(WritingSequences))]
    public void Plc_writing_sequences_finish_by_showing_the_written_objects(string id)
    {
        string root = Repository.FindRoot(AppContext.BaseDirectory);
        var sequences = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "reference/tool-examples/sequences.json")))!.AsArray();
        var sequence = sequences.Single(s => (string?)s!["id"] == "sequence/" + id)!;
        var steps = sequence["steps"]!.AsArray();
        var writes = steps.Select((step, index) => (step, index)).Where(p => WritesObject(p.step)).ToArray();
        if (writes.Length == 0)
        {
            Assert.DoesNotContain(steps, s => (string?)s!["tool"] == "ShowObjectInEditor");
            return;
        }
        Assert.Equal("ShowObjectInEditor", (string?)steps[^1]!["tool"]);
        int lastWrite = writes.Max(p => p.index);
        Assert.All(steps.Where(s => (string?)s!["tool"] == "ShowObjectInEditor"), show =>
        {
            Assert.True(steps.IndexOf(show) > lastWrite, id);
            Assert.False((bool?)show!["arguments"]!["dryRun"] ?? false);
            Assert.Contains("without user interface", (string?)show["expect"], StringComparison.Ordinal);
            Assert.Contains("engineer", (string?)show["expect"], StringComparison.Ordinal);
            Assert.DoesNotContain("expectedPlanHash", show["arguments"]!.AsObject().Select(p => p.Key));
        });
    }

    [Fact]
    public void Shared_pattern_and_both_call_profiles_open_the_written_object_directly()
    {
        string root = Repository.FindRoot(AppContext.BaseDirectory);
        var sequences = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "reference/tool-examples/sequences.json")))!.AsArray();
        var pattern = sequences.Single(s => (string?)s!["id"] == "sequence/show-written-object")!;
        Assert.Equal("ShowObjectInEditor", (string?)pattern["steps"]!.AsArray()[^1]!["tool"]);
        Assert.Equal(8, pattern["releaseKeys"]!.AsArray().Count);
        var calls = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "reference/tool-examples/calls.json")))!;
        foreach (string profile in new[] { "plc-foundation", "full-engine" })
        {
            var args = calls["profiles"]![profile]!["ShowObjectInEditor"]!["arguments"]!.AsObject();
            Assert.False((bool)args["dryRun"]!);
            Assert.DoesNotContain("expectedPlanHash", args.Select(p => p.Key));
            Assert.DoesNotContain("expectedProjectFile", args.Select(p => p.Key));
        }
    }
}
