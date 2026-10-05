using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using TiaMcp.Logic.V4;
using Xunit;

namespace TiaMcpServer.Tests
{
    public sealed class V4EnvelopeTests
    {
        private const string Hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        private static readonly DateTimeOffset Stamp = new DateTimeOffset(2026, 10, 3, 0, 0, 0, TimeSpan.Zero);
        private static readonly IReadOnlyDictionary<string, JsonElement> Empty = new Dictionary<string, JsonElement>();
        private const string Rejected = """{"schemaVersion":4,"ok":false,"data":null,"error":{"code":"PROJECT_NOT_BOUND","message":"No project is bound.","details":{}},"meta":{"timestamp":"2026-10-03T00:00:00Z","releaseKey":"21","tool":"GetPlcBlockInfo","requestId":"request-1","outcome":"rejected-before-operation","execution":"not-started","requiresSessionReset":false,"behaviorPolicy":"not-applicable","completeness":"none","paging":null,"warnings":[]}}""";

        private static JsonElement Json(string json)
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.Clone();
        }

        private static IReadOnlyDictionary<string, JsonElement> Evidence() => new Dictionary<string, JsonElement>
        {
            ["observation"] = Json("""{"source":"readback","nativeResult":null}"""),
            ["Executed"] = Json("true")
        };

        private static Meta Metadata(Outcome outcome = Outcome.RejectedBeforeOperation, Execution execution = Execution.NotStarted,
            bool reset = false, Completeness completeness = Completeness.None, Paging? paging = null,
            IReadOnlyList<Warning>? warnings = null, BehaviorPolicy policy = BehaviorPolicy.NotApplicable) =>
            new Meta(Stamp, "21", "GetPlcBlockInfo", "request-1", outcome, execution, reset, policy, completeness,
                paging, warnings ?? Array.Empty<Warning>());

        private static Envelope Reject() => Envelope.Create<object?>(null,
            new Error("No project is bound.", new ProjectNotBoundDetails()), Metadata());

        private static void Golden<T>(T value, string expected)
        {
            Assert.Equal(Encoding.UTF8.GetBytes(expected), V4Json.SerializeUtf8(value));
            Assert.Equal(expected, V4Json.Serialize(V4Json.Deserialize<T>(expected)));
        }

