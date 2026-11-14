extern alias enginehost;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.FoundationHost;
using TiaMcp.Logic.V4;
using Host = enginehost::TiaMcpServer.ModelContextProtocol;
using Xunit;

public sealed class NativeFailureDiagnosticsTests
{
    public static IEnumerable<object[]> Releases() => new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" }
        .Select(release => new object[] { release });
    public static IEnumerable<object[]> ReleasesAndRoutes() => Releases()
        .SelectMany(row => new[] { new object[] { row[0], false }, new object[] { row[0], true } });

    public static IEnumerable<object[]> DeviceUserReleases() => Releases()
        .Where(row => TiaMcp.Adapters.Contracts.PortedFamilies.Available((string)row[0], "ManageDeviceUsers"));

    public static IEnumerable<object[]> PasswordPolicyReleases() => Releases()
        .Where(row => TiaMcp.Adapters.Contracts.PortedFamilies.Available((string)row[0], "ManagePasswordPolicy"));

    [Theory, MemberData(nameof(Releases))]
    public async Task Wrapped_ported_refusal_after_an_earlier_mutation_suppresses_worker_diagnostics(string release)
    {
        using var fixture = new Fixture(release);
        int writes = 0;
        fixture.Worker.Handle = (operation, args) => {
            Assert.Equal("project-lifecycle.ExecuteProjectScaffoldPrimitive", operation);
            if ((string?)args["request"]?["Kind"] == "createProject")
            {
                writes++;
                return new JsonObject { ["Message"] = "Created.", ["Meta"] = new JsonObject { ["success"] = true } };
            }
            return new JsonObject { ["Ok"] = false, ["MayHaveChanged"] = false, ["Failure"] = new JsonObject {
                ["Code"] = "ArgumentException", ["Message"] = "Refused scaffold-fixture-secret after prior write.",
                ["Evidence"] = new JsonObject { ["exceptionType"] = "ArgumentException", ["parameter"] = "password", ["isArgument"] = true,
                    ["nativeMessage"] = "Refused scaffold-fixture-secret after prior write." } } };
        };
        var tool = fixture.Tools.Single(t => t.ProtocolTool.Name == "BuildProjectScaffold");
        var spec = new JsonObject { ["projectName"] = "Fixture", ["directoryPath"] = AppContext.BaseDirectory,
            ["plcMlfb"] = "6ES7 516-3AN02-0AB0", ["plcVersion"] = "V2.9", ["compile"] = false, ["save"] = false,
            ["password"] = "scaffold-fixture-secret" };
        JsonNode input = tool.ProtocolTool.InputSchema.GetProperty("properties").GetProperty("spec").GetProperty("type").GetString() == "string"
            ? JsonValue.Create(spec.ToJsonString())! : spec;
        var result = await fixture.Call("BuildProjectScaffold", new JsonObject { ["spec"] = input, ["dryRun"] = false });
        Assert.Equal(1, writes);
        Assert.Equal("OUTCOME_UNKNOWN", (string?)result.StructuredContent?["error"]?["code"]);
        Assert.True((bool?)result.StructuredContent?["data"]?["earlierMutationMayHaveChanged"]);
        Assert.NotNull(result.StructuredContent?["data"]?["evidence"]);
        Assert.Null(result.StructuredContent?["data"]?["evidence"]?["workerMessage"]);
        Assert.Null(result.StructuredContent?["error"]?["innererror"]);
        Assert.DoesNotContain("scaffold-fixture-secret", result.StructuredContent!.ToJsonString());
        Assert.All(result.Content.OfType<TextContentBlock>(), text => Assert.DoesNotContain("scaffold-fixture-secret", text.Text));
    }

