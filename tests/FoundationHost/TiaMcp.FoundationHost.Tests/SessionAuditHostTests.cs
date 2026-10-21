using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Adapters;
using TiaMcp.Adapters.Contracts;
using TiaMcp.FoundationHost;
using TiaMcp.Logic.V4;
using TiaMcp.WorkerChannel;
using TiaOpenness.Shared;
using Xunit;

// P8-81c (API audit 2026-10-19): owned-portal disconnect (F1), UMAC credentials (F2) and explicit refusal codes (F4).
public sealed class SessionAuditHostTests
{
    private const string Secret = "Umac-Secret-7f3!";

    [Theory]
    [InlineData("14sp1", false)] [InlineData("15.1", true)] [InlineData("16", true)] [InlineData("17", true)]
    [InlineData("18", true)] [InlineData("19", true)] [InlineData("20", true)] [InlineData("21", true)]
    public void OpenProject_offers_umac_credentials_exactly_where_the_SDK_has_UmacDelegate(string release, bool umac)
    {
        var open = FoundationTools.ForRelease(FoundationTools.Definitions.Single(d => d.Name == "OpenProject"), release);
        var names = open.Arguments.Select(a => a.Name).ToArray();
        Assert.Equal(umac, names.Contains("umacUserName"));
        Assert.Equal(umac, names.Contains("umacPassword"));
        Assert.Equal(umac, names.Contains("umacUserType"));
        Assert.Equal(umac, open.Description.Contains("umacUserName and umacPassword", StringComparison.Ordinal));
        Assert.Contains("Refused while the TIA instance already has an open project or local session.", open.Description, StringComparison.Ordinal);
        foreach (var argument in open.Arguments.Where(a => a.Name.StartsWith("umac", StringComparison.Ordinal)))
        { Assert.False(argument.Required); Assert.Equal("", argument.Default); Assert.NotNull(argument.Description); }
        Assert.Contains("never logged, journaled, echoed or shown on the approval card",
            FoundationTools.UmacArguments.Single(a => a.Name == "umacPassword").Description, StringComparison.Ordinal);
        Assert.Same(FoundationTools.Definitions.Single(d => d.Name == "CreateProject"),
            FoundationTools.ForRelease(FoundationTools.Definitions.Single(d => d.Name == "CreateProject"), release));
    }

    [Theory]
    [InlineData("OpenProject")] [InlineData("RetrieveProjectArchive")]
    public void Umac_password_never_reaches_the_journal_the_approval_card_or_the_audit_identity(string tool)
    {
        var arguments = new JsonObject { ["path"] = "C:/Projects/P/P.ap21", ["archivePath"] = "C:/Archives/P.zap21", ["destinationDirectory"] = "C:/Retrieved/P",
            ["dryRun"] = false, ["umacUserName"] = "engineer", ["umacPassword"] = Secret, ["umacUserType"] = "Project" };
        string json = arguments.ToJsonString();
        Assert.DoesNotContain(Secret, CallJournalPayload.Redact(json), StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, CallJournalPayload.Sanitize(json), StringComparison.Ordinal);
        var pending = PendingApproval.Create("foundation", "21", tool, json, "{\"ProjectFile\":\"C:/Projects/P/P.ap21\"}", 120);
        Assert.DoesNotContain(Secret, pending.ParametersJson, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, pending.ProjectIdentity, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, string.Join("|", pending.Operations.Select(o => o.Tool + o.Action + o.Target)), StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, JsonSerializer.Serialize(pending), StringComparison.Ordinal);
        // The user name is not secret; the approver still sees which account is used.
        Assert.Contains("engineer", pending.ParametersJson, StringComparison.Ordinal);
        // A refusal message for the given credentials never repeats them.
        Assert.DoesNotContain(Secret, OpennessRefusals.Message(OpennessRefusals.AccessDenied, "open", true, "denied"), StringComparison.Ordinal);
    }