        public static IEnumerable<object[]> ErrorGoldens()
        {
            var samples = new (ErrorDetails Details, string Json)[]
            {
                (new InvalidArgumentDetails("mode", new[] { "preview", "apply" }), """{"code":"INVALID_ARGUMENT","message":"Failure.","details":{"parameter":"mode","allowedValues":["preview","apply"]}}"""),
                (new LimitExceededDetails("items", 1000, 1001), """{"code":"LIMIT_EXCEEDED","message":"Failure.","details":{"parameter":"items","limit":1000,"actual":1001}}"""),
                (new UnsupportedCapabilityDetails("14sp1", "compile", null), """{"code":"UNSUPPORTED_CAPABILITY","message":"Failure.","details":{"releaseKey":"14sp1","capability":"compile","action":null}}"""),
                (new ToolNotFoundDetails("MissingTool"), """{"code":"TOOL_NOT_FOUND","message":"Failure.","details":{"tool":"MissingTool"}}"""),
                (new ProjectNotBoundDetails(), """{"code":"PROJECT_NOT_BOUND","message":"Failure.","details":{}}"""),
                (new NotFoundDetails("Block"), """{"code":"NOT_FOUND","message":"Failure.","details":{"target":"Block"}}"""),
                (new TargetAmbiguousDetails("PLC", new[] { "PLC_A", "PLC_B" }), """{"code":"TARGET_AMBIGUOUS","message":"Failure.","details":{"target":"PLC","candidates":["PLC_A","PLC_B"]}}"""),
                (new IdentityMismatchDetails("project", "epoch:1", "epoch:2"), """{"code":"IDENTITY_MISMATCH","message":"Failure.","details":{"target":"project","expected":"epoch:1","actual":"epoch:2"}}"""),
                (new AlreadyExistsDetails("Block"), """{"code":"ALREADY_EXISTS","message":"Failure.","details":{"target":"Block"}}"""),
                (new ConfirmationRequiredDetails(Hash), """{"code":"CONFIRMATION_REQUIRED","message":"Failure.","details":{"planHash":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}}"""),
                (new PlanStaleDetails(Hash, "input changed"), """{"code":"PLAN_STALE","message":"Failure.","details":{"planHash":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","reason":"input changed"}}"""),
                (new PreconditionFailedDetails("borrowed", "project"), """{"code":"PRECONDITION_FAILED","message":"Failure.","details":{"condition":"borrowed","target":"project"}}"""),
                (new OfflineRequiredDetails(new[] { "PLC_A" }), """{"code":"OFFLINE_REQUIRED","message":"Failure.","details":{"targets":["PLC_A"]}}"""),
                (new AuthenticationRequiredDetails("Safety"), """{"code":"AUTHENTICATION_REQUIRED","message":"Failure.","details":{"capability":"Safety"}}"""),
                (new AccessDeniedDetails("write", "Block"), """{"code":"ACCESS_DENIED","message":"Failure.","details":{"operation":"write","target":"Block"}}"""),
                (new SessionResetRequiredDetails("channel closed"), """{"code":"SESSION_RESET_REQUIRED","message":"Failure.","details":{"reason":"channel closed"}}"""),
                (new ResourceUnavailableDetails("SDK"), """{"code":"RESOURCE_UNAVAILABLE","message":"Failure.","details":{"resource":"SDK"}}"""),
                (new IoFailedDetails("read", "input.xml"), """{"code":"IO_FAILED","message":"Failure.","details":{"operation":"read","path":"input.xml"}}"""),
                (new NativeOperationFailedDetails("E1", "Native failure", Evidence()), """{"code":"NATIVE_OPERATION_FAILED","message":"Failure.","details":{"nativeCode":"E1","nativeMessage":"Native failure","evidence":{"Executed":true,"observation":{"source":"readback","nativeResult":null}}}}"""),
                (new CancelledDetails("before dispatch"), """{"code":"CANCELLED","message":"Failure.","details":{"stage":"before dispatch"}}"""),
                (new TimeoutDetails("read"), """{"code":"TIMEOUT","message":"Failure.","details":{"stage":"read"}}"""),
                (new NotExecutedDetails(0), """{"code":"NOT_EXECUTED","message":"Failure.","details":{"causeIndex":0}}"""),
                (new PartialFailureDetails(1, 1, 1), """{"code":"PARTIAL_FAILURE","message":"Failure.","details":{"succeeded":1,"failed":1,"notExecuted":1}}"""),
                (new OutcomeUnknownDetails("write", Evidence()), """{"code":"OUTCOME_UNKNOWN","message":"Failure.","details":{"stage":"write","evidence":{"Executed":true,"observation":{"source":"readback","nativeResult":null}}}}"""),
                (new InternalErrorDetails("diagnostic-1"), """{"code":"INTERNAL_ERROR","message":"Failure.","details":{"diagnosticId":"diagnostic-1"}}"""),
            };
            foreach (var culture in new[] { "en-US", "zh-CN" })
                foreach (var sample in samples) yield return new object[] { culture, sample.Details, sample.Json };
        }

        [Theory]
        [MemberData(nameof(ErrorGoldens))]
        public void TypedErrorDetailsHaveGoldenBytesAndRoundTrip(string culture, ErrorDetails details, string expected)
        {
            InCulture(culture, () => Golden(new Error("Failure.", details), expected));
            Assert.Equal(details.GetType(), V4Json.Deserialize<Error>(expected).Details.GetType());
        }

        public static IEnumerable<object[]> OutcomeGoldens()
        {
            foreach (var culture in new[] { "en-US", "zh-CN" })
            {
                yield return new object[] { culture, Outcome.Succeeded, Execution.ReadOnly, 0, "succeeded", "read-only" };
                yield return new object[] { culture, Outcome.Succeeded, Execution.Completed, 0, "succeeded", "completed" };
                yield return new object[] { culture, Outcome.RejectedBeforeOperation, Execution.NotStarted, 2, "rejected-before-operation", "not-started" };
                yield return new object[] { culture, Outcome.ReadFailed, Execution.ReadOnly, 3, "read-failed", "read-only" };
                yield return new object[] { culture, Outcome.Failed, Execution.Completed, 3, "failed", "completed" };
                yield return new object[] { culture, Outcome.Partial, Execution.Partial, 4, "partial", "partial" };
                yield return new object[] { culture, Outcome.Unknown, Execution.Unknown, 5, "unknown", "unknown" };
            }
        }

