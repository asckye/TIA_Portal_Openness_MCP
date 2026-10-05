using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using TiaMcp.Logic.V4;
using TiaMcpServer.Cli;
using Xunit;

namespace TiaMcpServer.Tests
{
    public sealed class CliV4Tests
    {
        public static IEnumerable<object[]> Goldens()
        {
            var assembly = typeof(CliV4Tests).Assembly;
            using var stream = assembly.GetManifestResourceStream(assembly.GetManifestResourceNames().Single(n => n.EndsWith("Golden.CliV4.json")))!;
            using var document = JsonDocument.Parse(stream);
            foreach (var sample in document.RootElement.EnumerateArray()) yield return new object[] { sample.Clone() };
        }

        [Theory, MemberData(nameof(Goldens))]
        public void ExitCodesAndOutputChannelsMatchGolden(JsonElement sample)
        {
            using var stdout = new StringWriter { NewLine = "\n" };
            using var stderr = new StringWriter { NewLine = "\n" };
            int expected = sample.GetProperty("exitCode").GetInt32();
            int actual = expected >= 64
                ? CliBoundary.Failure(expected == 64 ? CliFailure.Syntax : CliFailure.ContextCreation,
                    new InvalidOperationException(sample.GetProperty("diagnostic").GetString()), stderr)
                : CliBoundary.Write(V4Json.Deserialize<Envelope>(sample.GetProperty("stdout").GetString()!), stdout);
            Assert.Equal(expected, actual);
            Assert.Equal(sample.GetProperty("stdout").GetString(), stdout.ToString());
            Assert.Equal(sample.GetProperty("stderr").GetString(), stderr.ToString());
        }

        [Theory]
        [InlineData("unknown")]
        [InlineData("compile")]
        [InlineData("export", "p.ap21", "--out", "folder")]
        [InlineData("import", "p.ap21", "--from")]
        [InlineData("gen", "p.json", "--dry-run", "false")]
        [InlineData("compile", "p.ap21", "--typo")]
        [InlineData("compile", "p.ap21", "--logging", "4")]
        [InlineData("compile", "p.ap21", "--tia-version")]
        [InlineData("compile", "p.ap21", "--tia-version", "22")]
        [InlineData("compile", "p.ap21", "--plc", "")]
        [InlineData("compile", "p.ap21", "--plc", "--json")]
        [InlineData("compile", "p.ap21", "--plc", "P", "--plc", "Q")]
        [InlineData("compile", "p.ap21", "--profile", "bad")]
        [InlineData("compile", "p.ap21", "--worker-timeout-seconds", "9")]
        public void SyntaxIsRejectedBeforeAnyContextIsNeeded(params string[] args)
            => Assert.ThrowsAny<ArgumentException>(() => CliBoundary.Validate(args));

        [Theory]
        [InlineData("gen", "--dry-run", "p.json")]
        [InlineData("compile", "--plc", "PLC", "p.ap21")]
        [InlineData("import", "--from", "directory", "p.ap21")]
        [InlineData("export", "p.ap21", "--out", "directory", "--block", "Group/FB")]
        public void OptionsDoNotBecomeThePositionalPath(params string[] args)
        {
            CliBoundary.Validate(args);
            Assert.StartsWith("p.", CliBoundary.PathArgument(args));
        }

        [Fact]
        public void ServerModeIsNotCliSyntax()
        {
            Assert.False(CliBoundary.TryValidate(Array.Empty<string>(), out _));
            Assert.False(CliBoundary.TryValidate(new[] { "--transport", "http" }, out _));
        }

        [Fact]
        public void HelpAliasRoutingIsCaseInsensitive()
        {
            Assert.True(CliBoundary.TryValidate(new[] { "--HELP", "--typo" }, out int exit));
            Assert.Equal(64, exit);
        }

        [Fact]
        public void FailedContextDoesNotRunAToolOrEmitAnEnvelope()
        {
            using var stderr = new StringWriter();
            var output = Console.Out;
            bool called = false;
            Assert.Equal(70, CliBoundary.RunContext(new[] { "gen", "p.json" },
                () => { Console.WriteLine("setup diagnostic"); throw new InvalidOperationException("context failed"); },
                () => { called = true; return 0; }, stderr));
            Assert.False(called);
            Assert.Same(output, Console.Out);
            Assert.Equal("setup diagnostic" + Environment.NewLine + "ERROR: context failed" + Environment.NewLine, stderr.ToString());
            Assert.Throws<InvalidOperationException>(() => CliBoundary.RunContext(new[] { "config" },
                () => throw new InvalidOperationException("unchanged"), () => 0, stderr));
        }

