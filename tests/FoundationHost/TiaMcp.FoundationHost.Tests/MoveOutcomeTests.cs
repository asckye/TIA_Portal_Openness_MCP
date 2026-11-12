extern alias enginehost;
using System.Text.Json.Nodes;
using Host = enginehost::TiaMcpServer.ModelContextProtocol;
using Xunit;

public sealed class MoveOutcomeTests
{
    private static JsonObject MoveEvidence(bool restored = true) => new() {
        ["stage"] = "import", ["recoveryExportPath"] = "C:/recovery/original.xml", ["mayHaveChanged"] = true,
        ["deleteStarted"] = true, ["sourceDeleted"] = true, ["nativeMutationIssued"] = true,
        ["restoreAttempted"] = true, ["restored"] = restored, ["originalRetained"] = restored,
        ["recoveryExportVerified"] = true, ["targetGroupsCreated"] = new JsonArray(),
        ["nativeMessage"] = "Error when calling method 'Create' of type 'PlcBlockUserGroupComposition'." };

    private static JsonNode Move(JsonObject evidence)
    {
        var service = new enginehost::TiaMcpServer.Siemens.Services.PlcOrganisationPortService((operation, arguments) =>
            new JsonObject { ["MayHaveChanged"] = true, ["Failure"] = new JsonObject {
                ["Code"] = "IOException", ["Message"] = "Target import failed.", ["Evidence"] = evidence } },
            () => true, () => "C:/fixture/Project.ap21");
        return new Host.PlcOrganisationTools(service)
            .MoveBlockToGroupV4("PLC", "Motor", "Target", true, false, true, new string('a', 64), "C:/fixture/Project.ap21")
            .StructuredContent!;
    }

    private static void Verdict(JsonNode body, string outcome, string execution, string code)
    {
        Assert.Equal(outcome, (string?)body["meta"]?["outcome"]);
        Assert.Equal(execution, (string?)body["meta"]?["execution"]);
        Assert.Equal(code, (string?)body["error"]?["code"]);
    }

    [Fact]
    public void Move_restored_and_verified_reports_failed()
    {
        var body = Move(MoveEvidence());
        Verdict(body, "failed", "completed", "NATIVE_OPERATION_FAILED");
        Assert.Equal("verified", (string?)body["data"]?["evidence"]?["recoveryExportStatus"]);
        Assert.Equal("The move failed; the block was re-imported into its original group and its name, number and language verified. "
            + "Compile before use.", (string?)body["error"]?["message"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Move_restored_after_creating_target_groups_stays_unknown(bool nestedRollback)
    {
        var evidence = MoveEvidence();
        evidence["targetGroupsCreated"] = new JsonArray("New/Sub");
        if (nestedRollback) evidence["rollbackCompleted"] = true;
        var body = Move(evidence);
        Verdict(body, "unknown", "unknown", "OUTCOME_UNKNOWN");
        Assert.Equal("New/Sub", (string?)body["data"]?["evidence"]?["targetGroupsCreated"]?[0]);
    }

    [Theory]
    [InlineData("writeOutcomeUnknown")]
    [InlineData("connectionUnavailable")]
    [InlineData("unrestoredWrite")]
    public void Move_with_unresolved_effects_stays_unknown(string flag)
    {
        var evidence = MoveEvidence();
        evidence[flag] = true;
        evidence["rollbackCompleted"] = true;
        Verdict(Move(evidence), "unknown", "unknown", "OUTCOME_UNKNOWN");
    }

    [Fact]
    public void Move_restored_without_group_creation_evidence_reports_failed()
    {
        var evidence = MoveEvidence();
        evidence.Remove("targetGroupsCreated");
        Verdict(Move(evidence), "failed", "completed", "NATIVE_OPERATION_FAILED");
    }

    [Fact]
    public void Move_restored_with_nested_partial_group_rollback_stays_unknown()
    {
        var evidence = MoveEvidence();
        evidence["rollbackCompleted"] = false;
        evidence["rollbackOutcome"] = "partial";
        evidence["cleanupRequired"] = new JsonArray("/Blocks/New: delete object created by this call if present");
        var body = Move(evidence);
        Verdict(body, "unknown", "unknown", "OUTCOME_UNKNOWN");
        Assert.Single(body["data"]!["evidence"]!["cleanupRequired"]!.AsArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Move_restored_with_independent_nested_rollback_gaps_stays_unknown(bool hasCleanup)
    {
        var evidence = MoveEvidence();
        evidence["rollbackCompleted"] = hasCleanup;
        evidence["cleanupRequired"] = hasCleanup
            ? new JsonArray("/Blocks/New: delete object created by this call if present") : new JsonArray();
        Verdict(Move(evidence), "unknown", "unknown", "OUTCOME_UNKNOWN");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Move_with_failed_restoration_stays_unknown(bool nestedRollback)
    {
        var evidence = MoveEvidence(false);
        evidence["recoveryOnlyCopy"] = true;
        if (nestedRollback) evidence["rollbackCompleted"] = true;
        Verdict(Move(evidence), "unknown", "unknown", "OUTCOME_UNKNOWN");
    }

    private static JsonObject TechnologyEvidence(bool proof) => new() {
        ["tool"] = "ManageTechnologyObject", ["success"] = false, ["mayHaveChanged"] = !proof,
        ["objectPath"] = "Axis_Fixture", ["action"] = "create", ["dryRun"] = false,
        ["typeIdentifier"] = "TO_SpeedAxis", ["version"] = "5.0",
        ["nativeExceptionType"] = "Siemens.Engineering.EngineeringTargetInvocationException",
        ["nativeMessage"] = "TO_SpeedAxis 5.0 does not exist or is not a valid technology object.",
        ["operationSuccess"] = false, ["apiCallSuccess"] = false, ["dataComplete"] = false, ["status"] = "ReadOrWriteFailed" };

    [Fact]
    public void Refused_technology_create_with_journal_proof_reports_failed()
    {
        var evidence = TechnologyEvidence(true);
        evidence["rollbackOutcome"] = "restored";
        evidence["rollbackCompleted"] = true;
        evidence["rollbackAttempted"] = true;
        evidence["unrestoredWrite"] = false;
        evidence["cleanupRequired"] = new JsonArray();
        var body = Host.EngineeringToolContract.Run("ManageTechnologyObject", true,
            () => new Host.ResponseMessage { Meta = evidence }).StructuredContent!;
        Verdict(body, "failed", "completed", "NATIVE_OPERATION_FAILED");
    }

    [Fact]
    public void Refused_technology_create_without_proof_stays_unknown()
    {
        var body = Host.EngineeringToolContract.Run("ManageTechnologyObject", true,
            () => new Host.ResponseMessage { Meta = TechnologyEvidence(false) }).StructuredContent!;
        Verdict(body, "unknown", "unknown", "OUTCOME_UNKNOWN");
    }
}