        [Theory]
        [MemberData(nameof(OutcomeGoldens))]
        public void OutcomesMcpAndCliShareTheEnvelope(string culture, Outcome outcome, Execution execution,
            int exitCode, string outcomeText, string executionText)
        {
            InCulture(culture, () =>
            {
                Error? error = outcome switch
                {
                    Outcome.Succeeded => null,
                    Outcome.Partial => new Error("Failure.", new PartialFailureDetails(1, 1, 0)),
                    Outcome.Unknown => new Error("Failure.", new OutcomeUnknownDetails("write", Empty)),
                    _ => new Error("No project is bound.", new ProjectNotBoundDetails())
                };
                string errorJson = outcome switch
                {
                    Outcome.Succeeded => "null",
                    Outcome.Partial => """{"code":"PARTIAL_FAILURE","message":"Failure.","details":{"succeeded":1,"failed":1,"notExecuted":0}}""",
                    Outcome.Unknown => """{"code":"OUTCOME_UNKNOWN","message":"Failure.","details":{"stage":"write","evidence":{}}}""",
                    _ => """{"code":"PROJECT_NOT_BOUND","message":"No project is bound.","details":{}}"""
                };
                string expected = """{"schemaVersion":4,"ok":OK,"data":null,"error":ERROR,"meta":{"timestamp":"2026-10-03T00:00:00Z","releaseKey":"21","tool":"GetPlcBlockInfo","requestId":"request-1","outcome":"OUTCOME","execution":"EXECUTION","requiresSessionReset":RESET,"behaviorPolicy":"not-applicable","completeness":"none","paging":null,"warnings":[]}}"""
                    .Replace("OK", outcome == Outcome.Succeeded ? "true" : "false")
                    .Replace("OUTCOME", outcomeText).Replace("EXECUTION", executionText)
                    .Replace("RESET", outcome == Outcome.Unknown ? "true" : "false").Replace("ERROR", errorJson);
                var envelope = Envelope.Create<object?>(null, error, Metadata(outcome, execution, outcome == Outcome.Unknown));
                Golden(envelope, expected);
                var mapped = McpResult.From(envelope);
                Assert.Equal(expected, Assert.Single(mapped.Content).Text);
                Assert.Equal("text", mapped.Content[0].Type);
                Assert.Equal(expected, mapped.StructuredContent.GetRawText());
                Assert.Equal(!envelope.Ok, mapped.IsError);
                Assert.Equal(exitCode, CliExitCode.From(envelope));
            });
        }

        [Fact]
        public void NormativeExampleAndMcpWireAreExact()
        {
            Golden(Reject(), Rejected);
            // Fixed empty-data success keeps the outer MCP escaping independently reviewable.
            var result = McpResult.From(Reject());
            string expected = "{\"structuredContent\":" + Rejected + ",\"content\":[{\"type\":\"text\",\"text\":\""
                + Rejected.Replace("\"", "\\u0022") + "\"}],\"isError\":true}";
            Assert.Equal(Encoding.UTF8.GetBytes(expected), V4Json.SerializeUtf8(result));
            using var mapped = JsonDocument.Parse(V4Json.Serialize(result));
            Assert.Equal(new[] { "structuredContent", "content", "isError" }, mapped.RootElement.EnumerateObject().Select(p => p.Name));
            Assert.Equal(Rejected, mapped.RootElement.GetProperty("structuredContent").GetRawText());
            Assert.Equal(Rejected, mapped.RootElement.GetProperty("content")[0].GetProperty("text").GetString());
            Assert.True(mapped.RootElement.GetProperty("isError").GetBoolean());
            Assert.Equal(64, CliExitCode.From(CliFailure.Syntax));
            Assert.Equal(70, CliExitCode.From(CliFailure.ContextCreation));
            Assert.Throws<ArgumentOutOfRangeException>(() => CliExitCode.From((CliFailure)7));
        }

        [Theory]
        [InlineData(WarningCode.IncompleteData, "INCOMPLETE_DATA")]
        [InlineData(WarningCode.NativeWarning, "NATIVE_WARNING")]
        [InlineData(WarningCode.CandidateOnly, "CANDIDATE_ONLY")]
        [InlineData(WarningCode.UnverifiedBehavior, "UNVERIFIED_BEHAVIOR")]
        [InlineData(WarningCode.CleanupFailed, "CLEANUP_FAILED")]
        [InlineData(WarningCode.NativeCapabilityLimit, "NATIVE_CAPABILITY_LIMIT")]
        [InlineData(WarningCode.DiagnosticWriteFailed, "DIAGNOSTIC_WRITE_FAILED")]
        public void WarningsAreClosedAndKeepDetails(WarningCode code, string spelling)
        {
            Golden(new Warning(code, "Warning.", Empty),
                """{"code":"CODE","message":"Warning.","details":{}}""".Replace("CODE", spelling));
        }

