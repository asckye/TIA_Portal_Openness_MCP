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
    }
}