    [Theory, MemberData(nameof(DeviceUserReleases))]
    public async Task Wrapped_ported_native_failure_masks_the_current_password_after_a_prior_write(string release)
    {
        using var fixture = new Fixture(release);
        fixture.Worker.Reply = new JsonObject { ["Message"] = "Written.", ["Meta"] = new JsonObject {
            ["operationSuccess"] = true, ["apiCallSuccess"] = true, ["mayHaveChanged"] = true } };
        var args = new JsonObject { ["devicePath"] = new JsonArray("PLC"), ["itemPath"] = new JsonArray(), ["family"] = "webserver",
            ["action"] = "setPassword", ["userName"] = "fixture", ["password"] = "old-fixture-secret", ["dryRun"] = false };
        var written = await fixture.Call("ManageDeviceUsers", args);
        Assert.True((bool?)written.StructuredContent?["ok"], written.StructuredContent?.ToJsonString());
        args["action"] = "read";
        args["password"] = "current-fixture-secret";
        fixture.Worker.Failure = () => new WorkerOperationException("Rejected current-fixture-secret. PasswordValidity: 9\n   at private.stack",
            -32603, "read-failed", """{"exceptionType":"EngineeringException","hResult":-123}""");
        var result = await fixture.Call("ManageDeviceUsers", args);
        var body = result.StructuredContent!;
        Assert.Equal("NATIVE_OPERATION_FAILED", (string?)body["error"]?["code"]);
        Assert.NotNull(body["error"]?["innererror"]);
        Assert.NotNull(body["error"]?["details"]?["evidence"]);
        Assert.Contains("PasswordValidity: 9", (string?)body["error"]?["details"]?["nativeMessage"]);
        Assert.DoesNotContain("current-fixture-secret", body.ToJsonString());
        Assert.DoesNotContain("private.stack", body.ToJsonString());
        Assert.All(result.Content.OfType<TextContentBlock>(), text => Assert.DoesNotContain("current-fixture-secret", text.Text));
    }

    [Theory, MemberData(nameof(PasswordPolicyReleases))]
    public async Task Numeric_password_policy_arguments_keep_the_native_reason(string release)
    {
        using var fixture = new Fixture(release);
        fixture.Worker.Reply = new JsonObject { ["Message"] = "PasswordValidity: 9 out of range.", ["Meta"] = new JsonObject {
            ["operationSuccess"] = false, ["apiCallSuccess"] = false, ["mayHaveChanged"] = false,
            ["status"] = "ReadOrWriteFailed", ["nativeMessage"] = "PasswordValidity: 9 out of range.",
            ["nativeExceptionType"] = "PasswordPolicySettingsException" } };
        var result = await fixture.Call("ManagePasswordPolicy", new JsonObject { ["action"] = "update", ["target"] = "umac",
            ["properties"] = new JsonObject { ["PasswordValidity"] = 9 }, ["confirmChange"] = true, ["dryRun"] = false });
        Assert.Equal("NATIVE_OPERATION_FAILED", (string?)result.StructuredContent?["error"]?["code"]);
        Assert.Equal("PasswordValidity: 9 out of range.", (string?)result.StructuredContent?["error"]?["details"]?["nativeMessage"]);
    }

    [Theory, MemberData(nameof(ReleasesAndRoutes))]
    public async Task Worker_reason_and_correlation_are_available_through_the_registered_tools(string release, bool wrapped)
    {
        using var fixture = new Fixture(release, wrapped);
        fixture.Worker.Failure = () => new WorkerOperationException("Native address read failed.\n   at private.stack", -32603, "read-failed",
            """{"exceptionType":"EngineeringTargetInvocationException","hResult":-2146233088,"parameter":null,"isArgument":null}""");
        var result = await fixture.Call("GetDeviceAddressing", new JsonObject { ["devicePath"] = new JsonArray("PLC") });
        var body = result.StructuredContent!;
        Assert.Equal("NATIVE_OPERATION_FAILED", (string?)body["error"]?["code"]);
        Assert.Equal("Native address read failed.", (string?)body["error"]?["details"]?["nativeMessage"]);
        Assert.Equal("EngineeringTargetInvocationException", (string?)body["error"]?["innererror"]?["exceptionType"]);
        Assert.Equal(-2146233088, (int?)body["error"]?["innererror"]?["hResult"]);
        string id = (string)body["meta"]!["requestId"]!;
        Assert.Equal(id, (string?)body["error"]?["innererror"]?["diagnosticId"]);
        Assert.DoesNotContain("private.stack", body.ToJsonString());
        Assert.Equal(body.ToJsonString(), ((TextContentBlock)Assert.Single(result.Content)).Text);
        var found = Page(await fixture.Call("GetNativeInvocationLog", new JsonObject { ["diagnosticId"] = id }));
        Assert.NotEmpty(found["records"]!.AsArray());
        Assert.All(found["records"]!.AsArray(), row => Assert.Equal(id, (string?)row?["id"]));
    }