        [Theory]
        [InlineData("en-US")]
        [InlineData("zh-CN")]
        public void OffsetCursorAndExportGoldens(string culture)
        {
            InCulture(culture, () =>
            {
                Golden(new Paging(PagingMode.Offset, 0, 2, 2, null, null, 5, false),
                    """{"mode":"offset","offset":0,"limit":2,"nextOffset":2,"cursor":null,"nextCursor":null,"total":5,"complete":false}""");
                Golden(new Paging(PagingMode.Offset, 4, 2, null, null, null, 5, true),
                    """{"mode":"offset","offset":4,"limit":2,"nextOffset":null,"cursor":null,"nextCursor":null,"total":5,"complete":true}""");
                Golden(new Paging(PagingMode.Cursor, null, 2, null, null, "next-1", null, false),
                    """{"mode":"cursor","offset":null,"limit":2,"nextOffset":null,"cursor":null,"nextCursor":"next-1","total":null,"complete":false}""");
                Golden(new Paging(PagingMode.Cursor, null, 2, null, "next-1", null, null, true),
                    """{"mode":"cursor","offset":null,"limit":2,"nextOffset":null,"cursor":"next-1","nextCursor":null,"total":null,"complete":true}""");
                Golden(new ExportHandle("export-1", "application/json", 2147483648L, Hash, null),
                    """{"id":"export-1","mediaType":"application/json","byteLength":2147483648,"sha256":"HASH","expiresUtc":null}""".Replace("HASH", Hash));
                Golden(new ExportHandle("export-1", "application/json", 12, Hash, Stamp),
                    """{"id":"export-1","mediaType":"application/json","byteLength":12,"sha256":"HASH","expiresUtc":"2026-10-03T00:00:00Z"}""".Replace("HASH", Hash));
            });
        }

        [Fact]
        public void BatchPreservesEveryChildAndUnknownTakesPriority()
        {
            var success = Envelope.Create(new { Executed = true, NativeResult = (object?)null,
                Observation = new { Source = "readback" } }, null, Metadata(Outcome.Succeeded, Execution.Completed));
            var stopped = Envelope.Create<object?>(null, new Error("Stopped.", new NotExecutedDetails(1)), Metadata());
            var items = new BatchData(new[] { new BatchItem(0, "A", success), new BatchItem(1, null, Reject()), new BatchItem(2, "C", stopped) });
            var batch = V4Json.Batch(items, new Error("Mixed.", new PartialFailureDetails(1, 1, 1)), Metadata(Outcome.Partial, Execution.Partial));
            string successJson = """{"schemaVersion":4,"ok":true,"data":{"executed":true,"nativeResult":null,"observation":{"source":"readback"}},"error":null,"meta":{"timestamp":"2026-10-03T00:00:00Z","releaseKey":"21","tool":"GetPlcBlockInfo","requestId":"request-1","outcome":"succeeded","execution":"completed","requiresSessionReset":false,"behaviorPolicy":"not-applicable","completeness":"none","paging":null,"warnings":[]}}""";
            string stoppedJson = Rejected.Replace("""{"code":"PROJECT_NOT_BOUND","message":"No project is bound.","details":{}}""",
                """{"code":"NOT_EXECUTED","message":"Stopped.","details":{"causeIndex":1}}""");
            string dataJson = """{"items":[{"index":0,"target":"A","result":SUCCESS},{"index":1,"target":null,"result":REJECTED},{"index":2,"target":"C","result":STOPPED}]}"""
                .Replace("SUCCESS", successJson).Replace("REJECTED", Rejected).Replace("STOPPED", stoppedJson);
            Golden(items, dataJson);
            Golden(batch, """{"schemaVersion":4,"ok":false,"data":DATA,"error":{"code":"PARTIAL_FAILURE","message":"Mixed.","details":{"succeeded":1,"failed":1,"notExecuted":1}},"meta":{"timestamp":"2026-10-03T00:00:00Z","releaseKey":"21","tool":"GetPlcBlockInfo","requestId":"request-1","outcome":"partial","execution":"partial","requiresSessionReset":false,"behaviorPolicy":"not-applicable","completeness":"none","paging":null,"warnings":[]}}""".Replace("DATA", dataJson));
            Assert.Equal(successJson, V4Json.Serialize(V4Json.ReadBatch(batch).Items[0].Result));
            var unknown = Envelope.Create<object?>(null, new Error("Lost.", new OutcomeUnknownDetails("write", Evidence())),
                Metadata(Outcome.Unknown, Execution.Unknown, true));
            var unknownItems = new BatchData(new[] { new BatchItem(0, null, batch), new BatchItem(1, "B", unknown) });
            Assert.Throws<ArgumentException>(() => V4Json.Batch(unknownItems, batch.Error, batch.Meta));
            var parent = V4Json.Batch(unknownItems, unknown.Error, unknown.Meta);
            Assert.Equal(Outcome.Unknown, V4Json.ReadBatch(parent).Items[1].Result.Meta.Outcome);
            Assert.Throws<ArgumentException>(() => V4Json.Batch(items, null, success.Meta));
            Assert.Throws<ArgumentException>(() => new BatchData(new[] { new BatchItem(1, null, success) }));
        }