    [Fact]
    public void Worker_started_portal_is_disposed_once_and_refused_while_a_project_is_open()
    {
        var state = new PlcDisconnectState(); int disposes = 0;
        var refusal = Assert.Throws<AdapterPreconditionException>(() => state.Execute(4321, true, () => disposes++, false, workerStarted: true, ownedProjectOpen: true));
        Assert.False(refusal.IsArgument);
        Assert.Contains("SaveProject", refusal.Message, StringComparison.Ordinal);
        Assert.False(state.Attempted); Assert.Equal(0, disposes);
        state.RequireActive();
        var result = state.Execute(4321, true, () => disposes++, false, workerStarted: true);
        Assert.True(result.Detached); Assert.Equal(4321, result.ProcessId);
        Assert.Equal("worker-started-portal-dispose", result.Strategy); Assert.Equal("worker-started", result.LaunchMode);
        Assert.Same(result, state.Execute(4321, true, () => disposes++, false, workerStarted: true));
        Assert.Equal(1, disposes);
        Assert.Throws<InvalidOperationException>(state.RequireActive);
        var acknowledgement = DisconnectContract.Validate(JsonSerializer.SerializeToNode(result), true, 4321, true);
        Assert.Equal("worker-started-portal-dispose", (string?)acknowledgement["Strategy"]);
        // Owned portals that this worker did not start stay blocked, and other strategy/launch pairs stay malformed.
        Assert.False(PlcDisconnectState.Supports(true, false));
        Assert.True(PlcDisconnectState.Supports(true, false, workerStarted: true));
        Assert.Throws<NotSupportedException>(() => new PlcDisconnectState().Execute(4321, true, () => disposes++));
        var mixed = JsonSerializer.SerializeToNode(result)!.AsObject(); mixed["LaunchMode"] = "never";
        Assert.Throws<IOException>(() => DisconnectContract.Validate(mixed, true, 4321, true));
        var shared = JsonSerializer.SerializeToNode(result)!.AsObject(); shared["Strategy"] = "owned-shared-portal-dispose"; shared["LaunchMode"] = "engine-started";
        Assert.Throws<IOException>(() => DisconnectContract.Validate(shared, true, 4321, true));
        Assert.Equal(1, disposes);
    }

    [Fact]
    public void A_typed_disconnect_refusal_keeps_the_host_session_and_other_failures_do_not()
    {
        string evidence = JsonSerializer.Serialize(new { exceptionType = nameof(AdapterPreconditionException), parameter = (string?)null, isArgument = false });
        var refused = new ChannelFailureException(PlcDisconnectState.OwnedOpenProjectRefusal, -32603, ChannelOutcome.RejectedBeforeNative, evidence);
        Assert.True(DisconnectContract.Refused(refused));
        var precondition = DisconnectContract.Refusal(refused);
        Assert.False(precondition.IsArgument);
        Assert.Equal(HostFailureKind.Precondition, HostFailurePolicy.Classify(precondition, true, false));
        Assert.False(DisconnectContract.Refused(new ChannelFailureException("lost", -32603, ChannelOutcome.Unknown, evidence)));
        Assert.False(DisconnectContract.Refused(new ChannelFailureException("other", -32603, ChannelOutcome.RejectedBeforeNative,
            JsonSerializer.Serialize(new { exceptionType = nameof(InvalidOperationException) }))));
    }

    [Theory]
    [InlineData("openness-access-denied")] [InlineData("missing-products")] [InlineData("license-not-found")] [InlineData("multiuser-refused")]
    public void Documented_refusals_are_precondition_failures_with_their_condition(string condition)
    {
        var refusal = OpennessRefusals.Exception(condition, "open", false, new InvalidOperationException("TIA"));
        Assert.Equal(HostFailureKind.Precondition, HostFailurePolicy.Classify(refusal, true, false));
        Assert.Equal(condition, HostFailurePolicy.Precondition(refusal)!.Condition);
        string evidence = JsonSerializer.Serialize(new { exceptionType = nameof(AdapterPreconditionException), parameter = (string?)null, isArgument = false, condition });
        var reported = new WorkerOperationException(refusal.Message, -32603, "rejected-before-operation", evidence);
        Assert.Equal(condition, reported.Condition);
        Assert.True(reported.KnownNoMutation);
        var error = HostBehavior.FailureError(HostFailureKind.Precondition, null, new Dictionary<string, JsonElement>(), refusal.Message, condition: condition);
        var details = Assert.IsType<PreconditionFailedDetails>(error.Details);
        Assert.Equal(condition, details.Condition);
        Assert.Equal("native-admission", Assert.IsType<PreconditionFailedDetails>(
            HostBehavior.FailureError(HostFailureKind.Precondition, null, new Dictionary<string, JsonElement>()).Details).Condition);
    }
}
