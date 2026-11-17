using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace TiaMcp.Logic.Testing
{
    // Report content is independent of MCP, audit storage and Siemens assemblies.
    public sealed class PlcTestReport
    {
        public string Markdown { get; }
        public JsonObject Document { get; }
        internal PlcTestReport(string markdown, JsonObject document) { Markdown = markdown; Document = document; }
    }

    public static class PlcTestReportBuilder
    {
        public static PlcTestReport Build(PlcTestRunRecord run, string format, JsonObject audit,
            IReadOnlyDictionary<string, string> documents, Func<JsonNode?, JsonNode?> redactArguments)
        {
            if (format != "fat" && format != "sat" && format != "repro") throw new ArgumentException("Unknown report format.");
            string title = format == "fat" ? "Factory Acceptance Test" : format == "sat" ? "Site Acceptance Test" : "PLC Test Reproduction Report";
            var first = run.Cases.FirstOrDefault(c => c.Kind == "plcsimScenario");
            var environment = new JsonObject {
                ["releaseKey"] = run.ReleaseKey, ["hostVersion"] = run.HostVersion,
                ["projectFile"] = FileName((string?)run.Project?["file"] ?? ""),
                ["plcsimApi"] = Safe(first?.Evidence["api"])
            };
            var report = new JsonObject {
                ["title"] = title, ["runId"] = run.RunId, ["status"] = run.Status,
                ["startedUtc"] = PlcTestRunRecord.FormatUtc(run.StartedUtc),
                ["finishedUtc"] = run.FinishedUtc.HasValue ? PlcTestRunRecord.FormatUtc(run.FinishedUtc.Value) : null,
                ["environment"] = environment, ["librarySha256"] = LibraryHash(run),
                ["totals"] = run.Totals, ["exitCode"] = run.ExitCode
            };
            var text = new StringBuilder().Append("# ").Append(title).Append("\n\n");
            if (run.Status != "completed") text.Append("**INCOMPLETE RUN - not valid for acceptance**\n\n");
            foreach (var pair in report.Where(p => p.Key != "environment" && p.Key != "totals"))
                text.Append(pair.Key).Append(": ").Append(Cell(pair.Value?.ToString() ?? "")).Append('\n');
            text.Append("\nEnvironment:\n\n");
            Code(text, environment.ToJsonString(), "json");
            text.Append("\nTotals: ").Append(run.Totals.ToJsonString()).Append("\n\n");
            var cases = new JsonArray();
            report["cases"] = cases;
            if (format == "repro")
            {
                foreach (var item in run.Cases.Where(c => c.Verdict != "passed"))
                {
                    if (!documents.TryGetValue(item.Id, out string? document))
                        throw new ArgumentException("Original case document is unavailable: " + item.Id);
                    var original = PlcTestCaseDocument.Parse(document);
                    if (original.Id != item.Id || original.Sha256 != item.CaseSha256)
                        throw new ArgumentException("Stored case differs from the run; restore the original case: " + item.Id);
                    // A verbatim reproduction must not silently change path-bearing input.
                    if (!JsonNode.DeepEquals(original.Document, Safe(original.Document)))
                        throw new ArgumentException("Case contains absolute paths; a verbatim path-free reproduction is unavailable: " + item.Id);
                    var body = JsonNode.Parse(document)!;
                    var args = item.Kind == "plcsimScenario"
                        ? new JsonObject { ["scenario"] = body["scenario"]!.DeepClone(), ["confirmRun"] = true, ["dryRun"] = false }
                        : SuiteArguments(body["testSuite"]!.AsObject());
                    var row = Case(item);
                    row["testCase"] = body.DeepClone();
                    row["caseDocument"] = document;
                    row["call"] = new JsonObject {
                        ["name"] = item.Kind == "plcsimScenario" ? "RunPlcSimAdvancedTestScenario" : "RunTestSuiteCase",
                        ["arguments"] = Safe(redactArguments(args))
                    };
                    cases.Add(row);
                    text.Append("## ").Append(Cell(item.Id)).Append("\n\n");
                    Code(text, document, "json");
                    Code(text, row["call"]!.ToJsonString(), "json");
                    Code(text, row["details"]!.ToString(), "text");
                    text.Append("Verdict: ").Append(item.Verdict).Append("\n\nMessage: ").Append(Cell(SafeText(item.Message))).Append("\n\n");
                }
                if (cases.Count == 0) text.Append("All cases passed. No reproduction cases.\n");
            }
            else
            {
                report["audit"] = Safe(audit);
                text.Append("## Audit binding\n\n");
                Code(text, report["audit"]!.ToJsonString(), "json");
                text.Append("verify with `tia audit verify`\n\n");
                text.Append("| Id | Requirement | Title | Verdict | Duration (ms) | Message |\n|---|---|---|---|---|---|\n");
                foreach (var item in run.Cases)
                {
                    cases.Add(Case(item));
                    text.Append("| ").Append(string.Join(" | ", new[] { item.Id, item.Requirement ?? "", item.Title, item.Verdict,
                        item.DurationMs.ToString(CultureInfo.InvariantCulture), SafeText(item.Message) }.Select(Cell))).Append(" |\n");
                }
                foreach (var item in run.Cases.Where(c => c.Verdict != "passed"))
                {
                    text.Append("\n## ").Append(Cell(item.Id)).Append("\n\n");
                    Code(text, Details(item), "text");
                }
                report["signOff"] = new JsonObject { ["supplier"] = SignOff(), ["customer"] = SignOff(), ["acceptedDeviations"] = "" };
                text.Append("\n## Sign-off\n\n| Party | Name | Role | Date | Signature |\n|---|---|---|---|---|\n")
                    .Append("| Supplier | | | | |\n| Customer | | | | |\n\nAccepted deviations: ____________________\n");
            }
            return new PlcTestReport(text.ToString(), report);
        }

        private static JsonObject SignOff() => new JsonObject { ["name"] = "", ["role"] = "", ["date"] = "", ["signature"] = "" };
        private static JsonObject Case(PlcTestCaseResult item) => new JsonObject {
            ["id"] = item.Id, ["requirement"] = item.Requirement, ["title"] = SafeText(item.Title), ["verdict"] = item.Verdict,
            ["durationMs"] = item.DurationMs, ["message"] = SafeText(item.Message),
            ["details"] = Details(item)
        };
        private static string Details(PlcTestCaseResult item)
        {
            string details = PlcTestJUnitWriter.FailureDetails(item);
            return SafeText(string.IsNullOrWhiteSpace(details) ? item.Message : details);
        }
        private static JsonObject SuiteArguments(JsonObject selection) => new JsonObject {
            ["category"] = selection["category"]!.DeepClone(), ["kind"] = selection["kind"]?.DeepClone() ?? JsonValue.Create("case"),
            ["names"] = selection["names"]?.DeepClone() ?? new JsonArray(), ["runAll"] = selection["runAll"]?.DeepClone() ?? JsonValue.Create(false),
            ["confirmExternalExecution"] = true, ["dryRun"] = false
        };
        public static string LibraryHash(PlcTestRunRecord run)
        {
            string lines = string.Join("\n", run.Cases.OrderBy(c => c.Id, StringComparer.Ordinal).Select(c => c.Id + ":" + c.CaseSha256));
            using var sha = SHA256.Create();
            return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(lines))).Replace("-", "").ToLowerInvariant();
        }
        private static string FileName(string path) => path.Replace('\\', '/').Split('/').Last();
        private static string Cell(string value) => value.Replace("|", "\\|").Replace("\r", "").Replace("\n", "<br>");
        private static void Code(StringBuilder text, string value, string language)
        {
            string fence = "```";
            while (value.Contains(fence)) fence += "`";
            text.Append(fence).Append(language).Append('\n').Append(value).Append(value.EndsWith("\n", StringComparison.Ordinal) ? "" : "\n")
                .Append(fence).Append("\n\n");
        }
        // Keep logical PLC paths, but remove absolute local/UNC paths from free-form evidence and API metadata.
        private static string SafeText(string value) => Regex.Replace(value,
            @"[A-Za-z]:[\\/][^\s""<>|,;]*|\\\\[^\s""<>|,;]+|(?<![\w:/])/(?:[^/\s""<>|,;]+/)+[^\s""<>|,;]*",
            match => FileName(match.Value), RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        private static JsonNode? Safe(JsonNode? node)
        {
            if (node is JsonObject obj)
            {
                var result = new JsonObject();
                foreach (var pair in obj)
                    if (!new[] { "host", "hostname", "user", "username", "machinename" }.Contains(pair.Key.ToLowerInvariant()))
                        result[pair.Key] = Safe(pair.Value);
                return result;
            }
            if (node is JsonArray array) return new JsonArray(array.Select(Safe).ToArray());
            return node is JsonValue value && value.TryGetValue<string>(out string? text) ? JsonValue.Create(SafeText(text)) : node?.DeepClone();
        }
    }
}