        [Theory]
        [InlineData("en-US")]
        [InlineData("zh-CN")]
        public void PlanIdentityAndOperationsKeepOrderAndHashes(string culture)
        {
            InCulture(culture, () =>
            {
                var identity = new PlanIdentity(42, Stamp, "project.ap21", 7, null, Array.Empty<PlanFile>());
                var operation = new PlanOperation("ImportPlcBlock", "Block", Json("""{"overwrite":false}"""));
                var plan = new Plan(Hash, "21", "ImportPlcBlock", Hash, identity,
                    new Dictionary<string, string> { ["source.xml"] = Hash }, null, new[] { operation }, Array.Empty<Warning>());
                Golden(plan, """{"hash":"HASH","releaseKey":"21","tool":"ImportPlcBlock","argumentsHash":"HASH","identity":{"processId":42,"processStartUtc":"2026-10-03T00:00:00Z","projectFile":"project.ap21","bindingEpoch":7,"workspaceRoot":null,"files":[]},"inputHashes":{"source.xml":"HASH"},"inventoryHash":null,"operations":[{"tool":"ImportPlcBlock","target":"Block","arguments":{"overwrite":false}}],"warnings":[]}""".Replace("HASH", Hash));
                Golden(new PlanIdentity(null, null, null, null, "workspace", new[] { new PlanFile("output.xml", false, null, null) }),
                    """{"processId":null,"processStartUtc":null,"projectFile":null,"bindingEpoch":null,"workspaceRoot":"workspace","files":[{"path":"output.xml","exists":false,"byteLength":null,"sha256":null}]}""");
                Golden(new PlanFile("input.xml", true, 12, Hash),
                    """{"path":"input.xml","exists":true,"byteLength":12,"sha256":"HASH"}""".Replace("HASH", Hash));
            });
        }

        [Theory]
        [InlineData("en-US")]
        [InlineData("zh-CN")]
        public void CultureNullsUtcAndCorrelationAreStable(string culture)
        {
            InCulture(culture, () =>
            {
                var timestamp = new DateTimeOffset(2026, 10, 3, 8, 0, 0, TimeSpan.FromHours(8)).AddTicks(1234567);
                Golden(timestamp, "\"2026-10-03T00:00:00.1234567Z\"");
                var value = Envelope.Create(new { Amount = 42.5m, Text = "中文", Missing = (string?)null }, null,
                    new Meta(timestamp, null, "ReadValue", "upstream-id", Outcome.Succeeded, Execution.ReadOnly, false,
                        BehaviorPolicy.SafeV4, Completeness.Complete, null, Array.Empty<Warning>()));
                Golden(value, """{"schemaVersion":4,"ok":true,"data":{"amount":42.5,"text":"\u4E2D\u6587","missing":null},"error":null,"meta":{"timestamp":"2026-10-03T00:00:00.1234567Z","releaseKey":null,"tool":"ReadValue","requestId":"upstream-id","outcome":"succeeded","execution":"read-only","requiresSessionReset":false,"behaviorPolicy":"safe-v4","completeness":"complete","paging":null,"warnings":[]}}""");
                Assert.Equal("upstream-id", Meta.Correlate("upstream-id"));
                Assert.NotEqual(Meta.Correlate(null), Meta.Correlate(null));
                Assert.Equal(32, Meta.Correlate(null).Length);
            });
        }