        [Theory]
        [InlineData(Completeness.Partial, 4)]
        [InlineData(Completeness.Unknown, 5)]
        [InlineData(Completeness.None, 3)]
        public void IncompleteObservationDoesNotExitAsSuccess(Completeness completeness, int exit)
        {
            var result = Sample(Outcome.Succeeded, completeness);
            Assert.Equal(exit, CliBoundary.Write(result, new StringWriter()));
        }

        [Fact]
        public void AggregationRetainsEvidenceAndUnknownWins()
        {
            var ok = Sample(Outcome.Succeeded);
            var failed = Sample(Outcome.ReadFailed);
            var partial = CliBoundary.Combine("import", new[] { ok, failed });
            Assert.Equal(Outcome.Partial, partial.Meta.Outcome);
            Assert.Equal(2, partial.Data!.Value.GetProperty("items").GetArrayLength());
            var unknown = CliBoundary.Combine("import", new[] { partial, Sample(Outcome.Unknown) });
            Assert.Equal(Outcome.Unknown, unknown.Meta.Outcome);
            Assert.True(unknown.Meta.RequiresSessionReset);
            Assert.Equal(5, CliBoundary.Write(unknown, new StringWriter()));
        }

        [Fact]
        public void OfflineReportAnalyzerReceivesDataAndTemporaryFileIsRemovedOnFailure()
        {
            string path = Path.GetTempFileName();
            string? bodyPath = null;
            try
            {
                File.WriteAllText(path, V4Json.Serialize(Sample(Outcome.Succeeded)));
                Assert.Throws<IOException>(() => CliReportInput.Read<string>(path, input =>
                {
                    bodyPath = input;
                    using var body = JsonDocument.Parse(File.ReadAllText(input));
                    Assert.Equal("Retained evidence", body.RootElement.GetProperty("summary").GetString());
                    Assert.False(body.RootElement.TryGetProperty("schemaVersion", out _));
                    throw new IOException("analyzer failure");
                }));
                Assert.False(File.Exists(bodyPath));
                Assert.True(File.Exists(path));
                File.WriteAllText(path, "{\"probe\":{}}");
                Assert.Equal(path, CliReportInput.Read(path, input => input));
            }
            finally { File.Delete(path); }
        }

        [Theory]
        [InlineData(Outcome.Succeeded, Completeness.Partial)]
        [InlineData(Outcome.Succeeded, Completeness.Unknown)]
        [InlineData(Outcome.Unknown, Completeness.Unknown)]
        public void OfflineReportAnalyzerCannotPromoteIncompleteEvidence(Outcome outcome, Completeness completeness)
        {
            string path = Path.GetTempFileName();
            try
            {
                File.WriteAllText(path, V4Json.Serialize(Sample(outcome, completeness)));
                bool called = false;
                Assert.Throws<InvalidDataException>(() => CliReportInput.Read(path, input => { called = true; return input; }));
                Assert.False(called);
            }
            finally { File.Delete(path); }
        }

        private static Envelope Sample(Outcome outcome, Completeness completeness = Completeness.Complete)
        {
            Error? error = outcome == Outcome.Succeeded ? null : outcome == Outcome.Unknown
                ? new Error("Unconfirmed.", new OutcomeUnknownDetails("write", new Dictionary<string, JsonElement>()))
                : new Error("Read failed.", new InternalErrorDetails(null));
            return Envelope.Create(new { summary = "Retained evidence" }, error,
                new Meta(DateTimeOffset.UtcNow, "21", "Sample", "test", outcome,
                    outcome == Outcome.Unknown ? Execution.Unknown : Execution.ReadOnly, outcome == Outcome.Unknown,
                    BehaviorPolicy.NotApplicable, completeness, null,
                    completeness == Completeness.Partial ? new[] { new Warning(WarningCode.IncompleteData, "Incomplete.", new Dictionary<string, JsonElement>()) } : Array.Empty<Warning>()));
        }
    }
}