    [Theory, MemberData(nameof(Releases))]
    public async Task Worker_without_exception_evidence_keeps_null_native_type_and_hresult(string release)
    {
        using var fixture = new Fixture(release);
        fixture.Worker.Failure = () => new WorkerOperationException("Worker reported the native read failed.", -32603, "read-failed");
        var body = (await fixture.Call("GetDeviceAddressing", new JsonObject { ["devicePath"] = new JsonArray("PLC") })).StructuredContent!;
        Assert.Equal("Worker reported the native read failed.", (string?)body["error"]?["details"]?["nativeMessage"]);
        Assert.Null(body["error"]?["innererror"]?["exceptionType"]);
        Assert.Null(body["error"]?["innererror"]?["hResult"]);
    }

    [Theory, MemberData(nameof(Releases))]
    public async Task Production_worker_evidence_preserves_the_unwrapped_hresult_through_the_registered_tool(string release)
    {
        using var fixture = new Fixture(release);
        var native = new IOException("Native worker read failed.");
        fixture.Worker.Failure = () => {
            Exception cause = new TargetInvocationException(native);
            var classified = WorkerFailurePolicy.Classify(cause, true, true);
            var failure = new TiaMcp.WorkerChannel.ChannelFailureException(
                WorkerFailurePolicy.DiagnosticCause(cause).Message, classified.Code, classified.Outcome,
                TiaMcp.PlcWorker.WorkerJson.Evidence(cause));
            return WorkerOperationException.FromChannelFailure(failure);
        };
        var body = (await fixture.Call("GetDeviceAddressing", new JsonObject { ["devicePath"] = new JsonArray("PLC") })).StructuredContent!;
        Assert.Equal("NATIVE_OPERATION_FAILED", (string?)body["error"]?["code"]);
        Assert.Equal(nameof(IOException), (string?)body["error"]?["innererror"]?["exceptionType"]);
        Assert.Equal(native.HResult, (int?)body["error"]?["innererror"]?["hResult"]);
        Assert.Equal(native.HResult, (int?)body["error"]?["details"]?["evidence"]?["hResult"]);
    }

    [Theory, InlineData("\"0x80004005\""), InlineData("4294967295"), InlineData("1.5")]
    public async Task Non_int32_worker_hresults_preserve_the_native_failure(string hResult)
    {
        using var fixture = new Fixture("21");
        fixture.Worker.Failure = () => new WorkerOperationException("Native failure.", -32603, "read-failed",
            "{\"exceptionType\":\"EngineeringException\",\"hResult\":" + hResult + "}");
        var body = (await fixture.Call("GetDeviceAddressing", new JsonObject { ["devicePath"] = new JsonArray("PLC") })).StructuredContent!;
        Assert.Equal("NATIVE_OPERATION_FAILED", (string?)body["error"]?["code"]);
        Assert.Null(body["error"]?["innererror"]?["hResult"]);
    }

    [Fact]
    public async Task Earlier_diagnostic_reads_remain_auditable_while_this_calls_rows_are_excluded()
    {
        using var fixture = new Fixture("21");
        var first = await fixture.Call("GetNativeInvocationLog", new JsonObject());
        Assert.Empty(Page(first)["records"]!.AsArray());
        string earlierId = (string)first.StructuredContent!["meta"]!["requestId"]!;
        var second = await fixture.Call("GetNativeInvocationLog", new JsonObject());
        var page = Page(second);
        Assert.Equal(2, (int?)page["matchedRecords"]);
        Assert.All(page["records"]!.AsArray(), row => Assert.Equal(earlierId, (string?)row?["id"]));
        Assert.All(page["records"]!.AsArray(), row => Assert.Equal("GetNativeInvocationLog", (string?)row?["tool"]));
        Assert.DoesNotContain(page["records"]!.AsArray(), row => (string?)row?["id"] == (string?)second.StructuredContent?["meta"]?["requestId"]);
    }