        [Fact]
        public void IncompleteObservationsAndCurrentPolicyRequireWarnings()
        {
            Assert.Throws<ArgumentException>(() => Metadata(Outcome.Succeeded, Execution.ReadOnly, completeness: Completeness.Partial));
            Assert.Throws<ArgumentException>(() => Metadata(policy: BehaviorPolicy.Current));
            var warnings = new[] { new Warning(WarningCode.UnverifiedBehavior, "Current policy.", Empty),
                new Warning(WarningCode.IncompleteData, "Limited fields.", Empty) };
            var envelope = Envelope.Create(new { Items = Array.Empty<object>() }, null,
                Metadata(Outcome.Succeeded, Execution.ReadOnly, completeness: Completeness.Partial, warnings: warnings, policy: BehaviorPolicy.Current));
            string expected = """{"schemaVersion":4,"ok":true,"data":{"items":[]},"error":null,"meta":{"timestamp":"2026-10-03T00:00:00Z","releaseKey":"21","tool":"GetPlcBlockInfo","requestId":"request-1","outcome":"succeeded","execution":"read-only","requiresSessionReset":false,"behaviorPolicy":"current","completeness":"partial","paging":null,"warnings":[{"code":"UNVERIFIED_BEHAVIOR","message":"Current policy.","details":{}},{"code":"INCOMPLETE_DATA","message":"Limited fields.","details":{}}]}}""";
            Golden(envelope, expected);
        }

        [Theory]
        [InlineData("\"schemaVersion\":4", "\"schemaVersion\":3")]
        [InlineData("\"ok\":false", "\"ok\":true")]
        [InlineData("\"data\":null", "\"data\":[]")]
        [InlineData("\"data\":null", "\"data\":\"{}\"")]
        [InlineData("\"data\":null", "\"data\":{\"x\":1,\"x\":2}")]
        [InlineData("\"data\":null", "\"data\":{\"x\":1e999}")]
        [InlineData("\"data\":null", "\"data\":{\"x\":NaN}")]
        [InlineData("\"schemaVersion\":4,", "")]
        [InlineData("\"data\":null,", "")]
        [InlineData("\"releaseKey\":\"21\",", "")]
        [InlineData("\"schemaVersion\":4,", "\"schemaVersion\":4,\"schemaVersion\":4,")]
        [InlineData("\"ok\":false", "\"Ok\":false")]
        [InlineData("\"ok\":false", "\"ok\":false,\"extra\":0")]
        [InlineData("\"outcome\":\"rejected-before-operation\"", "\"outcome\":\"REJECTED-BEFORE-OPERATION\"")]
        [InlineData("\"execution\":\"not-started\"", "\"execution\":0")]
        [InlineData("\"execution\":\"not-started\"", "\"execution\":\"completed\"")]
        [InlineData("\"code\":\"PROJECT_NOT_BOUND\"", "\"code\":\"NEW_ERROR\"")]
        [InlineData("\"code\":\"PROJECT_NOT_BOUND\"", "\"code\":\"project_not_bound\"")]
        [InlineData("\"details\":{}", "\"details\":{\"extra\":0}")]
        [InlineData("\"details\":{}", "\"details\":{\"code\":\"PROJECT_NOT_BOUND\"}")]
        [InlineData("\"details\":{}", "\"details\":null")]
        [InlineData("\"requestId\":\"request-1\"", "\"requestId\":\"\"")]
        [InlineData("\"timestamp\":\"2026-10-03T00:00:00Z\"", "\"timestamp\":\"2026-10-03T08:00:00+08:00\"")]
        public void InvalidWireIsRejected(string before, string after) =>
            Assert.NotNull(Record.Exception(() => V4Json.Deserialize<Envelope>(Rejected.Replace(before, after))));

