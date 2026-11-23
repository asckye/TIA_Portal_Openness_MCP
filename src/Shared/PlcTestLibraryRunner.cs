using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Logic.Testing;
using TiaMcp.Shared;
using TiaMcpServer.Runtime;
using TiaMcpServer.Siemens.Services;
using TiaOpenness.Shared;

namespace TiaMcpServer.ModelContextProtocol
{
    // A batch calls the existing services directly; only the outer tool participates in approval.
    internal sealed class PlcTestLibraryRunner
    {
        internal sealed class Entry
        {
            internal PlcTestCaseDocument Case = null!;
            internal string Hash = "";
            internal string? Unsupported;
        }

        private readonly PlcSimAdvancedPortService simulator;
        private readonly TestSuitePortService testSuite;
        internal PlcTestLibraryRunner(PlcSimAdvancedPortService simulator, TestSuitePortService testSuite)
        { this.simulator = simulator; this.testSuite = testSuite; }

        internal static PlcSimAdvancedPortService SimulatorService() => new PlcSimAdvancedPortService(CallWorker);
        internal static TestSuitePortService TestSuiteService() => new TestSuitePortService(CallWorker, () =>
            (string?)(McpServer.Worker.Binding?["identity"]?["projectPath"] ?? McpServer.Worker.Binding?["projectFile"]
                ?? McpServer.Worker.Binding?["projectPath"]) ?? "");

        private static JsonNode? CallWorker(string operation, JsonObject arguments)
        {
            string tool = operation == "runtime-channels.ExecutePlcSimAdvanced" ? "RunPlcSimAdvancedTestScenario" : "RunTestSuiteCase";
            using var lane = Dispatch.ToolDispatchLanes.Enter(tool, McpServer.WorkerDispatchCancellation);
            InvocationJournal.NativeCallStarted();
            return ((IHostToolServices)McpServer.Worker).Call(operation, arguments, McpServer.WorkerDispatchCancellation).GetAwaiter().GetResult();
        }

        internal static Entry[] Select(string directory, string release, string[] ids, string[] tags, out JsonArray skippedFiles)
        {
            skippedFiles = new JsonArray();
            if (ids.Length > 100 || tags.Length > 32 || tags.Any(t => string.IsNullOrEmpty(t) || t.Length > 64))
                throw new ArgumentException("Supply at most 100 ids and 32 tags of 1..64 characters.");
            foreach (string id in ids) PlcTestCaseDocument.ValidateId(id);
            var library = new PlcTestLibrary(directory);
            foreach (string id in ids) _ = library.Read(id);
            var listed = library.List(tags)["items"]!.AsArray();
            if (ids.Length == 0)
                foreach (var row in listed.Where(r => (bool?)r?["valid"] == false))
                    skippedFiles.Add(new JsonObject { ["id"] = PlcTestEvidence.Scrub((string?)row?["id"]),
                        ["message"] = "INVALID_CASE: stored file failed validation and will not run" });
            var rows = listed.Where(r => (bool?)r?["valid"] == true).ToArray();
            foreach (string id in ids)
                if (!rows.Any(r => string.Equals((string?)r?["id"], id, StringComparison.OrdinalIgnoreCase)))
                    throw new ArgumentException("Selected case was not found or does not match tags: " + id);
            var selected = rows.Where(r => ids.Length == 0 || ids.Contains((string)r!["id"]!, StringComparer.OrdinalIgnoreCase)).ToArray();
            if (selected.Length < 1 || selected.Length > 100) throw new ArgumentException("Select 1..100 valid PLC test cases.");
            return selected.Select(row =>
            {
                var loaded = library.Read((string)row!["id"]!);
                var document = PlcTestCaseDocument.Parse(loaded["testCase"]!.ToJsonString());
                var suite = document.Document["testSuite"];
                return new Entry { Case = document, Hash = (string)loaded["sha256"]!, Unsupported = suite == null ? null
                    : OptionalProductCapabilities.Unsupported(release, "RunTestSuiteCase", (string)suite["category"]!, (string)suite["kind"]!) };
            }).ToArray();
        }

