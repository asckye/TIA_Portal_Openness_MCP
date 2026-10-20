using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using TiaMcp.Logic.Generation;
using Xunit;

namespace TiaMcp.Generation.Tests
{
    // The 100 ms regex budget is CPU time of the matching thread: an expired wall-clock deadline that the match did not
    // use restarts it, while a pattern that uses the budget is still refused. Every pattern uses the same budgeted
    // backtracking engine on .NET Framework and .NET 10.
    public sealed class GenerationRegexTests
    {
        private static readonly string CatastrophicInput = new string('a', 40) + "!";
        private const string Refused = "Invalid or excessively expensive regular expression.";

        private static string Example(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Examples", name + ".json"));

        // The verdict of GenerationSchemas for one string value: match, mismatch or refused.
        private static string Verdict(string pattern, string value)
        {
            var schema = CanonicalJson.Parse("{\"type\":\"string\",\"pattern\":" + System.Text.Json.JsonSerializer.Serialize(pattern) + "}");
            try
            {
                var errors = GenerationSchemas.ValidateSchema(schema, CanonicalJson.Parse(System.Text.Json.JsonSerializer.Serialize(value)), "common.schema.json");
                return errors.Count == 0 ? "match" : errors.Single().Rule == "pattern" ? "mismatch" : "other";
            }
            catch (GenerationValidationException error) when (error.Errors.Any(e => e.Rule == "pattern" && e.Message == Refused)) { return "refused"; }
        }

        [Fact]
        public void ValidDocumentsValidateWhileEveryBudgetedDeadlineExpiresUnused()
        {
            using var starved = GenerationRegex.SimulateExpiredDeadlines(attemptsPerMatch: 3, cpuPerAttempt: TimeSpan.Zero);
            foreach (var name in GenerationTests.SchemaNames.Select(row => (string)row[0]))
                Assert.Empty(GenerationSchemas.Validate(name, CanonicalJson.Parse(Example(name))));
            // Every schema match is budgeted, so the simulation reached real schema matches.
            Assert.True(starved.ExpiredDeadlines >= 3, "expired deadlines: " + starved.ExpiredDeadlines);
        }

        [Fact]
        public void InvalidValuesAreStillRejectedWhileDeadlinesExpireUnused()
        {
            using var starved = GenerationRegex.SimulateExpiredDeadlines(attemptsPerMatch: 2, cpuPerAttempt: TimeSpan.Zero);
            var schema = CanonicalJson.Parse("{\"type\":\"string\",\"pattern\":\"^(?!/)[a-z]+$\"}");
            Assert.Empty(GenerationSchemas.ValidateSchema(schema, CanonicalJson.Parse("\"valid\""), "common.schema.json"));
            Assert.Contains(GenerationSchemas.ValidateSchema(schema, CanonicalJson.Parse("\"/rooted\""), "common.schema.json"), e => e.Rule == "pattern");
            Assert.Equal(4, starved.ExpiredDeadlines);
        }

        [Fact]
        public void ExpiredDeadlinesThatUseTheBudgetRefuseThePattern()
        {
            // Each attempt uses 60 ms of the thread's CPU time: the first restarts, the second exhausts the 100 ms budget.
            using var expensive = GenerationRegex.SimulateExpiredDeadlines(attemptsPerMatch: int.MaxValue, cpuPerAttempt: TimeSpan.FromMilliseconds(60));
            var schema = CanonicalJson.Parse("{\"type\":\"string\",\"pattern\":\"^[a-z]+$\"}");
            var refused = Assert.Throws<GenerationValidationException>(() => GenerationSchemas.ValidateSchema(schema, CanonicalJson.Parse("\"valid\""), "common.schema.json"));
            Assert.Contains(refused.Errors, e => e.Rule == "pattern" && e.Message == Refused);
            Assert.Equal(2, expensive.ExpiredDeadlines);
        }

        [Theory]
        [InlineData("^(a+)+$")]
        [InlineData("^(?!x)(a+)+$")]
        [InlineData("^(a|aa)+$")]
        public void CatastrophicBacktrackingPatternIsStillRefused(string pattern)
        {
            var clock = Stopwatch.StartNew();
            var refused = Assert.Throws<GenerationValidationException>(() =>
                GenerationSchemas.ValidateSchema(CanonicalJson.Parse("{\"type\":\"string\",\"pattern\":\"" + pattern + "\"}"),
                    CanonicalJson.Parse("\"" + CatastrophicInput + "\""), "common.schema.json"));
            Assert.Contains(refused.Errors, e => e.Path == "" && e.Rule == "pattern" && e.Message == Refused);
            // The refusal follows from the CPU budget, not from an unbounded wait.
            Assert.True(clock.Elapsed < TimeSpan.FromMinutes(1), clock.Elapsed.ToString());
            Assert.Throws<RegexMatchTimeoutException>(() => GenerationRegex.IsMatch(pattern, CatastrophicInput));
        }

        // Both targets compile the same GenerationRegex without a framework condition on the engine. This table states
        // the verdicts that net48 and net10 both give; the generation test projects build for net10.0 only, and a
        // probe built for both frameworks printed this same table. Both engines reduce `(a|a)+` to a single loop.
        [Theory]
        [InlineData("^(a+)+$", "aaaa", "match")]
        [InlineData("^(a+)+$", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa!", "refused")]
        [InlineData("^(a|a)+$", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa!", "mismatch")]
        [InlineData("^(?!/)(?!.*(?:^|/)\\.\\.?(/|$))(?!.*[\\\\:*?\"<>|\\x00-\\x1f])[^/]+(?:/[^/]+)*$", "parts/rules/motor.json", "match")]
        [InlineData("^(?!/)(?!.*(?:^|/)\\.\\.?(/|$))(?!.*[\\\\:*?\"<>|\\x00-\\x1f])[^/]+(?:/[^/]+)*$", "parts/../escape.json", "mismatch")]
        [InlineData("^[A-Za-z0-9][A-Za-z0-9_.-]*$", "step-1.a", "match")]
        [InlineData("^[A-Za-z0-9][A-Za-z0-9_.-]*$", "-step", "mismatch")]
        public void EveryPatternGetsTheSameVerdictOnEveryTarget(string pattern, string value, string verdict)
        {
            var regex = GenerationRegex.Get(pattern);
            Assert.Equal(GenerationRegex.Budget, regex.MatchTimeout);
            Assert.Equal(RegexOptions.CultureInvariant, regex.Options);
            Assert.Equal(verdict, Verdict(pattern, value));
        }

        [Fact]
        public void InvalidPatternsAreRefusedAndBundledPatternsAreCompiledOnce()
        {
            var refused = Assert.Throws<GenerationValidationException>(() =>
                GenerationSchemas.ValidateSchema(CanonicalJson.Parse("{\"type\":\"string\",\"pattern\":\"(\"}"), CanonicalJson.Parse("\"x\""), "common.schema.json"));
            Assert.Contains(refused.Errors, e => e.Rule == "pattern" && e.Message == Refused);
            Assert.NotEmpty(GenerationSchemas.Names);
            var first = GenerationRegex.Get("^[A-Za-z0-9][A-Za-z0-9_.-]*$");
            Assert.Same(first, GenerationRegex.Get("^[A-Za-z0-9][A-Za-z0-9_.-]*$"));
        }

        [Fact]
        public void SclDeclarationsAndMembersParseWhileDeadlinesExpireUnused()
        {
            var source = "FUNCTION_BLOCK \"Motor\" // note\n{ S7_Optimized_Access := 'TRUE' }\nVAR_INPUT\n  \"Start\" : Bool; (* x *)\n  b : Int := 3;\nEND_VAR\n"
                + "BEGIN\n  s := 'it''s';\n /* multi\n line */\nEND_FUNCTION_BLOCK\nTYPE \"T\"\nSTRUCT\n  x : Bool;\nEND_STRUCT;\nEND_TYPE\n";
            using var starved = GenerationRegex.SimulateExpiredDeadlines(attemptsPerMatch: 3, cpuPerAttempt: TimeSpan.Zero);
            var declarations = GenerationScl.Declarations(Encoding.UTF8.GetBytes(source));
            Assert.Equal(new[] { "FB:Motor", "UDT:T" }, declarations.Select(d => d.Kind + ":" + d.Name).ToArray());
            Assert.Equal(new[] { "Start", "b", "x" }, GenerationScl.Members(source).Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray());
            Assert.True(starved.ExpiredDeadlines >= 3, "expired deadlines: " + starved.ExpiredDeadlines);
        }
    }
}
