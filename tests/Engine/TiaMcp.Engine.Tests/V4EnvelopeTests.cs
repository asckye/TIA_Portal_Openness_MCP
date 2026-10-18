using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using TiaMcp.Logic.V4;
using Xunit;

namespace TiaMcp.Engine.Tests
{
    public sealed class V4EnvelopeTests
    {
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Returned_compile_errors_are_completed_and_preserve_diagnostic_coverage(bool incomplete)
        {
            var response = new System.Text.Json.Nodes.JsonObject { ["state"] = "Error", ["errorCount"] = 6, ["warningCount"] = 0,
                ["errors"] = new System.Text.Json.Nodes.JsonArray("State=Error; Path=ASIS (DB1); Description=Drive telegram address missing."),
                ["meta"] = new System.Text.Json.Nodes.JsonObject { ["incomplete"] = incomplete } };
            var result = TiaMcpServer.ModelContextProtocol.PlcToolContract.Map("CompilePlcSoftware", response, true, true);
            var body = result.StructuredContent!;
            Assert.True(result.IsError);
            Assert.Equal("COMPILE_ERRORS", (string?)body["error"]?["code"]);
            Assert.Equal("completed", (string?)body["meta"]?["execution"]);
            Assert.Equal("failed", (string?)body["meta"]?["outcome"]);
            Assert.Equal(incomplete ? "partial" : "complete", (string?)body["meta"]?["completeness"]);
            Assert.Equal(6, (int?)body["error"]?["details"]?["errorCount"]);
            Assert.Equal("ASIS (DB1)", (string?)body["error"]?["details"]?["errors"]?[0]?["path"]);
            Assert.Equal(response["errors"]!.ToJsonString(), body["data"]!["errors"]!.ToJsonString());
        }