        internal static JsonArray Plan(Entry[] entries) => new JsonArray(entries.Select(entry =>
        {
            var document = entry.Case.Document;
            return (JsonNode)new JsonObject {
                ["id"] = entry.Case.Id, ["kind"] = entry.Case.Kind, ["caseSha256"] = entry.Hash, ["runnable"] = entry.Unsupported == null,
                ["plan"] = entry.Case.Kind == "plcsimScenario"
                    ? PlcSimAdvancedLogic.ScenarioPlan(PlcSimAdvancedLogic.ParseScenario(document["scenario"]!.ToJsonString())) : null,
                ["testSuite"] = document["testSuite"]?.DeepClone(),
                ["verdict"] = entry.Unsupported == null ? null : "skipped",
                ["message"] = entry.Unsupported == null ? null : "UNSUPPORTED_CAPABILITY: " + entry.Unsupported
            };
        }).ToArray());

        internal PlcTestRunRecord Execute(Entry[] entries, string runsDirectory, string release, string hash, string projectFile,
            string[] ids, string[] tags)
        {
            AuditInvocation.StartCurrent();
            var run = NewRecord(entries, release, hash, projectFile, ids, tags);
            run.Save(runsDirectory);
            bool finished = false;
            try
            {
                for (int i = 0; i < entries.Length; i++)
                {
                    if (entries[i].Unsupported != null) continue;
                    var item = run.Cases[i];
                    var timer = Stopwatch.StartNew();
                    JsonObject reply = Invoke(entries[i].Case, out string? diagnostic, out bool refused);
                    item.DurationMs = timer.ElapsedMilliseconds;
                    item.Verdict = refused ? "skipped" : Verdict(item.Kind, reply);
                    item.Evidence = PlcTestEvidence.Normalize(reply, item.Verdict, diagnostic);
                    item.Message = PlcTestEvidence.Text(item.Evidence["message"]);
                    string failure = PlcTestJUnitWriter.FailureDetails(item);
                    if (item.Verdict == "failed" && failure.Length > 0) item.Message = failure;
                    refused |= (string?)reply["error"]?["details"]?["condition"] == "worker-call-running";
                    if (item.Verdict == "unknown" || refused)
                    {
                        run.Status = "stopped";
                        foreach (var remaining in run.Cases.Skip(i + 1))
                        {
                            remaining.Verdict = "skipped";
                            remaining.Message = item.Verdict == "unknown"
                                ? "NOT_EXECUTED: an earlier case ended with an unknown outcome" : "NOT_EXECUTED: an earlier case was refused before dispatch";
                            remaining.Evidence = new JsonObject { ["code"] = "NOT_EXECUTED", ["message"] = remaining.Message,
                                ["details"] = new JsonObject() };
                        }
                    }
                    run.Save(runsDirectory);
                    if (run.Status == "stopped") break;
                }
                finished = true;
            }
            finally
            {
                if (run.Status == "running") run.Status = finished ? "completed" : "stopped";
                run.FinishedUtc = Now();
                run.Save(runsDirectory);
                PlcTestRunRecord.WriteAtomic(Path.Combine(runsDirectory, run.RunId, "junit.xml"), PlcTestJUnitWriter.Write(run));
            }
            return run;
        }