        [Fact]
        public void InvalidCombinationsAreRejectedAtConstruction()
        {
            Assert.Throws<ArgumentException>(() => new Envelope(4, true, null, Reject().Error, Metadata()));
            Assert.Throws<ArgumentException>(() => Metadata(Outcome.Unknown, Execution.Unknown));
            Assert.Throws<ArgumentException>(() => Envelope.Create<object?>(null, Reject().Error, Metadata(Outcome.Partial, Execution.Partial)));
            Assert.Throws<ArgumentException>(() => Envelope.Create<object?>(null, new Error("Mixed.", new PartialFailureDetails(1, 1, 0)), Metadata()));
            Assert.Throws<ArgumentException>(() => Envelope.Create<object?>(null, new Error("Lost.", new OutcomeUnknownDetails(null, Empty)), Metadata()));
            Assert.Throws<ArgumentException>(() => Envelope.Create<object?>(null, new Error("Reset.", new SessionResetRequiredDetails(null)), Metadata()));
            Assert.Throws<ArgumentException>(() => Envelope.Create<object?>(null, new Error("Timeout.", new TimeoutDetails(null)), Metadata(Outcome.Failed, Execution.Completed)));
            Assert.Throws<ArgumentException>(() => Metadata((Outcome)99));
            Assert.Throws<ArgumentException>(() => Metadata(policy: (BehaviorPolicy)99));
            Assert.Throws<ArgumentException>(() => new PartialFailureDetails(-1, 0, 0));
            Assert.Throws<ArgumentException>(() => new PartialFailureDetails(1, 0, 0));
            Assert.Throws<ArgumentException>(() => new LimitExceededDetails(null, -1, 0));
            Assert.Throws<ArgumentException>(() => new NotExecutedDetails(-1));
            Assert.Throws<ArgumentException>(() => new PlanStaleDetails("not-a-hash", null));
            Assert.Throws<ArgumentException>(() => Envelope.Create(new[] { 1 }, null, Metadata(Outcome.Succeeded, Execution.ReadOnly)));
            Assert.Throws<ArgumentException>(() => new PlanIdentity(42, null, "project", 1, null, Array.Empty<PlanFile>()));
            Assert.Throws<ArgumentException>(() => new PlanIdentity(42, Stamp, "project", 1, "workspace", Array.Empty<PlanFile>()));
            Assert.Throws<ArgumentException>(() => new PlanFile("output", false, 1, Hash));
            Assert.Throws<ArgumentException>(() => new ExportHandle("export", "text/plain", -1, Hash, null));
            Assert.Throws<ArgumentException>(() => new Warning((WarningCode)99, "Warning.", Empty));
            Assert.NotNull(Record.Exception(() => V4Json.Serialize(new { Value = double.NaN })));
            Assert.NotNull(Record.Exception(() => V4Json.Serialize(new { Value = double.PositiveInfinity })));
            Assert.NotNull(Record.Exception(() => V4Json.Serialize(new { Value = double.NegativeInfinity })));
            Assert.Throws<ArgumentException>(() => V4Json.Serialize(new { Time = new DateTime(2026, 10, 3) }));
        }

        [Fact]
        public void PreviewIsReadOnlyAndRetainsPlanIdentity()
        {
            var plan = new Plan(Hash, "21", "GetPlcBlockInfo", Hash,
                new PlanIdentity(42, Stamp, "project.ap21", 7, null, Array.Empty<PlanFile>()),
                new Dictionary<string, string>(), null, Array.Empty<PlanOperation>(), Array.Empty<Warning>());
            var preview = new PreviewData(plan);
            var envelope = Envelope.Create(preview, null, Metadata(Outcome.Succeeded, Execution.ReadOnly));
            Assert.Equal(Hash, V4Json.ReadPreview(V4Json.Deserialize<Envelope>(V4Json.Serialize(envelope))).Plan.Hash);
            Assert.Throws<ArgumentException>(() => Envelope.Create(preview, null, Metadata(Outcome.Succeeded, Execution.Completed)));
            Assert.Throws<ArgumentException>(() => Envelope.Create(preview, Reject().Error, Metadata()));
        }

        public static IEnumerable<object[]> ExecutionPairs() =>
            from outcome in Enum.GetValues<Outcome>()
            from execution in Enum.GetValues<Execution>()
            select new object[] { outcome, execution };

        [Theory]
        [MemberData(nameof(ExecutionPairs))]
        public void EveryOutcomeExecutionPairIsValidated(Outcome outcome, Execution execution)
        {
            bool valid = (outcome, execution) switch
            {
                (Outcome.Succeeded, Execution.ReadOnly or Execution.Completed) => true,
                (Outcome.RejectedBeforeOperation, Execution.NotStarted) => true,
                (Outcome.ReadFailed, Execution.ReadOnly) => true,
                (Outcome.Failed, Execution.Completed) => true,
                (Outcome.Partial, Execution.Partial) => true,
                (Outcome.Unknown, Execution.Unknown) => true,
                _ => false
            };
            var exception = Record.Exception(() => Metadata(outcome, execution, outcome == Outcome.Unknown));
            Assert.Equal(valid, exception == null);
        }

        [Theory]
        [MemberData(nameof(ErrorGoldens))]
        public void ErrorDetailFieldsAreRequiredAndCannotBeSwapped(string culture, ErrorDetails details, string json)
        {
            InCulture(culture, () =>
            {
                using var document = JsonDocument.Parse(json);
                var original = document.RootElement.GetProperty("details");
                foreach (var field in original.EnumerateObject())
                {
                    var missing = System.Text.Json.Nodes.JsonNode.Parse(json)!;
                    missing["details"]!.AsObject().Remove(field.Name);
                    Assert.NotNull(Record.Exception(() => V4Json.Deserialize<Error>(missing.ToJsonString())));
                }
                if (details is ProjectNotBoundDetails) return;
                var wrong = System.Text.Json.Nodes.JsonNode.Parse(json)!;
                wrong["code"] = "PROJECT_NOT_BOUND";
                Assert.NotNull(Record.Exception(() => V4Json.Deserialize<Error>(wrong.ToJsonString())));
            });
        }

