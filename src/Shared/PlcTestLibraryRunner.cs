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

        private static readonly JsonSerializerOptions ReplyOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
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
            AuditInvocation.StartCurrent();
            InvocationJournal.NativeCallStarted();
            return ((IHostToolServices)McpServer.Worker).Call(operation, arguments, McpServer.WorkerDispatchCancellation).GetAwaiter().GetResult();
        }

        internal static Entry[] Select(string directory, string release, string[] ids, string[] tags)
        {
            if (ids.Length > 100 || tags.Length > 32 || tags.Any(t => string.IsNullOrEmpty(t) || t.Length > 64))
                throw new ArgumentException("Supply at most 100 ids and 32 tags of 1..64 characters.");
            foreach (string id in ids) PlcTestCaseDocument.ValidateId(id);
            var library = new PlcTestLibrary(directory);
            var rows = library.List(tags)["items"]!.AsArray();
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
            var run = new PlcTestRunRecord {
                StartedUtc = Now(),
                ReleaseKey = release, HostVersion = typeof(PlcTestLibraryRunner).Assembly.GetName().Version?.ToString() ?? "",
                PlanHash = hash, Project = entries.Any(e => e.Case.Kind == "testSuite") ? new JsonObject { ["file"] = projectFile } : null,
                Audit = new JsonObject { ["requestId"] = AuditInvocation.CurrentRequestId ?? "" },
                Selection = new JsonObject { ["ids"] = JsonSerializer.SerializeToNode(ids), ["tags"] = JsonSerializer.SerializeToNode(tags) },
                Cases = entries.Select(e => new PlcTestCaseResult {
                    Id = e.Case.Id, Title = e.Case.Title, Requirement = e.Case.Requirement, Kind = e.Case.Kind, CaseSha256 = e.Hash,
                    Message = e.Unsupported == null ? "NOT_EXECUTED: not started" : "UNSUPPORTED_CAPABILITY: " + e.Unsupported,
                    Evidence = new JsonObject { ["errorCode"] = e.Unsupported == null ? "NOT_EXECUTED" : "UNSUPPORTED_CAPABILITY" }
                }).ToList()
            };
            run.Save(runsDirectory);
            for (int i = 0; i < entries.Length; i++)
            {
                if (entries[i].Unsupported != null) continue;
                var item = run.Cases[i];
                var timer = Stopwatch.StartNew();
                JsonObject reply = Invoke(entries[i].Case);
                item.DurationMs = timer.ElapsedMilliseconds;
                item.Verdict = PlcTestRunRecord.MapVerdict(item.Kind, reply);
                // PLCSIM data is retained unchanged. Test Suite has no data payload: retain its result-bearing meta object.
                item.Evidence = (JsonObject)(reply["data"] ?? reply["meta"] ?? new JsonObject()).DeepClone();
                item.Message = Message(item, reply);
                if (item.Verdict == "unknown")
                {
                    run.Status = "stopped";
                    foreach (var remaining in run.Cases.Skip(i + 1))
                    {
                        remaining.Verdict = "skipped";
                        remaining.Message = "NOT_EXECUTED: an earlier case ended with an unknown outcome";
                        remaining.Evidence = new JsonObject { ["errorCode"] = "NOT_EXECUTED" };
                    }
                }
                run.Save(runsDirectory);
                if (run.Status == "stopped") break;
            }
            if (run.Status == "running") run.Status = "completed";
            run.FinishedUtc = Now();
            run.Save(runsDirectory);
            PlcTestRunRecord.WriteAtomic(Path.Combine(runsDirectory, run.RunId, "junit.xml"), PlcTestJUnitWriter.Write(run));
            return run;
        }

        // History stores millisecond timestamps; JUnit must use the same precision before and after a read.
        private static DateTimeOffset Now() => DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        private JsonObject Invoke(PlcTestCaseDocument testCase)
        {
            var document = testCase.Document;
            object reply;
            if (testCase.Kind == "plcsimScenario")
                reply = simulator.RunPlcSimAdvancedTestScenario(document["scenario"]!.ToJsonString(), confirmRun: true, dryRun: false);
            else
            {
                var suite = document["testSuite"]!;
                reply = testSuite.RunTestSuiteCase((string)suite["category"]!, "", confirmExternalExecution: true, dryRun: false,
                    namesJson: suite["names"]?.ToJsonString() ?? "[]", runAll: (bool?)suite["runAll"] == true, kind: (string)suite["kind"]!);
            }
            return JsonSerializer.SerializeToNode(reply, reply.GetType(), ReplyOptions)!.AsObject();
        }

        private static string Message(PlcTestCaseResult item, JsonObject reply)
        {
            item.Message = (string?)reply["message"] ?? item.Verdict;
            string details = PlcTestJUnitWriter.FailureDetails(item);
            if (item.Verdict != "passed" && details.Length > 0) return details;
            return item.Message;
        }
    }
}