    [Theory, MemberData(nameof(Releases))]
    public async Task Journal_limits_and_pages_use_the_registered_route(string release)
    {
        using var fixture = new Fixture(release);
        var protocol = fixture.Tools.Single(t => t.ProtocolTool.Name == "GetNativeInvocationLog").ProtocolTool;
        var schema = protocol.InputSchema.GetProperty("properties");
        Assert.Equal(500, schema.GetProperty("take").GetProperty("maximum").GetInt32());
        Assert.Equal(10000, schema.GetProperty("offset").GetProperty("maximum").GetInt32());
        Assert.Equal(new[] { "object", "null" }, protocol.OutputSchema!.Value.GetProperty("properties").GetProperty("data")
            .GetProperty("type").EnumerateArray().Select(value => value.GetString()));
        foreach (var limit in new[] { (Name: "take", Value: 501, Maximum: "500"), (Name: "offset", Value: 10001, Maximum: "10000"),
            (Name: "take", Value: 0, Maximum: "500"), (Name: "offset", Value: -1, Maximum: "10000") })
        {
            var refused = (await fixture.Call("GetNativeInvocationLog", new JsonObject { [limit.Name] = limit.Value })).StructuredContent!;
            Assert.Equal("INVALID_ARGUMENT", (string?)refused["error"]?["code"]);
            Assert.Equal("/" + limit.Name, (string?)refused["error"]?["details"]?["parameter"]);
            Assert.Contains(limit.Maximum, (string?)refused["error"]?["message"]);
        }
        fixture.PauseJournalCapture();
        File.WriteAllLines(fixture.Log, Enumerable.Range(1, 6).Select(index => Row("native-" + index, index)));
        var maximumTake = Page(await fixture.Call("GetNativeInvocationLog", new JsonObject { ["take"] = 500 }));
        Assert.Equal(6, maximumTake["records"]!.AsArray().Count);
        var maximumOffset = Page(await fixture.Call("GetNativeInvocationLog", new JsonObject { ["offset"] = 10000 }));
        Assert.Empty(maximumOffset["records"]!.AsArray());
        Assert.Equal(6, (int?)maximumOffset["matchedRecords"]);
        var missingId = Page(await fixture.Call("GetNativeInvocationLog", new JsonObject { ["diagnosticId"] = "absent-fixture-id" }));
        Assert.Empty(missingId["records"]!.AsArray());
        Assert.Equal(0, (int?)missingId["matchedRecords"]);
        var ids = new List<string>();
        int? offset = 0;
        do
        {
            var page = Page(await fixture.Call("GetNativeInvocationLog", new JsonObject { ["take"] = 1, ["offset"] = offset }));
            Assert.Equal(6, (int?)page["matchedRecords"]);
            ids.Add((string)Assert.Single(page["records"]!.AsArray())!["id"]!);
            offset = (int?)page["nextOffset"];
        } while (offset.HasValue);
        Assert.Equal(Enumerable.Range(1, 6).Reverse().Select(index => "native-" + index), ids);
        File.Move(fixture.Log, fixture.Log + ".previous");
        File.WriteAllText(fixture.Log, "");
        ids.Clear();
        offset = 0;
        do
        {
            var page = Page(await fixture.Call("GetNativeInvocationLog", new JsonObject { ["take"] = 1, ["offset"] = offset }));
            Assert.Equal(6, (int?)page["matchedRecords"]);
            ids.Add((string)Assert.Single(page["records"]!.AsArray())!["id"]!);
            offset = (int?)page["nextOffset"];
        } while (offset.HasValue);
        Assert.Equal(Enumerable.Range(1, 6).Reverse().Select(index => "native-" + index), ids);
        var end = Page(await fixture.Call("GetNativeInvocationLog", new JsonObject { ["take"] = 1, ["offset"] = 8 }));
        Assert.Empty(end["records"]!.AsArray());
        Assert.Equal(6, (int?)end["matchedRecords"]);
        var first = Page(await fixture.Call("GetNativeInvocationLog", new JsonObject { ["take"] = 1 }));
        File.Delete(fixture.Log + ".previous");
        File.WriteAllText(fixture.Log, Row("native-6", 6) + "\n");
        var rotated = Page(await fixture.Call("GetNativeInvocationLog", new JsonObject { ["take"] = 1, ["offset"] = first["nextOffset"]!.DeepClone() }));
        Assert.Empty(rotated["records"]!.AsArray());
        Assert.Equal(1, (int?)rotated["matchedRecords"]);
    }

    [Theory, MemberData(nameof(Releases))]
    public async Task Recovered_probe_cannot_replace_the_reported_failure(string release)
    {
        using var fixture = new Fixture(release);
        fixture.Worker.Reply = new JsonObject { ["Message"] = "Reported failure.", ["Meta"] = new JsonObject {
            ["operationSuccess"] = false, ["nativeMessage"] = "Actual native reason.\nsecond line",
            ["nativeExceptionType"] = "EngineeringException", ["hResult"] = -123,
            ["Probe"] = new JsonObject { ["nativeMessage"] = "Recovered probe.", ["nativeExceptionType"] = "ProbeException" } } };
        var body = (await fixture.Call("GetDeviceAddressing", new JsonObject { ["devicePath"] = new JsonArray("PLC") })).StructuredContent!;
        Assert.Equal("Actual native reason.", (string?)body["error"]?["details"]?["nativeMessage"]);
        Assert.Equal("EngineeringException", (string?)body["error"]?["innererror"]?["exceptionType"]);
        Assert.Equal(-123, (int?)body["error"]?["innererror"]?["hResult"]);
    }

