using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.Logic.Testing;
using TiaMcp.Logic.V4;
using TiaMcp.Logic.V4.Inputs;
using TiaOpenness.Shared;

namespace TiaMcpServer.ModelContextProtocol
{
    internal sealed partial class PlcTestLibraryTools
    {
        [McpServerTool(Name = "ExportPlcTestReport"),
            Description("[L0][Reports][FILE] Export a stored PLC test run as JUnit XML, FAT/SAT acceptance Markdown and JSON, or a reproduction report. " +
                "All eight releases; host files only, no worker, TIA or PLCSIM calls. Defaults to the session output directory. Explicit targets " +
                "outside the output root require Workbench approval. Incomplete runs are marked invalid for acceptance. FAT/SAT include audit " +
                "binding and chain verification plus blank supplier/customer sign-off rows. Local paths, including paths with spaces, are reduced " +
                "to file names in all tool report formats. Files have unique operation-id suffixes and never overwrite; Markdown/JSON pairs are " +
                "staged together and removed on publication failure. Reproduction includes only non-passed cases and redacts sensitive values " +
                "in both case documents and calls, preserving other source text. It shows preview then execution with preview identity placeholders; " +
                "restore matching case hashes if the library changed. Path-bearing case documents cannot produce a verbatim path-free reproduction. " +
                "Real PLCSIM/Test Suite installation acceptance is pending.")]
        public CallToolResult ExportPlcTestReport(
            [Description("Exact stored run id, or latest for the newest valid run.")] string runId = "latest",
            [Description("Report format: junit | fat | sat | repro.")] string format = "fat",
            [Description("Absolute output directory. Empty uses <OutputRoot>/<session>; targets outside the output root need approval.")]
            string exportDirectory = "")
        {
            const string tool = "ExportPlcTestReport";
            try
            {
                if (!new[] { "junit", "fat", "sat", "repro" }.Contains(format)) throw new ArgumentException("Unknown report format.");
                if (exportDirectory.Length == 0)
                {
#if TIA_ENGINE_HOST
                    exportDirectory = TiaMcp.FoundationHost.EngineHostConfiguration.CurrentOrNull?.GetExportDirectory(prepare: true) ?? "";
#endif
                }
                if (!Path.IsPathFullyQualified(exportDirectory) || exportDirectory != exportDirectory.Trim())
                    throw new ArgumentException("Supply an absolute exportDirectory or use an active session output directory.");
                var run = PlcTestRunRecord.Read(runsDirectory(), runId);
                string stem = Path.Combine(exportDirectory, "plc-test-" + run.RunId + "-" + format + "-" + Guid.NewGuid().ToString("N"));
                string[] roots = new[] { DataLocations.Current.Root, DataLocations.Current.OutputRoot };
                var paths = new JsonArray();
                if (format == "junit")
                {
                    PublishReport(stem, new[] { (".xml", PlcTestReportBuilder.WriteJUnit(run, roots)) });
                    paths.Add(stem + ".xml");
                }
                else
                {
                    var documents = new Dictionary<string, string>(StringComparer.Ordinal);
                    if (format == "repro")
                        foreach (var item in run.Cases.Where(c => c.Verdict != "passed"))
                        {
                            PlcTestCaseDocument.ValidateId(item.Id);
                            documents[item.Id] = File.ReadAllText(Path.Combine(libraryDirectory(), "cases", item.Id + ".json"), Encoding.UTF8);
                        }
                    var audit = format == "repro" ? new JsonObject() : ReportAudit(run, DataLocations.Current.AuditReadRoots);
                    var report = PlcTestReportBuilder.Build(run, format, audit, documents, CallJournalPayload.RedactArguments, roots);
                    PublishReport(stem, new[] { (".md", report.Markdown), (".json", PlcTestCaseDocument.Format(report.Document)) });
                    paths.Add(stem + ".md"); paths.Add(stem + ".json");
                }
                return McpServer.V4Result(tool, new JsonObject { ["runId"] = run.RunId, ["format"] = format,
                    ["files"] = paths, ["exitCode"] = run.ExitCode }, completed: true);
            }
            catch (FileNotFoundException) /* swallow(privacy): missing history or source document is reported without a local user path */
            { return McpServer.V4Reject(tool, new Error("Run or original case document was not found.", new NotFoundDetails(null))); }
            catch (DirectoryNotFoundException) /* swallow(privacy): missing source directory is reported without a local user path */
            { return McpServer.V4Reject(tool, new Error("Run or original case document was not found.", new NotFoundDetails(null))); }
            catch (Exception ex) when (ex is ArgumentException || ex is JsonException || ex is InputRejectionException)
            { return McpServer.V4Reject(tool, new Error(ex.Message, new InvalidArgumentDetails("", Array.Empty<string>()))); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            { return McpServer.V4Reject(tool, new Error("PLC test report file operation failed: " + ex.GetType().Name,
                new IoFailedDetails("plc-test-report", null))); }
        }

        // Stage every file before publishing any. Only files moved by this operation participate in rollback.
        internal static void PublishReport(string stem, (string Extension, string Text)[] files,
            Action<string, string>? writeStaged = null, Action<string, string>? move = null)
        {
            string stage = stem + ".tmp";
            Directory.CreateDirectory(stage);
            var published = new List<string>();
            bool complete = false;
            try
            {
                try
                {
                    foreach (var file in files)
                    {
                        string temporary = Path.Combine(stage, "report" + file.Extension);
                        if (writeStaged != null) writeStaged(temporary, file.Text);
                        else
                        {
                            using var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                            using var writer = new StreamWriter(stream, new UTF8Encoding(false));
                            writer.Write(file.Text);
                        }
                    }
                    foreach (var file in files)
                    {
                        string temporary = Path.Combine(stage, "report" + file.Extension);
                        string destination = stem + file.Extension;
                        if (move != null) move(temporary, destination); else File.Move(temporary, destination);
                        published.Add(destination);
                    }
                }
                finally
                {
                    foreach (var file in files)
                    {
                        string temporary = Path.Combine(stage, "report" + file.Extension);
                        if (File.Exists(temporary)) File.Delete(temporary);
                    }
                    Directory.Delete(stage);
                }
                complete = true;
            }
            finally
            {
                if (!complete) foreach (string path in published) File.Delete(path);
            }
        }

        internal static JsonObject ReportAudit(PlcTestRunRecord run, string[] roots)
        {
            string request = (string?)run.Audit["requestId"] ?? "";
            var rows = new JsonArray();
            var chains = new JsonArray();
            foreach (string root in roots)
            {
                var log = new AuditLog(root);
                var check = log.Verify();
                chains.Add(new JsonObject { ["state"] = check.Passed ? "intact" : "broken", ["count"] = check.Count,
                    ["breakIndex"] = check.BreakIndex, ["reason"] = check.Reason });
                if (!Directory.Exists(root)) continue;
                foreach (var row in log.Read().Where(r => request.Length > 0 && r.RequestId == request))
                    rows.Add(new JsonObject { ["index"] = row.Index, ["utc"] = row.Utc, ["event"] = row.Event,
                        ["requestId"] = row.RequestId, ["tool"] = row.Tool, ["outcome"] = row.Outcome, ["planHash"] = row.PlanHash });
            }
            return new JsonObject { ["requestId"] = request, ["binding"] = rows.Count == 0 ? "no audit row found" : "audit rows found",
                ["rows"] = rows, ["chains"] = chains };
        }
    }
}