        [Theory]
        [InlineData(PagingMode.Offset, -1, 1, null, null, null, false)]
        [InlineData(PagingMode.Offset, 0, 0, null, null, null, true)]
        [InlineData(PagingMode.Offset, 0, 1, 0, null, null, false)]
        [InlineData(PagingMode.Offset, 0, 1, 1, null, null, true)]
        [InlineData(PagingMode.Offset, 0, 1, null, "cursor", null, true)]
        [InlineData(PagingMode.Cursor, 0, 1, null, null, null, true)]
        [InlineData(PagingMode.Cursor, null, 1, null, null, null, false)]
        [InlineData(PagingMode.Cursor, null, 1, null, "same", "same", false)]
        [InlineData(PagingMode.Cursor, null, 1, null, "a", "b", true)]
        public void InvalidPagingIsRejected(PagingMode mode, int? offset, int limit, int? nextOffset,
            string? cursor, string? nextCursor, bool complete) =>
            Assert.Throws<ArgumentException>(() => new Paging(mode, offset, limit, nextOffset, cursor, nextCursor, null, complete));

        [Fact]
        public void CursorScopesCannotCrossSessionBindingQueryOrSnapshot()
        {
            var scope = new CursorScope("21", "session", 1, Hash, "snapshot");
            Golden(scope, """{"releaseKey":"21","sessionId":"session","bindingEpoch":1,"queryHash":"HASH","snapshotId":"snapshot"}""".Replace("HASH", Hash));
            Assert.True(scope.Matches(new CursorScope("21", "session", 1, Hash, "snapshot")));
            foreach (var other in new[] { new CursorScope("20", "session", 1, Hash, "snapshot"),
                new CursorScope("21", "another", 1, Hash, "snapshot"), new CursorScope("21", "session", 2, Hash, "snapshot"),
                new CursorScope("21", "session", 1, new string('b', 64), "snapshot"), new CursorScope("21", "session", 1, Hash, "another") })
                Assert.False(scope.Matches(other));
        }

        [Fact]
        public void ModelsFreezeCollectionsAndJsonDocuments()
        {
            var warnings = new[] { new Warning(WarningCode.IncompleteData, "Limited.", Empty) };
            var meta = Metadata(Outcome.Succeeded, Execution.ReadOnly, warnings: warnings);
            warnings[0] = new Warning(WarningCode.NativeWarning, "Changed.", Empty);
            Assert.Equal(WarningCode.IncompleteData, meta.Warnings[0].Code);
            Assert.Throws<NotSupportedException>(() => ((IList<Warning>)meta.Warnings).Clear());
            var evidence = new Dictionary<string, JsonElement>();
            Error error;
            using (var document = JsonDocument.Parse("""{"executed":true}"""))
            {
                evidence["original"] = document.RootElement;
                error = new Error("Failed.", new NativeOperationFailedDetails(null, null, evidence));
            }
            evidence.Clear();
            Golden(error, """{"code":"NATIVE_OPERATION_FAILED","message":"Failed.","details":{"nativeCode":null,"nativeMessage":null,"evidence":{"original":{"executed":true}}}}""");
        }

        [Fact]
        public void NewTypesHaveNoSiemensOrMcpSdkReferences()
        {
            var assembly = typeof(Envelope).Assembly;
            Assert.DoesNotContain(assembly.GetReferencedAssemblies(), reference =>
                reference.Name!.StartsWith("Siemens", StringComparison.OrdinalIgnoreCase)
                || reference.Name.StartsWith("ModelContextProtocol", StringComparison.OrdinalIgnoreCase));
            var types = assembly.GetExportedTypes().Where(t => t.Namespace == "TiaMcp.Logic.V4").ToArray();
            Assert.Contains(typeof(Plan), types);
            Assert.Equal(25, Enum.GetValues<ErrorCode>().Length);
            Assert.Equal(7, Enum.GetValues<WarningCode>().Length);
            Assert.Equal(25, types.Count(t => t.BaseType == typeof(ErrorDetails)));
        }

        private static void InCulture(string culture, Action action)
        {
            var previous = CultureInfo.CurrentCulture;
            var previousUi = CultureInfo.CurrentUICulture;
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
                CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
                action();
            }
            finally
            {
                CultureInfo.CurrentCulture = previous;
                CultureInfo.CurrentUICulture = previousUi;
            }
        }
    }
}
