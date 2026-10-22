extern alias enginehost;
using System.Reflection;
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

[Collection(BundleSelectionCollection.Name)]
public sealed class SensitiveArgumentDigestTests
{
    [Theory]
    [InlineData("14sp1")] [InlineData("15.1")] [InlineData("16")] [InlineData("17")]
    [InlineData("18")] [InlineData("19")] [InlineData("20")] [InlineData("21")]
    public void Every_sensitive_tool_argument_is_excluded_from_approval_and_audit_digests(string release)
    {
        var parameters = enginehost::TiaMcpServer.ModelContextProtocol.PortedToolDeclarations.Methods(release)
            .SelectMany(method => method.GetParameters().Where(p => p.ParameterType == typeof(string) && CallJournalPayload.Sensitive(p.Name!))
                .Select(p => (Tool: method.GetCustomAttribute<ModelContextProtocol.Server.McpServerToolAttribute>()!.Name!, Parameter: p.Name!)))
            .Concat(FoundationTools.Definitions.Select(d => FoundationTools.ForRelease(d, release))
                .SelectMany(d => d.Arguments.Where(a => a.Type == "string" && CallJournalPayload.Sensitive(a.Name)).Select(a => (Tool: d.Name, Parameter: a.Name))))
            .Distinct().ToArray();
        Assert.NotEmpty(parameters);
        var log = new AuditLog(Path.Combine(AppContext.BaseDirectory, "sensitive-digests", Guid.NewGuid().ToString("N")));
        var journal = new List<string>();
        TiaMcpServer.ModelContextProtocol.InvocationJournal.ConfigureOutput(journal.Add);
        try
        {
            foreach (var parameter in parameters)
                foreach (string secret in new[] { "weak-fixture-password", new string('x', 5000) + "private-suffix" })
                {
                    var args = new JsonObject { ["target"] = "reviewed-object", [parameter.Parameter] = secret };
                    string original = args.ToJsonString();
                    var first = PendingApproval.Create("foundation", release, parameter.Tool, original, "{}", 120);
                    args[parameter.Parameter] = "another-password-of-a-different-length";
                    var second = PendingApproval.Create("foundation", release, parameter.Tool, args.ToJsonString(), "{}", 120);
                    Assert.Equal(first.ArgumentDigest, second.ArgumentDigest);
                    Assert.Equal(first.InvocationArgumentsDigest, second.InvocationArgumentsDigest);
                    Assert.Equal(first.PlanHash, second.PlanHash);
                    args["target"] = "changed-object";
                    Assert.NotEqual(first.ArgumentDigest, PendingApproval.Create("foundation", release, parameter.Tool, args.ToJsonString(), "{}", 120).ArgumentDigest);
                    Assert.NotEqual(first.InvocationArgumentsDigest, PendingApproval.Create("foundation", release, parameter.Tool, args.ToJsonString(), "{}", 120).InvocationArgumentsDigest);
                    var result = FoundationV4Result.Reject(release, parameter.Tool, first.RequestId,
                        HostBehavior.Confirmation("denied", first.PlanHash, first.RequestId));
                    string response = JsonSerializer.Serialize(result);
                    log.Append("request", first.RequestId, first.Host, release, first.Tool, planHash: first.PlanHash);
                    log.Approval(first.RequestId, first.Host, release, first.Tool, "denied", first.PlanHash);
                    using (var span = TiaMcpServer.ModelContextProtocol.InvocationJournal.Observe(first.RequestId, first.Tool, first.Host, release, true, () => original))
                        span.Complete(() => response);
                    string evidence = JsonSerializer.Serialize(first) + response + string.Join("\n", journal)
                        + string.Join("\n", log.Read().Select(AuditLog.Canonical));
                    Assert.DoesNotContain(secret, evidence, StringComparison.Ordinal);
                    Assert.DoesNotContain(PendingApproval.Hash(secret), evidence, StringComparison.Ordinal);
                    Assert.DoesNotContain(PendingApproval.Hash(PendingApproval.Canonical(JsonNode.Parse(original))), evidence, StringComparison.Ordinal);
                    Assert.Equal(secret, (string?)JsonNode.Parse(original)![parameter.Parameter]);
                    journal.Clear();
                }
            Assert.True(log.Verify().Passed);
            Assert.Equal(parameters.Length * 4, log.Read().Count);
        }
        finally { TiaMcpServer.ModelContextProtocol.InvocationJournal.ConfigureOutput(null); }
    }

    [Fact]
    public void Nested_maps_embedded_json_and_unbounded_arguments_use_the_journal_sensitive_definition()
    {
        var args = new JsonObject { ["target"] = "secret-a", ["operations"] = new JsonArray(new JsonObject {
            ["credentials"] = new JsonObject { ["user"] = "alice", ["password"] = "secret-a" },
            ["attributes"] = new JsonArray(new JsonObject { ["name"] = "WebPassword", ["value"] = "secret-a" }),
            ["propertiesJson"] = "{\"archivePassword\":\"secret-a\"}" }) };
        JsonObject deep = args;
        for (int i = 0; i < 20; i++) { var next = new JsonObject(); deep["child"] = next; deep = next; }
        deep["password"] = "secret-a"; deep["ordinary"] = new string('n', 70000);
        string json = args.ToJsonString();
        var first = PendingApproval.Create("foundation", "21", "CallTool", json, "{}", 120);
        var changed = JsonNode.Parse(json.Replace("secret-a", "secret-b"))!.AsObject();
        changed["target"] = "secret-a";
        Assert.Equal(first.ArgumentDigest, PendingApproval.Create("foundation", "21", "CallTool", changed.ToJsonString(), "{}", 120).ArgumentDigest);
        deep["ordinary"] = new string('n', 70000) + "changed";
        Assert.NotEqual(first.ArgumentDigest, PendingApproval.Create("foundation", "21", "CallTool", args.ToJsonString(), "{}", 120).ArgumentDigest);
        Assert.Equal("secret-a", (string?)deep["password"]);
        Assert.Equal(json, args.ToJsonString().Replace(new string('n', 70000) + "changed", new string('n', 70000)));
    }
}