    [Theory, MemberData(nameof(Releases))]
    public async Task Explicit_worker_failure_has_priority_over_a_recovered_root_diagnostic(string release)
    {
        using var fixture = new Fixture(release);
        fixture.Worker.Reply = new JsonObject { ["Success"] = false, ["MayHaveChanged"] = false,
            ["nativeMessage"] = "Recovered root probe.", ["nativeExceptionType"] = "ProbeException",
            ["Failure"] = new JsonObject { ["Code"] = "NativeFailure", ["Message"] = "Reported worker failure.",
                ["Evidence"] = new JsonObject { ["exceptionType"] = "Siemens.Engineering.EngineeringException", ["hResult"] = -123 } } };
        var body = (await fixture.Call("GetDeviceAddressing", new JsonObject { ["devicePath"] = new JsonArray("PLC") })).StructuredContent!;
        Assert.Equal("NATIVE_OPERATION_FAILED", (string?)body["error"]?["code"]);
        Assert.Equal("Reported worker failure.", (string?)body["error"]?["details"]?["nativeMessage"]);
        Assert.Equal("EngineeringException", (string?)body["error"]?["innererror"]?["exceptionType"]);
        Assert.Equal(-123, (int?)body["error"]?["innererror"]?["hResult"]);
        Assert.DoesNotContain("Recovered root probe", body["error"]!.ToJsonString());
    }

    [Fact]
    public void Native_diagnostic_uses_only_current_string_credentials()
    {
        using var outer = Host.InvocationJournal.Observe("outer", "fixture", "host", "21", false,
            () => JsonSerializer.Serialize(new { password = "old-secret" }));
        using (Host.InvocationJournal.Observe("child", "fixture", "host", "21", false,
            () => JsonSerializer.Serialize(new {
                password = "echoed-secret", properties = new { PasswordValidity = 9, PasswordEnabled = true, PasswordLetter = "x", PasswordEmpty = "" } })))
        {
            var evidence = new Dictionary<string, JsonElement> { ["reason"] = JsonSerializer.SerializeToElement("echoed-secret old-secret Value 9 true x"),
                ["exceptionType"] = JsonSerializer.SerializeToElement("echoed-secret.NativeException"), ["hResult"] = JsonSerializer.SerializeToElement(-9) };
            var error = new Error("Native failure.", new NativeOperationFailedDetails(null,
                "echoed-secret old-secret PasswordValidity: 9 PasswordEnabled: true x\nprivate stack", evidence));
            string wire = V4Json.Serialize(error);
            Assert.DoesNotContain("echoed-secret", wire);
            Assert.DoesNotContain("old-secret", wire);
            Assert.Contains("PasswordValidity: 9", wire);
            Assert.Contains("PasswordEnabled: true x", wire);
            var diagnostic = JsonNode.Parse(wire)!["innererror"];
            Assert.Equal(-9, (int?)diagnostic?["hResult"]);
            Assert.Equal("child", (string?)diagnostic?["diagnosticId"]);
        }
        var restored = new Error("Native failure.", new NativeOperationFailedDetails(null, "old-secret echoed-secret", new Dictionary<string, JsonElement>()));
        string outerWire = V4Json.Serialize(restored);
        Assert.DoesNotContain("old-secret", outerWire);
        Assert.Contains("echoed-secret", outerWire);
    }

    [Fact]
    public void Multiline_credentials_are_masked_before_the_first_line_is_selected()
    {
        using var call = Host.InvocationJournal.Observe("multiline", "fixture", "host", "21", false,
            () => JsonSerializer.Serialize(new { password = "fixture-prefix\nfixture-suffix" }));
        var details = new NativeOperationFailedDetails(null, "Refused fixture-prefix\nfixture-suffix.\n   at stack",
            new Dictionary<string, JsonElement>());
        Assert.Equal("Refused ••••", details.NativeMessage);
    }

    [Fact]
    public void A_short_password_cannot_change_the_journal_correlation_key()
    {
        const string id = "stable-ab-correlation";
        using var call = Host.InvocationJournal.Observe(id, "fixture", "host", "21", false,
            () => JsonSerializer.Serialize(new { password = "ab" }));
        var body = FoundationV4Result.Failure("21", "GetDeviceAddressing", id, true, false, null,
            new WorkerOperationException("Refused ab.", -32603, "read-failed")).StructuredContent!;
        Assert.Equal(id, (string?)body["meta"]?["requestId"]);
        Assert.Equal(id, (string?)body["error"]?["innererror"]?["diagnosticId"]);
        Assert.DoesNotContain("ab", (string?)body["error"]?["details"]?["nativeMessage"]);
    }

