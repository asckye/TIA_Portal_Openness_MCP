extern alias toolpipeline;
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
    [InlineData(false)] [InlineData(true)]
    public async Task Download_preview_apply_and_approval_keep_the_reference_even_when_two_entries_hold_the_same_password(bool bridge)
    {
        using var vault = new VaultFileScope();
        string protectedValue = HttpAccessKeyStore.Protect(Secret);
        File.WriteAllText(SecretVault.VaultPath, JsonSerializer.Serialize(new { version = 1,
            secrets = new Dictionary<string, string> { ["plc1"] = protectedValue, ["plc2"] = protectedValue } }));
        using var targets = new OnlineTargetSettingsScope(null);
        using var worker = new OnlinePortAdmissionTests.VaultOnlineWorker();
        using var fixture = new VersionControlAdmissionTests.FileToolFixture("21", worker: worker);
        var cards = new List<PendingApproval>();
        var journal = new List<string>();
        var previous = toolpipeline::TiaMcpServer.ModelContextProtocol.McpServer.ApprovalWaitOverrideForTests;
        toolpipeline::TiaMcpServer.ModelContextProtocol.McpServer.ApprovalWaitOverrideForTests = (card, _, _) => {
            cards.Add(card); return Task.FromResult(new ApprovalOutcome(card, false, null)); };
        // This project links the Foundation journal and references the bridge journal from ToolPipeline.
        TiaMcpServer.ModelContextProtocol.InvocationJournal.ConfigureOutput(journal.Add);
        toolpipeline::TiaMcpServer.ModelContextProtocol.InvocationJournal.ConfigureOutput(journal.Add);
        try
        {
            var arguments = new JsonObject { ["softwarePath"] = "PLC_1", ["targetIpAddress"] = "192.0.2.10",
                ["pgPcInterface"] = "PLCSIM", ["confirmPhysicalTarget"] = true, ["password"] = "vault:plc1", ["dryRun"] = true };
            Task<ModelContextProtocol.Protocol.CallToolResult> Call() => fixture.Call(bridge ? "CallTool" : "DownloadPlc",
                bridge ? new { name = "DownloadPlc", arguments } : (object)arguments);
            var preview = await Call();
            Assert.True((bool?)preview.StructuredContent!["ok"], preview.StructuredContent.ToJsonString());
            string hash = (string)preview.StructuredContent["data"]!["planHash"]!;
            Assert.Equal("vault:plc1", (string?)preview.StructuredContent["data"]!["plannedChanges"]!["password"]);
            arguments["password"] = "vault:plc2";
            var different = await Call();
            Assert.NotEqual(hash, (string?)different.StructuredContent!["data"]!["planHash"]);
            arguments["password"] = "vault:plc1";
            arguments["dryRun"] = false;
            arguments["expectedPlanHash"] = hash;
            arguments["expectedProjectFile"] = "C:/Fixture/P.ap21";
            var applied = await Call();
            Assert.True((bool?)applied.StructuredContent!["ok"], applied.StructuredContent.ToJsonString());
            Assert.Equal(hash, (string?)applied.StructuredContent["data"]!["planHash"]);
            Assert.Equal(Secret, worker.Password);
            Assert.Single(cards.Select(card => card.ArgumentDigest).Distinct());
            Assert.All(cards, card => Assert.Contains("vault:plc1", card.ParametersJson));
            Assert.Contains("vault:plc1", string.Join("\n", journal));
            Assert.DoesNotContain(Secret, string.Join("\n", journal) + JsonSerializer.Serialize(cards) + JsonSerializer.Serialize(applied));
        }
        finally
        {
            TiaMcpServer.ModelContextProtocol.InvocationJournal.ConfigureOutput(null);
            toolpipeline::TiaMcpServer.ModelContextProtocol.InvocationJournal.ConfigureOutput(null);
            toolpipeline::TiaMcpServer.ModelContextProtocol.McpServer.ApprovalWaitOverrideForTests = previous;
        }
    }

    [Fact]
    public async Task Parked_download_reply_redacts_the_resolved_credential()
    {
        using var vault = new VaultFileScope();
        vault.Vault.Set("plc1", Secret, vault.Audit);
        using var targets = new OnlineTargetSettingsScope(null);
        const string marker = "native fixture detail";
        using var worker = new OnlinePortAdmissionTests.VaultOnlineWorker(diagnostic: marker + new string('x', 24000));
        using var fixture = new VersionControlAdmissionTests.FileToolFixture("21", worker: worker,
            approvalWait: (pending, _, _) => Task.FromResult(new ApprovalOutcome(pending, false, null)));
        var args = new JsonObject { ["softwarePath"] = "PLC_1", ["targetIpAddress"] = "192.0.2.10",
            ["pgPcInterface"] = "PLCSIM", ["confirmPhysicalTarget"] = true, ["password"] = "vault:plc1", ["dryRun"] = true };
        var preview = (await fixture.Call("DownloadPlc", args)).StructuredContent!;
        args["expectedPlanHash"] = preview["data"]!["planHash"]!.DeepClone();
        args["expectedProjectFile"] = "C:/Fixture/P.ap21";
        args["dryRun"] = false;
        var applied = (await fixture.Call("DownloadPlc", args)).StructuredContent!;
        Assert.True((bool?)applied["ok"], applied.ToJsonString());
        Assert.Equal(Secret, worker.Password);
        using var session = fixture.EnterSession();
        var handle = (string)applied["data"]!["export"]!["id"]!;
        var payload = toolpipeline::TiaMcp.FoundationHost.SessionExportStore.Get(handle)!.Content;
        Assert.Contains(marker, payload);
        Assert.DoesNotContain(Secret, payload);
    }

    [Fact]
    public void Vault_references_stay_readable_in_approval_journal_audit_and_response_and_bind_the_plan_hash()
    {
        using var vault = new VaultFileScope();
        vault.Vault.Set("plc1", Secret, vault.Audit);
        var args = new JsonObject { ["softwarePath"] = "PLC_1", ["password"] = "vault:plc1", ["umacPassword"] = Secret };
        var pending = PendingApproval.Create("foundation", "21", "DownloadPlc", args.ToJsonString(), "{}", 120);
        Assert.Contains("vault:plc1", pending.ParametersJson);
        Assert.Contains("••••", JsonNode.Parse(pending.ParametersJson)!.ToJsonString(new JsonSerializerOptions {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        Assert.DoesNotContain(Secret, pending.ParametersJson);
        var apply = PendingApproval.Create("foundation", "21", "DownloadPlc", args.ToJsonString(), "{}", 120);
        Assert.Equal(pending.PlanHash, apply.PlanHash);
        args["password"] = "vault:plc2";
        Assert.NotEqual(apply.PlanHash, PendingApproval.Create("foundation", "21", "DownloadPlc", args.ToJsonString(), "{}", 120).PlanHash);
        var response = FoundationV4Result.Reject("21", "DownloadPlc", pending.RequestId,
            new Error("Review vault:plc1.", new ConfirmationRequiredDetails("denied", pending.PlanHash, pending.RequestId)));
        var journal = new List<string>();
        TiaMcpServer.ModelContextProtocol.InvocationJournal.ConfigureOutput(journal.Add);
        try
        {
            using (var span = TiaMcpServer.ModelContextProtocol.InvocationJournal.Observe(pending.RequestId, "DownloadPlc", "foundation", "21",
                true, () => pending.ParametersJson)) span.Complete(() => JsonSerializer.Serialize(response));
        }
        finally { TiaMcpServer.ModelContextProtocol.InvocationJournal.ConfigureOutput(null); }
        vault.Audit.Append("request", pending.RequestId, "foundation", "21", "DownloadPlc", planHash: pending.PlanHash);
        string evidence = string.Join("\n", journal) + JsonSerializer.Serialize(response)
            + string.Join("\n", vault.Audit.Read().Select(AuditLog.Canonical));
        Assert.Contains("vault:plc1", evidence);
        Assert.DoesNotContain(Secret, evidence);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Foundation_credential_references_are_admitted_before_any_approval_or_worker_call(bool bridge)
    {
        using var vault = new VaultFileScope();
        int approvals = 0;
        using var fixture = new VersionControlAdmissionTests.FileToolFixture("21", approvalWait: (card, _, _) => {
            approvals++; return Task.FromResult(new ApprovalOutcome(card, false, "denied")); });
        var arguments = new { path = "C:/Fixture/P.ap21", umacPassword = "vault:missing" };
        var result = await fixture.Call(bridge ? "CallTool" : "OpenProject",
            bridge ? new { name = "OpenProject", arguments } : (object)arguments);
        Assert.Equal("INVALID_ARGUMENT", (string?)result.StructuredContent!["error"]?["code"]);
        Assert.EndsWith("/umacPassword", (string?)result.StructuredContent["error"]?["details"]?["parameter"]);
        Assert.Contains("vault:missing", (string?)result.StructuredContent["error"]?["message"]);
        Assert.Equal(0, approvals);
        Assert.Equal(0, fixture.Worker.Calls);
    }

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
        var parameters = toolpipeline::TiaMcpServer.ModelContextProtocol.PortedToolDeclarations.Methods(release)
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
