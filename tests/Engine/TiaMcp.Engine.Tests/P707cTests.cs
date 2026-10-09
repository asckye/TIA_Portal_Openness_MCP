using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Logic.V4;
using TiaMcpServer.ModelContextProtocol;
using Xunit;

namespace TiaMcp.Engine.Tests
{
    public sealed class P707cTests
    {
        // Independent expectations include SDK values that do not prove completion.
        public static IEnumerable<object[]> States()
        {
            foreach (string type in new[] { NativeResultStates.Compiler, NativeResultStates.Download, NativeResultStates.Upload, NativeResultStates.Cax, NativeResultStates.TestSuite })
                foreach (var row in Rows(type, "Success,Information", "Error")) yield return row;
            foreach (string type in new[] { NativeResultStates.AlarmTexts, NativeResultStates.TextLists })
                foreach (var row in Rows(type, "OK", "Error")) yield return row;
            foreach (string type in new[] { NativeResultStates.AlarmClasses, NativeResultStates.SystemDiagnostics })
                foreach (var row in Rows(type, "Success", "Error")) yield return row;
            foreach (var row in Rows(NativeResultStates.ProjectTexts, "Info", "Error")) yield return row;
            foreach (var row in Rows(NativeResultStates.Library, "Success", "")) yield return row;
            foreach (var row in Rows(NativeResultStates.Layout, "Success", "", unknown: "None")) yield return row;
            foreach (var row in Rows(NativeResultStates.Unified, "Success", "Error", unknown: "None")) yield return row;
            foreach (var row in Rows(NativeResultStates.Documents, "Success", "Failure", partial: "PartialSuccess", warning: false)) yield return row;
            foreach (var row in Rows(NativeResultStates.SupervisionXlsx, "Success", "Failure", warning: false)) yield return row;
            foreach (var row in Rows(NativeResultStates.SupervisionSettings, "Success", "ErrorRollback", warning: false)) yield return row;
            foreach (var row in Rows(NativeResultStates.SafetyTest, "Succeeded", "Failed", unknown: "NotTested", warning: false)) yield return row;
            foreach (var row in Rows(NativeResultStates.SafetyValidation, "Okay", "Error", warning: false)) yield return row;
        }

        private static IEnumerable<object[]> Rows(string type, string successes, string failures, string partial = "", string unknown = "", bool warning = true)
        {
            foreach (string state in successes.Split(',').Where(s => s != "")) yield return new object[] { type, state, NativeStateKind.Success };
            foreach (string state in failures.Split(',').Where(s => s != "")) yield return new object[] { type, state, NativeStateKind.Failure };
            foreach (string state in partial.Split(',').Where(s => s != "")) yield return new object[] { type, state, NativeStateKind.Partial };
            foreach (string state in unknown.Split(',').Where(s => s != "")) yield return new object[] { type, state, NativeStateKind.Unexpected };
            if (warning) yield return new object[] { type, "Warning", NativeStateKind.Warning };
            yield return new object[] { type, "987654", NativeStateKind.Unexpected };
        }

        [Theory]
        [MemberData(nameof(States))]
        public void EverySdkValueHasAnExplicitMapping(string type, string state, NativeStateKind expected)
        {
            Assert.Equal(expected, NativeResultStates.Classify(type, state));
            Assert.Equal(expected is NativeStateKind.Success or NativeStateKind.Warning, NativeResultStates.Succeeded(type, state));
        }

        [Theory]
        [MemberData(nameof(States))]
        public void EveryValueFlowsThroughEachContract(string type, string state, NativeStateKind expected)
        {
            foreach (bool changesProject in new[] { false, true })
            {
                var evidence = new JsonObject { ["success"] = expected is NativeStateKind.Success or NativeStateKind.Warning,
                    ["mayHaveWrittenFiles"] = !changesProject, ["nativeStateType"] = type };
                NativeResultState.Record(evidence, state, changesProject, "native.log", messages: new JsonArray("SDK diagnostic"));
                var response = new ResponseMessage { Message = "Native result returned", Meta = evidence };
                foreach (var result in new[] {
                    EngineeringToolContract.Map("ExportAlarmInstanceTexts", response, true),
                    LibraryToolContract.Map("ExchangeSivarcScreenLayouts", response, true),
                    OptionalPackageContract.Map("ExchangePlcSupervisions", response, true),
                    RuntimeToolContract.Map("DownloadPlcToFolder", response, false, true),
                    PlcToolContract.Map("ManagePlcBlockDocuments", response, true, true) })
                {
                    var body = result.StructuredContent!.AsObject();
                    bool success = expected is NativeStateKind.Success or NativeStateKind.Warning;
                    bool unknown = expected is NativeStateKind.Unexpected or NativeStateKind.Partial || expected == NativeStateKind.Failure && changesProject;
                    Assert.Equal(success ? "succeeded" : unknown ? "unknown" : "failed", (string?)body["meta"]?["outcome"]);
                    Assert.Equal(unknown, (bool?)body["meta"]?["requiresSessionReset"]);
                    Assert.Equal(success ? null : unknown ? "OUTCOME_UNKNOWN" : "NATIVE_OPERATION_FAILED", (string?)body["error"]?["code"]);
                    if (!success) Assert.Equal(state, (string?)body["error"]?["details"]?["evidence"]?["nativeState"]);
                    Assert.Equal(expected == NativeStateKind.Warning, body["meta"]!["warnings"]!.AsArray().Any(w => (string?)w?["code"] == "NATIVE_WARNING"));
                }
            }
        }