        private static PlcTestRunRecord NewRecord(Entry[] entries, string release, string hash, string projectFile, string[] ids, string[] tags)
        {
            return new PlcTestRunRecord {
                StartedUtc = Now(),
                ReleaseKey = release, HostVersion = typeof(PlcTestLibraryRunner).Assembly.GetName().Version?.ToString() ?? "",
                PlanHash = hash, Project = entries.Any(e => e.Case.Kind == "testSuite" && e.Unsupported == null)
                    ? new JsonObject { ["file"] = Path.GetFileName(projectFile) } : null,
                Audit = new JsonObject { ["requestId"] = AuditInvocation.CurrentRequestId ?? "" },
                Selection = new JsonObject { ["ids"] = JsonSerializer.SerializeToNode(ids),
                    ["tags"] = JsonSerializer.SerializeToNode(tags.Select(PlcTestEvidence.Scrub).ToArray()) },
                Cases = entries.Select(e => new PlcTestCaseResult {
                    Id = e.Case.Id, Title = PlcTestEvidence.Scrub(e.Case.Title),
                    Requirement = e.Case.Requirement == null ? null : PlcTestEvidence.Scrub(e.Case.Requirement),
                    Kind = e.Case.Kind, CaseSha256 = e.Hash,
                    Message = e.Unsupported == null ? "NOT_EXECUTED: not started" : "UNSUPPORTED_CAPABILITY: " + e.Unsupported,
                    Evidence = new JsonObject { ["code"] = e.Unsupported == null ? "NOT_EXECUTED" : "UNSUPPORTED_CAPABILITY",
                        ["message"] = e.Unsupported == null ? "NOT_EXECUTED: not started" : "UNSUPPORTED_CAPABILITY: " + e.Unsupported,
                        ["details"] = new JsonObject() }
                }).ToList()
            };
        }

        // History stores millisecond timestamps; JUnit must use the same precision before and after a read.
        private static DateTimeOffset Now() => DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        private JsonObject Invoke(PlcTestCaseDocument testCase, out string? diagnostic, out bool refused)
        {
            var document = testCase.Document;
            string? reason = null;
            bool threw = false;
            ResponseMessage Attempt(Func<ResponseMessage> call)
            {
                try { return Capture(call()); }
                catch { threw = true; throw; }
            }
            ResponseMessage Capture(ResponseMessage reply)
            {
                var error = reply.Meta?["error"];
                string native = error is JsonObject obj ? PlcTestEvidence.Text(obj["message"]) : PlcTestEvidence.Text(error);
                if (native.Length == 0 && reply is ResponseJsonReport scenario)
                    native = PlcTestEvidence.Text(scenario.Data?["operatingModeRestoreError"]);
                string status = PlcTestEvidence.Text(reply.Meta?["status"]);
                reason = native.Length > 0 ? (status.Length > 0 ? status + ": " : "") + native : reply.Message;
                return reply;
            }
            global::ModelContextProtocol.Protocol.CallToolResult result;
            if (testCase.Kind == "plcsimScenario")
                result = RuntimeToolContract.Run("RunPlcSimAdvancedTestScenario", false, true, () =>
                    Attempt(() => simulator.RunPlcSimAdvancedTestScenario(document["scenario"]!.ToJsonString(), confirmRun: true, dryRun: false)),
                    dispatched: true);
            else
            {
                var suite = document["testSuite"]!;
                result = OptionalPackageContract.Run("RunTestSuiteCase", true, () => Attempt(() => testSuite.RunTestSuiteCase(
                    (string)suite["category"]!, "", confirmExternalExecution: true, dryRun: false,
                    namesJson: suite["names"]?.ToJsonString() ?? "[]", runAll: (bool?)suite["runAll"] == true, kind: (string)suite["kind"]!)),
                    dispatched: true);
            }
            diagnostic = reason;
            refused = threw && (string?)result.StructuredContent?["meta"]?["execution"] == "not-started";
            return result.StructuredContent!.AsObject();
        }

        private static string Verdict(string kind, JsonObject reply)
        {
            string outcome = PlcTestEvidence.Text(reply["meta"]?["outcome"]);
            if (outcome == "unknown") return "unknown";
            if ((string?)reply["error"]?["code"] == "UNSUPPORTED_CAPABILITY"
                || (string?)reply["error"]?["details"]?["condition"] == "worker-call-running") return "skipped";
            if (outcome == "rejected-before-operation") return "error";
            // The service contract has already decided the operation outcome. Apply only the shared test-verdict mapping to its sanitized payload.
            return PlcTestRunRecord.MapVerdict(kind, new JsonObject { ["data"] = reply["data"]?.DeepClone(),
                ["meta"] = reply["data"]?.DeepClone(), ["ok"] = outcome == "succeeded" });
        }
    }
}
