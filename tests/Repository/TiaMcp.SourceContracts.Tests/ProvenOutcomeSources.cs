using System.Text.RegularExpressions;
using TiaMcp.BuildCommon;
using Xunit;

namespace TiaMcp.SourceContracts.Tests;

public sealed class ProvenOutcomeSources
{
    private static string Read(string path) => Regex.Replace(Repository.ReadSource(
        Path.Combine(Repository.FindRoot(AppContext.BaseDirectory), path)), @"\s+", " ");

    [Fact]
    public void Move_records_the_groups_it_created()
    {
        string source = Read("src/Adapters/Native/Plc/PlcOrganisationAdapter.cs");
        source = source[source.IndexOf("public HardwareAddressingReply MoveBlockToGroup", StringComparison.Ordinal)..];
        int manage = source.IndexOf("PlcGroupOperations.Manage(", StringComparison.Ordinal);
        Assert.True(manage >= 0);
        int captured = source.IndexOf("[\"createdPaths\"]", manage, StringComparison.Ordinal);
        Assert.True(captured > manage);
        int caught = source.IndexOf("catch (Exception", captured, StringComparison.Ordinal);
        Assert.True(caught > captured);
        int evidence = source.IndexOf("[\"targetGroupsCreated\"]", caught, StringComparison.Ordinal);
        Assert.True(evidence > caught);
        int rethrow = source.IndexOf("throw;", evidence, StringComparison.Ordinal);
        Assert.True(manage >= 0 && captured > manage && caught > captured && evidence > caught && rethrow > evidence);
    }

    [Fact]
    public void Verified_import_bridge_uses_plain_values()
    {
        Assert.Contains("=> PlcVerifiedImport.PlainValue(node);", Read("src/PlcWorker/PlcVerifiedImportBridge.cs"), StringComparison.Ordinal);
    }

    [Fact]
    public void Worker_merges_outer_move_evidence_before_inner_evidence()
    {
        Assert.Contains("PlcMovePolicy.MergeEvidence(error, reply.Failure.Evidence)",
            Read("src/Adapters/Native/Plc/PlcOrganisationWorker.cs"), StringComparison.Ordinal);
    }

    [Fact]
    public void Technology_create_registers_the_journal_entry_before_Create()
    {
        string source = Read("src/Adapters/Native/Plc/PlcTechnology.cs");
        int create = source.IndexOf("if (action == \"create\")", StringComparison.Ordinal);
        int hook = source.IndexOf("EngineeringWriteJournal.Creating(", create, StringComparison.Ordinal);
        int branch = source.IndexOf("if (collection is TechnologicalInstanceDBComposition", create, StringComparison.Ordinal);
        Assert.True(create >= 0 && hook > create && hook < branch);
        Assert.Contains("() => PlcGroupOperations.Find(collection, parts.Last())", source, StringComparison.Ordinal);
        Assert.Contains("softwarePath + \"/TechnologicalObjects/\" + objectPath", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Create_preview_says_tia_checks_the_version()
    {
        string source = Read("src/Adapters/Native/Plc/PlcTechnology.cs");
        Assert.Contains("action == \"create\"", source, StringComparison.Ordinal);
        Assert.Contains("Create preview; no changes. TIA checks the type and version against this CPU only when the object is created.",
            source, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Hmi/UnifiedRequestScalar.cs", "change.Property")]
    [InlineData("Hardware/HardwareAddressPrimitives.cs", "change.Property")]
    [InlineData("Hardware/HardwareScalarEvidence.cs", "change.Property")]
    [InlineData("Hmi/UnifiedUiModelLogic.cs", "edit.Property")]
    public void Property_writes_mark_the_change_after_the_journal_hook(string path, string property)
    {
        string source = Read("src/Adapters/Native/" + path);
        int attempt = source.IndexOf("meta[\"lastAttemptedProperty\"]", StringComparison.Ordinal);
        int hook = source.IndexOf("EngineeringWriteJournal.Property(", attempt, StringComparison.Ordinal);
        int mark = source.IndexOf("meta[\"mayHaveChanged\"] = true;", attempt, StringComparison.Ordinal);
        int setter = source.IndexOf(property + ".SetValue(", attempt, StringComparison.Ordinal);
        Assert.True(attempt >= 0 && hook > attempt && mark > hook && setter > mark);
    }

    [Fact]
    public void Event_update_marks_the_change_only_in_apply()
    {
        string source = Read("src/Adapters/Native/Hmi/UnifiedEventsAdapter.cs");
        Assert.Contains("if (action != \"update\") meta[\"mayHaveChanged\"] = true;", source, StringComparison.Ordinal);
        Assert.Contains("UnifiedRequestScalar.Apply(script!,prepared!,meta)", source, StringComparison.Ordinal);
    }
}