        [Fact]
        public void ExactV21VmFinding52ReturnsSuccessAndLeavesSessionUsable()
        {
            string directory = Path.Combine(Path.GetTempPath(), "p707c-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                string path = Path.Combine(directory, "AlarmInstanceTexts.xlsx");
                File.WriteAllBytes(path, new byte[4038]);
                var evidence = new JsonObject { ["state"] = "OK", ["success"] = true,
                    ["nativeResultReturned"] = true, ["nativeState"] = "OK", ["mayHaveChanged"] = false,
                    ["targetFile"] = NativeResultState.FileRow(path) };
                var body = EngineeringToolContract.Map("ExportAlarmInstanceTexts", new ResponseMessage { Meta = evidence }, true).StructuredContent!;
                Assert.True((bool)body["ok"]!);
                Assert.Null(body["error"]);
                Assert.Equal("succeeded", (string?)body["meta"]?["outcome"]);
                Assert.False((bool)body["meta"]!["requiresSessionReset"]!);
                Assert.Equal(4038, (long?)body["data"]?["evidence"]?["targetFile"]?["sizeBytes"]);
            }
            finally { Directory.Delete(directory, true); }
        }

        [Fact]
        public void SuccessSpellingFromAnotherEnumAndNullDoNotProveCompletion()
        {
            foreach (string? state in new[] { "OK", "Info", "Failed", null })
            {
                var evidence = new JsonObject { ["nativeStateType"] = NativeResultStates.Compiler };
                NativeResultState.Record(evidence, state, false);
                Assert.True(NativeResultState.TryUnsuccessful(evidence, true, out var outcome, out var error));
                Assert.Equal(Outcome.Unknown, outcome);
                Assert.Equal(ErrorCode.OutcomeUnknown, error!.Code);
            }
        }

        [Theory]
        [InlineData("Failure")]
        [InlineData("Failed")]
        [InlineData("Success")]
        [InlineData("Information")]
        public void LegacyAlarmEvidenceCannotBorrowValuesFromAnotherEnum(string state)
        {
            var evidence = new JsonObject(); NativeResultState.Record(evidence, state, false);
            Assert.True(NativeResultState.TryUnsuccessful(evidence, true, out var outcome, out var error, "ExportAlarmInstanceTexts"));
            Assert.Equal(Outcome.Unknown, outcome);
            var body = JsonNode.Parse(V4Json.Serialize(error))!;
            Assert.Equal(state, (string?)body["details"]?["evidence"]?["nativeState"]);
            Assert.Equal(NativeResultStates.AlarmTexts, (string?)body["details"]?["evidence"]?["nativeStateType"]);
        }

        [Theory]
        [InlineData("Success", true)]
        [InlineData("Information", true)]
        [InlineData("Warning", true)]
        [InlineData("Error", false)]
        [InlineData("987654", false)]
        public void CompilerDiagnosticsUsesAllSdkValues(string state, bool success)
        {
            var diagnostics = new CompilerDiagnostics.CompilerMessageCollectResult();
            var summary = diagnostics.Summary(state, 0, 0);
            Assert.Equal(success, (bool?)summary["success"]);
            Assert.Equal(state, (string?)summary["rootState"]);
        }

        [Theory]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public void CompilerDiagnosticsCannotReplaceAnUnknownRootWithChildSeverity(bool error, bool warning)
        {
            var diagnostics = new CompilerDiagnostics.CompilerMessageCollectResult { HasError = error, HasWarning = warning };
            var summary = diagnostics.Summary("987654", 0, 0);
            Assert.False((bool)summary["success"]!);
            Assert.Equal("987654", (string?)summary["effectiveState"]);
            var body = PlcToolContract.Map("CompilePlcSoftware", new ResponseMessage { Meta = summary }, true, true).StructuredContent!;
            Assert.Equal("unknown", (string?)body["meta"]?["outcome"]);
            Assert.True((bool)body["meta"]!["requiresSessionReset"]!);
            Assert.Equal("987654", (string?)body["error"]?["details"]?["evidence"]?["nativeState"]);
        }
    }
}