    [Fact]
    public void Blank_native_text_uses_the_known_failure_reason()
    {
        var evidence = new Dictionary<string, JsonElement> { ["nativeMessage"] = JsonSerializer.SerializeToElement(" ") };
        var result = FoundationV4Result.Composite("21", "GetDeviceAddressing", "blank-reason", new JsonObject(), Outcome.ReadFailed,
            Execution.ReadOnly, new Error("The native read did not complete.", new NativeOperationFailedDetails(null, "", evidence)));
        Assert.Equal("The native read did not complete.", (string?)result.StructuredContent?["error"]?["details"]?["nativeMessage"]);
    }

    [Fact]
    public void Session_contract_reports_the_wrapped_native_cause()
    {
        using var fixture = new Fixture("21");
        var cause = new IOException("Wrapped native reason.");
        var body = Host.SessionToolContract.Run("GetDeviceAddressing", false, true,
            () => { Host.InvocationJournal.NativeCallStarted(); throw new TargetInvocationException(cause); }).StructuredContent!;
        Assert.Equal("IOException", (string?)body["error"]?["innererror"]?["exceptionType"]);
        Assert.Equal(cause.HResult, (int?)body["error"]?["innererror"]?["hResult"]);
        Assert.Equal("Wrapped native reason.", (string?)body["error"]?["details"]?["nativeMessage"]);
    }

    [Theory, InlineData(false), InlineData(true)]
    public void Session_unknown_outcome_preserves_the_existing_io_diagnostic_rule(bool io)
    {
        using var fixture = new Fixture("21");
        Exception error = io ? new IOException("IO failure\nstack") : new InvalidOperationException("Private worker failure");
        var body = Host.SessionToolContract.Failure("SetDeviceItemIoAddress", error, true, true).StructuredContent!;
        Assert.Equal("OUTCOME_UNKNOWN", (string?)body["error"]?["code"]);
        Assert.Equal(io ? "IO failure" : null, (string?)body["data"]?["evidence"]?["workerMessage"]);
        Assert.Null(body["error"]?["innererror"]);
    }

    [Fact]
    public void Legacy_non_json_journal_arguments_preserve_the_invocation_scope()
    {
        using var call = Host.InvocationJournal.Observe("legacy-journal", "fixture", "host", "21", false, () => "legacy argument text");
        var body = FoundationV4Result.Failure("21", "GetDeviceAddressing", "legacy-journal", true, false, null,
            new WorkerOperationException("Native read failed.", -32603, "read-failed")).StructuredContent!;
        Assert.Equal("legacy-journal", (string?)body["error"]?["innererror"]?["diagnosticId"]);
        Assert.Equal("Native read failed.", (string?)body["error"]?["details"]?["nativeMessage"]);
    }

    [Theory, InlineData("x"), InlineData("🔑")]
    public void Security_success_paths_keep_single_character_secret_redaction(string secret)
    {
        var result = Host.SecurityToolContract.MapResult("ManageDeviceUsers", new Host.ResponseMessage {
            Message = "Accepted " + secret, Meta = new JsonObject { ["success"] = true, ["value"] = "Value " + secret } }, true, false, secret);
        Assert.Equal("Accepted [redacted]", (string?)result.StructuredContent?["data"]?["summary"]);
        Assert.Equal("Value [redacted]", (string?)result.StructuredContent?["data"]?["value"]);
    }

    [Theory, InlineData("x"), InlineData("🔑")]
    public void Security_mapped_native_messages_keep_single_character_secret_redaction(string secret)
    {
        var result = Host.SecurityToolContract.MapResult("ManageDeviceUsers", new Host.ResponseMessage {
            Message = "Native reason " + secret, Meta = new JsonObject { ["success"] = false } }, true, false, secret);
        Assert.Equal("NATIVE_OPERATION_FAILED", (string?)result.StructuredContent?["error"]?["code"]);
        Assert.Equal("Native reason [redacted]", (string?)result.StructuredContent?["error"]?["message"]);
        Assert.Equal("Native reason [redacted]", (string?)result.StructuredContent?["error"]?["details"]?["nativeMessage"]);
        Assert.All(result.Content.OfType<TextContentBlock>(), text => Assert.DoesNotContain("Native reason " + secret, text.Text));
    }

