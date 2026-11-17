using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
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
    [McpServerToolType]
    internal sealed partial class PlcTestLibraryTools
    {
        private readonly Func<string> libraryDirectory;
        private readonly Func<string> runsDirectory;
        private readonly PlcTestLibraryRunner runner;
        public PlcTestLibraryTools() : this(() => DataLocations.Current.TestLibraryDirectory, () => DataLocations.Current.TestRunsDirectory) { }
        internal PlcTestLibraryTools(Func<string> libraryDirectory, Func<string> runsDirectory,
            Siemens.Services.PlcSimAdvancedPortService? simulator = null, TiaMcp.Shared.TestSuitePortService? testSuite = null)
        {
            this.libraryDirectory = libraryDirectory; this.runsDirectory = runsDirectory;
            runner = new PlcTestLibraryRunner(simulator ?? PlcTestLibraryRunner.SimulatorService(),
                testSuite ?? PlcTestLibraryRunner.TestSuiteService());
        }

        [McpServerTool(Name = "RunPlcTestLibrary"),
            Description("[L2][Simulation][ONLINE-WRITE] Run 1..100 stored PLC test entries sequentially on all eight releases. " +
                "ids and tags filter the library together; tags match any value, case-insensitively. Empty filters select the whole library. " +
                "Default dryRun=true validates locally and returns the ordered plan and planHash without worker calls. Execution requires " +
                "dryRun=false and expectedPlanHash; runnable Test Suite entries also require a bound project and absolute expectedProjectFile. " +
                "One Workbench approval covers the batch. Failed/error cases do not stop the run; an unknown outcome stops without replay. " +
                "Unsupported entries are skipped (Test Suite V17+, system V18+, application test sets V20+); all unsupported is refused. " +
                "Writes run history and junit.xml under the data root, returns verdict totals and exitCode (0 passed, 3 failed/error, 4 skipped, " +
                "5 unknown/incomplete). Long runs block tools/call; asynchronous execution and cancellation are not available. " +
                "Real PLCSIM Advanced / Test Suite installation acceptance is pending.")]
        public CallToolResult RunPlcTestLibrary(
            [Description("Optional exact case ids, at most 100; case-insensitive. Combined with the tags filter.")] string[]? ids = null,
            [Description("Optional tags, at most 32 of 1..64 characters; any case-insensitive match. Combined with ids.")] string[]? tags = null,
            [Description("true validates and previews without worker calls; false executes the approved batch.")] bool dryRun = true,
            [Description("Required for execution, including PLCSIM-only runs; use planHash from the matching preview.")] string expectedPlanHash = "",
            [Description("Absolute bound project file required when a runnable Test Suite entry is selected.")] string expectedProjectFile = "")
        {
            const string tool = "RunPlcTestLibrary";
            ids ??= Array.Empty<string>(); tags ??= Array.Empty<string>();
            bool started = false;
            try
            {
                if (!dryRun && string.IsNullOrWhiteSpace(expectedPlanHash)) return McpServer.V4Reject(tool, McpServer.InvalidInput("expectedPlanHash"));
                var entries = PlcTestLibraryRunner.Select(libraryDirectory(), McpServer.ReleaseKey, ids, tags, out var skippedFiles);
                var orderedPlan = PlcTestLibraryRunner.Plan(entries);
                if (entries.All(e => e.Unsupported != null)) return McpServer.V4Reject(tool,
                    new Error("Every selected entry is unsupported on this release.", new UnsupportedCapabilityDetails(McpServer.ReleaseKey, tool, null)),
                    new JsonObject { ["plan"] = new JsonObject { ["cases"] = orderedPlan } });
                bool writing = entries.Any(e => e.Case.Kind == "testSuite" && e.Unsupported == null);
                using var lane = dryRun ? null : Dispatch.ToolDispatchLanes.Enter(writing ? "RunTestSuiteCase" : "RunPlcSimAdvancedTestScenario",
                    McpServer.WorkerDispatchCancellation);
                var arguments = new { ids, tags, cases = entries.Select(e => new { id = e.Case.Id, sha256 = e.Hash, document = e.Case.Document }).ToArray() };
                var result = McpServer.RunWritePreview(tool, dryRun, arguments, () => {
                    if (dryRun) return McpServer.V4Result(tool, new JsonObject());
                    started = true;
                    return Run(entries, expectedPlanHash, expectedProjectFile, ids, tags);
                }, expectedPlanHash, expectedProjectFile, writing);
                if (result.StructuredContent?["data"] is JsonObject data)
                {
                    data["dryRun"] = dryRun;
                    if (dryRun && (bool?)result.StructuredContent["ok"] == true)
                        data["plan"] = new JsonObject { ["cases"] = orderedPlan, ["skippedFiles"] = skippedFiles };
                    if (!dryRun) data.Remove("readOnlyInvocation");
                    result.Content = new[] { new TextContentBlock { Text = V4Json.Serialize(result.StructuredContent) } };
                }
                return result;
            }
            catch (Exception ex) when (!started && (ex is ArgumentException || ex is JsonException || ex is InputRejectionException
                || ex is IOException || ex is UnauthorizedAccessException))
            {
                string message = ex is IOException || ex is UnauthorizedAccessException ? ex.GetType().Name : PlcTestEvidence.Scrub(ex.Message);
                return McpServer.V4Reject(tool, new Error("Invalid selection or stored test case: " + message,
                    new InvalidArgumentDetails("", Array.Empty<string>())));
            }
        }

        private CallToolResult Run(PlcTestLibraryRunner.Entry[] entries, string hash, string projectFile, string[] ids, string[] tags)
        {
            string directory = runsDirectory();
            var run = runner.Execute(entries, directory, McpServer.ReleaseKey, hash, projectFile, ids, tags);
            var data = new JsonObject {
                ["runId"] = run.RunId, ["totals"] = run.Totals, ["exitCode"] = run.ExitCode,
                ["runDirectory"] = Path.Combine(directory, run.RunId), ["junitFile"] = Path.Combine(directory, run.RunId, "junit.xml"),
                ["cases"] = JsonSerializer.SerializeToNode(run.Cases.Select(c => new { id = c.Id, verdict = c.Verdict, message = c.Message }))
            };
            if (run.Status == "stopped" && run.Cases.All(c => c.Verdict != "unknown"))
            {
                var refusal = new Error("A case was refused before dispatch; remaining cases were not executed.",
                    new PreconditionFailedDetails("case-not-started", null));
                return run.Cases.All(c => c.Verdict == "skipped") ? McpServer.V4Reject("RunPlcTestLibrary", refusal, data)
                    : McpServer.V4Result("RunPlcTestLibrary", data,
                        new Error(refusal.Message, new PartialFailureDetails(run.Cases.Count(c => c.Verdict == "passed"),
                            run.Cases.Count(c => c.Verdict is "failed" or "error"), run.Cases.Count(c => c.Verdict == "skipped"))),
                        Outcome.Partial, Execution.Partial, Completeness.Partial);
            }
            return run.Status == "completed" ? McpServer.V4Result("RunPlcTestLibrary", data, completed: true)
                : McpServer.V4Result("RunPlcTestLibrary", data,
                    new Error("A case ended with an unknown outcome. Inspect the target; do not replay the run.",
                        new OutcomeUnknownDetails(null, new System.Collections.Generic.Dictionary<string, JsonElement>())),
                    Outcome.Unknown, Execution.Unknown, Completeness.Partial);
        }

        [McpServerTool(Name = "ManagePlcTestLibrary"),
            Description("[L1][Simulation][FILE] Manage stored PLC test cases and read run history under the data root on all eight releases. " +
                "Actions: list (including invalid files), read, save, delete, listRuns, readRun. Save validates the closed schemaVersion=1 JSON; " +
                "plcsimScenario uses the existing scenario validator; testSuite stores exact project case/test-set names. Existing cases need " +
                "overwrite=true. Delete returns the document for restoration. list filters tags (any match) and uses offset/limit; history is newest " +
                "first; invalid run files remain visible and only the requested directory page is parsed. readRun latest selects the newest valid record. " +
                "Unreadable runs return IO_FAILED naming the run. Incomplete runs return exitCode=5. Host files only; no TIA or PLCSIM calls. " +
                "Stored Test Suite selections require V17+, system V18+, application test sets V20+ when executed. Real installation acceptance pending.")]
        public CallToolResult ManagePlcTestLibrary(
            [Description("list | read | save | delete | listRuns | readRun.")] string action,
            [Description("Case id; read/delete only. 1..64 letters, digits, dot, underscore or hyphen; starts with a letter or digit. " +
                "Windows device stems (CON, PRN, AUX, NUL, COM1-9, LPT1-9) are refused before any extension.")] string id = "",
            [Description("Closed schemaVersion=1 case document; required for save. kind selects scenario or testSuite, exclusively.")]
            PlcTestCaseDocument? testCase = null,
            [Description("Allow save to replace an existing case, returning its previous SHA-256.")] bool overwrite = false,
            [Description("Optional tags for list; any case-insensitive match selects a case. Invalid files remain visible.")] string[]? tags = null,
            [Description("Zero-based offset for list/listRuns.")] int offset = 0,
            [Description("Page size for list/listRuns, 1..100.")] int limit = 100,
            [Description("Exact history run id, or latest for the newest valid record; readRun only.")] string runId = "latest")
        {
            const string tool = "ManagePlcTestLibrary";
            try
            {
                if (offset < 0 || limit < 1 || limit > 100) throw new ArgumentException("offset must be nonnegative and limit must be 1..100.");
                if (tags != null && (tags.Length > 32 || tags.Any(t => string.IsNullOrEmpty(t) || t.Length > 64)))
                    throw new ArgumentException("Supply at most 32 tags of 1..64 characters.");
                if (action == "read" || action == "delete") PlcTestCaseDocument.ValidateId(id);
                var library = new PlcTestLibrary(libraryDirectory());
                JsonObject data;
                switch (action)
                {
                    case "list": return Page(library.List(tags)["items"]!.AsArray(), offset, limit);
                    case "read": data = library.Read(id); break;
                    case "save": data = library.Save(testCase ?? throw new ArgumentException("save requires testCase."), overwrite); break;
                    case "delete": data = library.Delete(id); break;
                    case "listRuns": return Page(PlcTestRunRecord.List(runsDirectory(), offset, limit), offset, limit);
                    case "readRun": data = new JsonObject { ["run"] = PlcTestRunRecord.Read(runsDirectory(), runId).Document }; break;
                    default: throw new ArgumentException("Unknown library action.");
                }
                return McpServer.V4Result(tool, data, completed: action == "save" || action == "delete");
            }
            catch (PlcTestRunReadException ex)
            { return McpServer.V4Reject(tool, new Error(ex.Message, new IoFailedDetails("plc-test-run", null))); }
            catch (FileNotFoundException) /* swallow(privacy): return NOT_FOUND without exposing the full local file path */
            { return McpServer.V4Reject(tool, new Error("Case or run was not found.", new NotFoundDetails(null))); }
            catch (DirectoryNotFoundException) /* swallow(privacy): return NOT_FOUND without exposing the full local directory path */
            { return McpServer.V4Reject(tool, new Error("Case or run was not found.", new NotFoundDetails(null))); }
            catch (Exception ex) when (ex is ArgumentException || ex is JsonException || ex is InputRejectionException)
            { return McpServer.V4Reject(tool, new Error("Invalid library input or stored document: " + ex.Message,
                new InvalidArgumentDetails("", Array.Empty<string>()))); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            { return McpServer.V4Reject(tool, new Error("PLC test library file operation failed: " + ex.Message,
                new IoFailedDetails("plc-test-library", null))); }
        }

        private static CallToolResult Page(JsonArray rows, int offset, int limit)
        {
            if (offset > rows.Count) throw new ArgumentException("offset exceeds the matching row count.");
            return Page(new JsonObject {
                ["items"] = new JsonArray(rows.Skip(offset).Take(limit).Select(r => r!.DeepClone()).ToArray()),
                ["matchingCount"] = rows.Count, ["returnedCount"] = Math.Min(limit, rows.Count - offset)
            }, offset, limit);
        }

        private static CallToolResult Page(JsonObject page, int offset, int limit)
        {
            int total = (int)page["matchingCount"]!;
            return McpServer.V4Result("ManagePlcTestLibrary", page,
                paging: new Paging(PagingMode.Offset, offset, limit, offset + limit < total ? offset + limit : (int?)null,
                    null, null, total, offset + limit >= total, PagingUnit.Items));
        }
    }
}
