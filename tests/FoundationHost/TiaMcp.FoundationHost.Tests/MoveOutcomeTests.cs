extern alias toolpipeline;
extern alias adaptertests;
extern alias plcpathfixture;
using System.Text.Json.Nodes;
using TiaMcp.Adapters.Contracts;
using TiaMcp.FoundationHost;
using TiaMcp.PlcWorker;
using TiaMcp.WorkerChannel;
using PathEngine = plcpathfixture::TiaMcp.Adapters.PlcFoundationEngine;
using PathBlock = plcpathfixture::Siemens.Engineering.SW.Blocks.PlcBlock;
using Host = toolpipeline::TiaMcpServer.ModelContextProtocol;
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
        var service = new toolpipeline::TiaMcpServer.Siemens.Services.PlcOrganisationPortService((operation, arguments) =>
            new JsonObject { ["MayHaveChanged"] = true, ["Failure"] = new JsonObject {
                ["Code"] = "IOException", ["Message"] = "Target import failed.", ["Evidence"] = evidence } },
            () => true, () => "C:/fixture/Project.ap21");
        return new Host.PlcOrganisationTools(service)
            .MoveBlockToGroupV4("PLC", "Motor", "Target", true, false, true, new string('a', 64), "C:/fixture/Project.ap21")
            .StructuredContent!;
    }

    [Theory, MemberData(nameof(VerifiedImportOutcomeTests.Releases), MemberType = typeof(VerifiedImportOutcomeTests))]
    public void Refused_group_creation_and_verified_source_restore_reach_the_port(string release)
    {
        foreach (bool refuseGroup in new[] { true, false })
        {
            var engine = new PathEngine(release);
            var root = engine.Plc.BlockGroup;
            root.Name = "Blocks";
            root.Blocks.Add(new PathBlock { Name = "FC_A", Parent = root });
            var calls = new List<string>();
            root.OnCall = call => {
                calls.Add(call);
                if (call == (refuseGroup ? "create Sub" : "import Sub")) throw new IOException("Native refusal.");
            };
            string hash = (string)engine.MovePlcBlockToGroup("FC_A", "G/Sub").Meta["planHash"]!;
            string recoveryFile = Path.Combine(AppContext.BaseDirectory, Guid.NewGuid() + ".xml");
            try
            {
                var state = new WorkerSessionOutcomeState();
                var recovery = new WorkerSessionRecovery(state, () => false, () => { }, () => { }, () => { }, () => { });
                var service = new toolpipeline::TiaMcpServer.Siemens.Services.PlcOrganisationPortService((operation, args) => {
                    var failure = Assert.ThrowsAny<Exception>(() => engine.ApplyMove("FC_A", "G/Sub", recoveryFile, hash));
                    var reply = new PlcSoftwareReply { Failure = new PlcSoftwareFailure { Code = "OpennessError", Message = failure.Message } };
                    var evidence = reply.Failure.Evidence;
                    PlcMovePolicy.MergeEvidence(failure, evidence);
                    evidence["nativeMessage"] = failure.GetBaseException().Message;
                    reply.MayHaveChanged = (bool)evidence["mayHaveChanged"]!;
                    reply.OutcomeUnknown = reply.MayHaveChanged && !PlcMovePolicy.SettledByRestore(evidence);
                    var request = new ChannelRequest(1, "adapter." + operation, args.ToJsonString(), (_, _) => { });
                    var response = recovery.Synchronize(request, recovery.Dispatch(request, _ => WorkerOperationResponse.Complete(reply, false, state)));
                    if (response.Failure is { } channelFailure) throw WorkerOperationException.FromChannelFailure(channelFailure);
                    return JsonNode.Parse(response.ResultJson);
                }, () => true, () => "C:/fixture/Project.ap" + release);
                var body = new Host.PlcOrganisationTools(service)
                    .MoveBlockToGroupV4("PLC", "FC_A", "G/Sub", true, false, true, hash, "C:/fixture/Project.ap" + release).StructuredContent!;
                Verdict(body, refuseGroup ? "rejected-before-operation" : "failed", refuseGroup ? "not-started" : "completed",
                    refuseGroup ? "PRECONDITION_FAILED" : "NATIVE_OPERATION_FAILED");
                Assert.False(state.Uncertain);
                var proof = body["data"]!["evidence"]!;
                Assert.Equal(!refuseGroup, (bool?)proof["deleteStarted"]);
                Assert.Equal(!refuseGroup, (bool?)proof["mayHaveChanged"]);
                if (!refuseGroup) Assert.True((bool?)proof["restored"]);
                Assert.True((bool?)proof["rollbackCompleted"]);
                Assert.Equal(refuseGroup ? new[] { "create G", "create Sub", "delete group G" }
                    : new[] { "create G", "create Sub", "export FC_A", "delete FC_A", "import Sub", "import Blocks", "delete group Sub", "delete group G" },
                    calls);
                Assert.Empty(root.Groups);
                Assert.Equal("FC_A", Assert.Single(root.Blocks).Name);
                Assert.Equal(!refuseGroup, File.Exists(recoveryFile));
            }
            finally { File.Delete(recoveryFile); }
        }
    }

    private static void Verdict(JsonNode body, string outcome, string execution, string code)
    {
        Assert.Equal(outcome, (string?)body["meta"]?["outcome"]);
        Assert.Equal(execution, (string?)body["meta"]?["execution"]);
        Assert.Equal(code, (string?)body["error"]?["code"]);
    }

    [Theory, InlineData("verify"), InlineData("import"), InlineData("backup")]
    public void Failed_move_retains_nonempty_target_groups_and_reports_cleanup(string stage)
    {
        var engine = new PathEngine("21");
        var root = engine.Plc.BlockGroup;
        root.Name = "Blocks";
        root.Blocks.Add(new PathBlock { Name = "FC_A", Parent = root });
        var calls = new List<string>();
        root.AfterImport = block => {
            if (stage == "verify") block.Name = "FC_B"; // mismatch on destination readback
            if (stage == "import") throw new IOException("Import failed after insertion.");
        };
        root.OnCall = call => {
            calls.Add(call);
            if (stage == "verify" && call == "delete FC_B") throw new IOException("Target block deletion refused.");
            if (stage == "import" && call == "import Blocks") throw new IOException("Source restore refused.");
            if (stage == "backup" && call == "export FC_A")
            {
                var sub = root.Groups.Single().Groups.Single();
                sub.Blocks.Add(new PathBlock { Name = "FC_B", Parent = sub });
                throw new IOException("Source export failed.");
            }
        };
        string hash = (string)engine.MovePlcBlockToGroup("FC_A", "G/Sub").Meta["planHash"]!;
        string file = Path.Combine(AppContext.BaseDirectory, Guid.NewGuid() + ".xml");
        try
        {
            var failure = Assert.ThrowsAny<Exception>(() => engine.ApplyMove("FC_A", "G/Sub", file, hash));
            var reply = new PlcSoftwareReply { Failure = new PlcSoftwareFailure { Code = "OpennessError", Message = failure.Message } };
            PlcMovePolicy.MergeEvidence(failure, reply.Failure.Evidence);
            Assert.False(PlcMovePolicy.SettledByRestore(reply.Failure.Evidence));
            reply.MayHaveChanged = (bool)reply.Failure.Evidence["mayHaveChanged"]!;
            reply.OutcomeUnknown = reply.MayHaveChanged && !PlcMovePolicy.SettledByRestore(reply.Failure.Evidence);
            var state = new WorkerSessionOutcomeState();
            var recovery = new WorkerSessionRecovery(state, () => false, () => { }, () => { }, () => { }, () => { });
            var service = new toolpipeline::TiaMcpServer.Siemens.Services.PlcOrganisationPortService((operation, args) => {
                var request = new ChannelRequest(1, "adapter." + operation, args.ToJsonString(), (_, _) => { });
                var response = recovery.Synchronize(request, recovery.Dispatch(request, _ => WorkerOperationResponse.Complete(reply, false, state)));
                throw WorkerOperationException.FromChannelFailure(response.Failure!);
            }, () => true, () => "C:/fixture/Project.ap21");
            var body = new Host.PlcOrganisationTools(service)
                .MoveBlockToGroupV4("PLC", "FC_A", "G/Sub", true, false, true, hash, "C:/fixture/Project.ap21").StructuredContent!;
            Verdict(body, "unknown", "unknown", "OUTCOME_UNKNOWN");
            var evidence = body["data"]!["evidence"]!;
            Assert.Equal(stage, (string?)evidence["stage"]);
            Assert.Equal(stage != "backup", (bool?)evidence["deleteStarted"]);
            Assert.True((bool?)evidence["nativeMutationIssued"]);
            Assert.Equal(new[] { "G", "G/Sub" }, evidence["targetGroupsCreated"]!.AsArray().Select(path => (string?)path));
            Assert.NotEmpty(evidence["cleanupRequired"]!.AsArray());
            Assert.False((bool?)evidence["rollbackCompleted"]);
            var target = Assert.Single(Assert.Single(root.Groups).Groups);
            Assert.Equal(stage == "import" ? "FC_A" : "FC_B", Assert.Single(target.Blocks).Name);
            Assert.DoesNotContain(calls, call => call.StartsWith("delete group", StringComparison.Ordinal));
            if (stage == "backup") Assert.Equal("FC_A", Assert.Single(root.Blocks).Name);
            else Assert.Empty(root.Blocks);
            if (stage == "verify")
            {
                Assert.Equal("failed", (string?)evidence["targetCleanup"]);
                Assert.False((bool?)evidence["restoreAttempted"]);
                Assert.Contains("delete FC_B", calls);
            }
            if (stage == "import") Assert.Contains("import Blocks", calls);
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void Refused_nested_group_creation_reports_the_nonempty_parent_left_in_the_project()
    {
        var engine = new PathEngine("21");
        var root = engine.Plc.BlockGroup;
        root.Blocks.Add(new PathBlock { Name = "FC_A", Parent = root });
        root.OnCall = call => {
            if (call != "create Sub") return;
            var group = root.Groups.Single();
            group.Blocks.Add(new PathBlock { Name = "FC_B", Parent = group });
            throw new IOException("Native group creation refused.");
        };
        string hash = (string)engine.MovePlcBlockToGroup("FC_A", "G/Sub").Meta["planHash"]!;
        string file = Path.Combine(AppContext.BaseDirectory, Guid.NewGuid() + ".xml");
        try
        {
            var failure = Assert.ThrowsAny<Exception>(() => engine.ApplyMove("FC_A", "G/Sub", file, hash));
            var evidence = new Dictionary<string, object?>();
            PlcMovePolicy.MergeEvidence(failure, evidence);
            Assert.Equal(new[] { "G" }, Assert.IsType<object?[]>(evidence["targetGroupsCreated"]));
            Assert.Equal(false, evidence["deleteStarted"]);
            Assert.Equal(true, evidence["nativeMutationIssued"]);
            Assert.Equal(false, evidence["rollbackCompleted"]);
            Assert.NotEmpty(Assert.IsAssignableFrom<IEnumerable<object?>>(evidence["cleanupRequired"]));
            Assert.False(File.Exists(file));
            Assert.Equal("FC_A", Assert.Single(root.Blocks).Name);
            Assert.Equal("FC_B", Assert.Single(Assert.Single(root.Groups).Blocks).Name);
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void Watch_import_with_verified_absence_keeps_native_failure_through_session_recovery()
    {
        var state = new WorkerSessionOutcomeState();
        var recovery = new WorkerSessionRecovery(state, () => false, () => { }, () => { }, () => { }, () => { });
        var request = new ChannelRequest(1, "adapter.plc-software.ExecutePlcOrganisation", """{"dryRun":false}""", (_, _) => { });
        var response = recovery.Dispatch(request, _ => {
            var step = adaptertests::TiaMcp.Adapters.Contracts.Tests.WriteRollbackProductionTests.RefusedWatchImport();
            return WorkerOperationResponse.Complete(new PlcSoftwareReply { Meta = step.Meta, Message = step.Message,
                OutcomeUnknown = step.OutcomeUnknown, MayHaveChanged = step.MayHaveChanged }, false, state);
        });
        response = recovery.Synchronize(request, response);
        Assert.Null(response.Failure);
        Assert.False(state.Uncertain);
        var body = Host.PlcToolContract.Map("ImportPlcWatchTableOffline", JsonNode.Parse(response.ResultJson)!, true, true).StructuredContent!;
        Verdict(body, "failed", "completed", "NATIVE_OPERATION_FAILED");
        Assert.Equal("Watch table import refused.", (string?)body["error"]?["details"]?["nativeMessage"]);
        Assert.False((bool?)body["data"]?["evidence"]?["mayHaveChanged"]);
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