    [Fact]
    public void Security_catch_masks_duplicate_key_diagnostics_without_escaping_the_tool_call()
    {
        var result = Host.SecurityToolContract.Invoke("ManageDeviceUsers", true, false,
            () => throw new System.IO.IOException("{\"a\":1,\"a\":2,\"password\":\"Hunter2\"}"));
        Assert.Equal("NATIVE_OPERATION_FAILED", (string?)result.StructuredContent?["error"]?["code"]);
        Assert.DoesNotContain("Hunter2", result.StructuredContent!.ToJsonString());
    }

    [Theory, InlineData("x"), InlineData("🔑")]
    public void Security_native_diagnostics_exclude_single_character_cross_text_replacement(string secret)
    {
        var result = Host.SecurityToolContract.Invoke("ManageDeviceUsers", true, false,
            () => throw new System.IO.IOException("Native reason " + secret), secret);
        Assert.Equal("Native reason " + secret, (string?)result.StructuredContent?["error"]?["details"]?["nativeMessage"]);
    }

    private static JsonObject Page(CallToolResult result)
    {
        Assert.True((bool?)result.StructuredContent?["ok"], result.StructuredContent?.ToJsonString());
        return result.StructuredContent!["data"]!["evidence"]!.AsObject();
    }

    private static string Row(string id, int second) => new JsonObject { ["utc"] = $"2026-11-13T00:00:{second:00}Z", ["id"] = id,
        ["tool"] = "native:GetDeviceAddressing", ["phase"] = "THREW", ["pid"] = 123, ["thread"] = 4, ["apartment"] = "STA" }.ToJsonString();

    private sealed class Fixture : IDisposable
    {
        private readonly string? previousDiagnostics = Environment.GetEnvironmentVariable("TIA_MCP_DIAGNOSTICS_DIRECTORY");
        private readonly TiaOpenness.Shared.ApprovalSettings previousApproval;
        private readonly string root = Path.Combine(AppContext.BaseDirectory, "native-diagnostic-" + Guid.NewGuid().ToString("N"));
        internal Worker Worker { get; } = new();
        internal IReadOnlyList<McpServerTool> Tools { get; }
        private readonly HardwareAddressingAdmissionTests.UnboundWorker contextWorker = new();
        private readonly Host.ImportStagingHostLifetime lifetime = new();
        private readonly IDisposable session;
        private readonly string previousRelease = enginehost::TiaMcp.FoundationHost.EngineHostConfiguration.ReleaseKey;
        internal string Log { get; }
        internal Fixture(string release, bool wrapped = true)
        {
            Environment.SetEnvironmentVariable("TIA_MCP_DIAGNOSTICS_DIRECTORY", root);
            Directory.CreateDirectory(root);
            Log = Path.Combine(root, "calls-fixture.jsonl");
            Host.InvocationJournal.ConfigureOutput(line => File.AppendAllText(Log, line + "\n"),
                () => TiaMcpServer.ModelContextProtocol.InvocationJournal.CurrentCallId
                    ?? TiaMcpServer.ModelContextProtocol.InvocationJournal.CorrelationId);
            TiaMcpServer.ModelContextProtocol.InvocationJournal.ConfigureOutput(line => File.AppendAllText(Log, line + "\n"));
            previousApproval = TiaOpenness.Shared.ApprovalSettings.Load(TiaOpenness.Shared.ApprovalSettings.SettingsPath);
            new TiaOpenness.Shared.ApprovalSettings(false, 1).Save(TiaOpenness.Shared.ApprovalSettings.SettingsPath);
            enginehost::TiaMcp.FoundationHost.EngineHostConfiguration.ReleaseKey = release;
            var catalog = new Catalog(release);
            session = enginehost::TiaMcp.FoundationHost.EngineHostConfiguration.Enter(
                new enginehost::TiaMcp.FoundationHost.EngineSessionContext(contextWorker) {
                    Catalog = catalog, Invoker = new enginehost::TiaMcp.FoundationHost.WorkerToolInvoker(catalog, contextWorker, lifetime) });
            var tools = new List<McpServerTool>();
            foreach (var entry in new[] {
                (Owner: typeof(Host.AddressesTools), Method: "GetDeviceAddressingV4", Name: "GetDeviceAddressing"),
                (Owner: typeof(Host.HardwareSecurityTools), Method: "ManageDeviceUsers", Name: "ManageDeviceUsers"),
                (Owner: typeof(Host.SecurityDeepTools), Method: "ManagePasswordPolicy", Name: "ManagePasswordPolicy"),
                (Owner: typeof(Host.ProjectLifecycleProjectTools), Method: "BuildProjectScaffoldV4", Name: "BuildProjectScaffold") })
            {
                if (!TiaMcp.Adapters.Contracts.PortedFamilies.Available(release, entry.Name)) continue;
                var inner = new FoundationTool(Worker, release, entry.Owner.GetMethod(entry.Method)!, entry.Name);
                tools.Add(wrapped ? PortedToolContract.Wrap(inner)
                    : new FoundationV4Tool(inner, release, () => new TiaOpenness.Shared.ApprovalSettings(false, 1)));
            }
            tools.Add(new FoundationV4Tool(Host.DeclaredToolMetadata.CreateToolCore(
                typeof(Host.EngineeringDiagnosticsTools).GetMethod("GetNativeInvocationLogV4")!, null,
                _ => new Host.EngineeringDiagnosticsTools()), release, () => new TiaOpenness.Shared.ApprovalSettings(false, 1)));
            Tools = tools;
        }
        internal async Task<CallToolResult> Call(string name, JsonObject args)
            => await Tools.Single(t => t.ProtocolTool.Name == name).InvokeAsync(new RequestContext<CallToolRequestParams>(
                DispatchProxy.Create<IMcpServer, WorkerRobustnessTests.ServerProxy>()) {
                Params = new() { Name = name, Arguments = args.Deserialize<Dictionary<string, JsonElement>>() } });
        internal void PauseJournalCapture()
        {
            Host.InvocationJournal.ConfigureOutput(_ => { });
            TiaMcpServer.ModelContextProtocol.InvocationJournal.ConfigureOutput(_ => { });
        }
        public void Dispose()
        {
            Host.InvocationJournal.ConfigureOutput(null);
            TiaMcpServer.ModelContextProtocol.InvocationJournal.ConfigureOutput(null);
            Environment.SetEnvironmentVariable("TIA_MCP_DIAGNOSTICS_DIRECTORY", previousDiagnostics);
            previousApproval.Save(TiaOpenness.Shared.ApprovalSettings.SettingsPath);
            session.Dispose();
            lifetime.Dispose();
            enginehost::TiaMcp.FoundationHost.EngineHostConfiguration.ReleaseKey = previousRelease;
            contextWorker.Dispose();
            Worker.Dispose();
            Directory.Delete(root, true);
        }
    }