        private const string Hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        private static readonly DateTimeOffset Stamp = new DateTimeOffset(2026, 10, 3, 0, 0, 0, TimeSpan.Zero);
        private static readonly IReadOnlyDictionary<string, JsonElement> Empty = new Dictionary<string, JsonElement>();
        private const string Rejected = """{"schemaVersion":4,"ok":false,"data":null,"error":{"code":"PROJECT_NOT_BOUND","message":"No project is bound.","details":{}},"meta":{"timestamp":"2026-10-03T00:00:00.0000000Z","releaseKey":"21","tool":"GetPlcBlockInfo","requestId":"request-1","outcome":"rejected-before-operation","execution":"not-started","requiresSessionReset":false,"behaviorPolicy":"not-applicable","completeness":"none","paging":null,"warnings":[]}}""";

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
                (new InvalidArgumentDetails("mode", new[] { "preview", "apply" }), """{"code":"INVALID_ARGUMENT","message":"Failure.","details":{"parameter":"mode","allowedValues":["preview","apply"],"parameters":["mode"]}}"""),
                (new LimitExceededDetails("items", 1000, 1001), """{"code":"LIMIT_EXCEEDED","message":"Failure.","details":{"parameter":"items","limit":1000,"actual":1001}}"""),
                (new UnsupportedCapabilityDetails("14sp1", "compile", null), """{"code":"UNSUPPORTED_CAPABILITY","message":"Failure.","details":{"releaseKey":"14sp1","capability":"compile","action":null}}"""),
                (new ToolNotFoundDetails("MissingTool"), """{"code":"TOOL_NOT_FOUND","message":"Failure.","details":{"tool":"MissingTool"}}"""),
                (new ProjectNotBoundDetails(), """{"code":"PROJECT_NOT_BOUND","message":"Failure.","details":{}}"""),
                (new NotFoundDetails("Block"), """{"code":"NOT_FOUND","message":"Failure.","details":{"target":"Block"}}"""),
                (new TargetAmbiguousDetails("PLC", new[] { "PLC_A", "PLC_B" }), """{"code":"TARGET_AMBIGUOUS","message":"Failure.","details":{"target":"PLC","candidates":["PLC_A","PLC_B"]}}"""),
                (new IdentityMismatchDetails("project", "epoch:1", "epoch:2"), """{"code":"IDENTITY_MISMATCH","message":"Failure.","details":{"target":"project","expected":"epoch:1","actual":"epoch:2"}}"""),
                (new AlreadyExistsDetails("Block"), """{"code":"ALREADY_EXISTS","message":"Failure.","details":{"target":"Block"}}"""),
                (new ConfirmationRequiredDetails(Hash), """{"code":"CONFIRMATION_REQUIRED","message":"Failure.","details":{"reason":"plan-confirmation","planHash":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","requestId":null}}"""),
                (new PlanStaleDetails(Hash, "input changed"), """{"code":"PLAN_STALE","message":"Failure.","details":{"planHash":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","reason":"input-changed","previewToken":null}}"""),
                (new PreconditionFailedDetails("borrowed", "project"), """{"code":"PRECONDITION_FAILED","message":"Failure.","details":{"condition":"borrowed","target":"project","parameter":null}}"""),
                (new OfflineRequiredDetails(new[] { "PLC_A" }), """{"code":"OFFLINE_REQUIRED","message":"Failure.","details":{"targets":["PLC_A"]}}"""),
                (new AuthenticationRequiredDetails("Safety"), """{"code":"AUTHENTICATION_REQUIRED","message":"Failure.","details":{"capability":"Safety"}}"""),
                (new AccessDeniedDetails("write", "Block"), """{"code":"ACCESS_DENIED","message":"Failure.","details":{"operation":"write","target":"Block"}}"""),
                (new SessionResetRequiredDetails("channel closed"), """{"code":"SESSION_RESET_REQUIRED","message":"Failure.","details":{"reason":"channel-closed"}}"""),
                (new ResourceUnavailableDetails("SDK"), """{"code":"RESOURCE_UNAVAILABLE","message":"Failure.","details":{"resource":"sdk"}}"""),
                (new IoFailedDetails("read", "input.xml"), """{"code":"IO_FAILED","message":"Failure.","details":{"operation":"read","path":"input.xml"}}"""),
                (new NativeOperationFailedDetails("E1", "Native failure", Evidence()), """{"code":"NATIVE_OPERATION_FAILED","message":"Failure.","details":{"nativeCode":"E1","nativeMessage":"Native failure","evidence":{"Executed":true,"observation":{"source":"readback","nativeResult":null}}}}"""),
                (new CompileErrorsDetails(6, 0, new[] { new CompileErrorMessage("ASIS (DB1)", "Drive telegram address missing.") }), """{"code":"COMPILE_ERRORS","message":"Failure.","details":{"errorCount":6,"warningCount":0,"errors":[{"path":"ASIS (DB1)","description":"Drive telegram address missing."}]}}"""),
                (new CancelledDetails("before dispatch"), """{"code":"CANCELLED","message":"Failure.","details":{"stage":"before-dispatch"}}"""),
                (new TimeoutDetails("read"), """{"code":"TIMEOUT","message":"Failure.","details":{"stage":"read"}}"""),
                (new NotExecutedDetails(0), """{"code":"NOT_EXECUTED","message":"Failure.","details":{"causeIndex":0}}"""),
                (new PartialFailureDetails(1, 1, 1), """{"code":"PARTIAL_FAILURE","message":"Failure.","details":{"succeeded":1,"failed":1,"notExecuted":1}}"""),
                (new OutcomeUnknownDetails("write", Evidence()), """{"code":"OUTCOME_UNKNOWN","message":"Failure.","details":{"stage":"write","evidence":{"Executed":true,"observation":{"source":"readback","nativeResult":null}},"reason":null}}"""),
                (new InternalErrorDetails("diagnostic-1"), """{"code":"INTERNAL_ERROR","message":"Failure.","details":{"diagnosticId":"diagnostic-1"}}"""),
            };
            foreach (var culture in new[] { "en-US", "zh-CN" })
                foreach (var sample in samples) yield return new object[] { culture, sample.Details, sample.Json };
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













        public static IEnumerable<object[]> ExecutionPairs() =>
            from outcome in Enum.GetValues<Outcome>()
            from execution in Enum.GetValues<Execution>()
            select new object[] { outcome, execution };







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