    private sealed class Catalog : Host.IToolCatalogView
    {
        public IReadOnlyDictionary<string, Host.ToolDescriptor> All { get; }
        public IReadOnlyDictionary<string, Host.ToolDescriptor> IncludingUnavailable => All;
        public IReadOnlyList<Host.ToolDescriptor> Lite => All.Values.ToArray();
        public JsonArray BehaviorCapabilities => new();
        public Host.ToolDescriptor? Find(string name, bool includeUnavailable = false) => All.GetValueOrDefault(name);
        internal Catalog(string release)
        {
            var descriptors = new Dictionary<string, Host.ToolDescriptor>();
            foreach (var method in Host.PortedToolDeclarations.Methods(release)
                .Concat(typeof(Host.EngineeringDiagnosticsTools).GetMethods().Where(m => m.Name == "GetNativeInvocationLogV4")))
            {
                string name = method.GetCustomAttribute<McpServerToolAttribute>()!.Name!;
                Host.DeclaredToolMetadata.Create(name, method, _ => throw new InvalidOperationException("Metadata only."), out var descriptor);
                descriptors[name] = descriptor;
            }
            All = descriptors;
        }
    }

    private sealed class Worker : IFoundationWorker
    {
        internal Func<Exception>? Failure;
        internal JsonObject? Reply;
        internal Func<string, JsonObject, JsonNode?>? Handle;
        public Task<JsonNode?> Call(string operation, JsonObject arguments, CancellationToken token)
        {
            if (operation == "ReadState") return Task.FromResult<JsonNode?>(new JsonObject {
                ["ProcessId"] = 123, ["IsAttached"] = true, ["ProjectFile"] = "C:/Fixture.ap21" });
            if (Failure != null) throw Failure();
            if (Handle != null) return Task.FromResult(Handle(operation, arguments));
            return Task.FromResult<JsonNode?>(Reply?.DeepClone());
        }
        public void Dispose() { }
    }
}
