using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using TiaOpenness.Shared;

internal static class AuditLogTests
{
    internal static int Child(string[] args)
    {
        if (args[0] == "audit-verify") return AuditCli.Verify(args[1], Console.Out);
        var log = new AuditLog(args[1], 1500);
        for (int i = 0; i < 60; i++) log.Append("request", args[2] + ":" + i, "fixture", "21", "WriteFixture");
        return 0;
    }
    internal static void Run(string root)
    {
        int checks = 0;
        void Check(bool value, string name) { if (!value) throw new Exception(name); checks++; }
        string Fresh(string name) => Path.Combine(root, "audit-tests", name);
        var empty = new AuditLog(Fresh("empty"));
        Check(empty.Verify().Passed && empty.Verify().Count == 0, "empty chain has no broken retained link");
        var log = new AuditLog(Fresh("creation"));
        log.Append("request", "one", "fixture", "21", "WriteFixture");
        log.Append("start", "one", "fixture", "21", "WriteFixture");
        log.Append("end", "one", "fixture", "21", "WriteFixture", "partial");
        var rows = log.Read();
        Check(log.Verify().Passed && rows.Count == 3 && rows[0].PreviousHash == AuditLog.Genesis && rows[1].PreviousHash == AuditLog.Hash(rows[0]), "chain links canonical records");
        Check(rows.Last().Outcome == "partial", "V4 outcome preserved");
        var canonical = AuditLog.Canonical(rows[0]);
        var reordered = JsonNode.Parse(canonical)!.AsObject().Reverse().ToArray();
        var reversed = new JsonObject(); foreach (var pair in reordered) reversed[pair.Key] = pair.Value?.DeepClone();
        Check(AuditLog.Hash(AuditLog.Parse(reversed.ToJsonString())) == AuditLog.Hash(rows[0]), "canonical hashing ignores input property order");
        var rotation = new AuditLog(Fresh("rotation"), 1);
        for (int i = 0; i < 4; i++) rotation.Append("request", "r" + i, "fixture", "20", "WriteFixture");
        Check(Directory.GetFiles(Fresh("rotation"), "audit-*.jsonl").Length == 4 && rotation.Verify().Count == 4 && rotation.Verify().Passed, "rotation links previous file tail");
        foreach (string mode in new[] { "modified", "removed", "inserted", "malformed", "duplicate-key", "wrong-type" })
        {
            string directory = Fresh(mode);
            var chain = new AuditLog(directory);
            for (int i = 0; i < 5; i++) chain.Append("request", "r" + i, "fixture", "21", "WriteFixture");
            string path = Directory.GetFiles(directory, "audit-*.jsonl").Single();
            var lines = File.ReadAllLines(path).ToList();
            if (mode == "modified") lines[1] = lines[1].Replace("WriteFixture", "Changed");
            if (mode == "removed") lines.RemoveAt(1);
            if (mode == "inserted") lines.Insert(1, lines[0]);
            if (mode == "malformed") lines[1] = "{broken";
            if (mode == "duplicate-key") lines[1] = lines[1].Insert(1, "\"index\":2,");
            if (mode == "wrong-type") lines[1] = lines[1].Replace("\"index\":2", "\"index\":\"2\"");
            File.WriteAllText(path, string.Join("\n", lines) + "\n", new UTF8Encoding(false));
            var result = chain.Verify();
            Check(!result.Passed && result.BreakIndex == (mode == "modified" ? 3 : 2), mode + " first break");
        }
        string tailPath = Directory.GetFiles(Fresh("creation"), "audit-*.jsonl").Single();
        File.WriteAllText(tailPath, string.Join("\n", File.ReadAllLines(tailPath).Take(2)) + "\n", new UTF8Encoding(false));
        Check(log.Verify().Passed && log.Verify().Count == 2, "whole-record tail truncation passes by design");
        var approval = new AuditLog(Fresh("approvals"));
        foreach (string decision in new[] { "granted", "denied", "timeout" }) approval.Approval("one", "fixture", "21", "WriteFixture", decision, "plan");
        approval.ApprovalSwitch("fixture", false);
        Check(approval.Verify().Passed && approval.Read().Last().ApprovalEnabled == false, "approval and switch API");
        using (AuditInvocation.Begin(false, "fixture", "21", "ReadFixture", log: approval)) { }
        Check(approval.Read().Count == 4, "read calls never audited");
        foreach (string outcome in new[] { "succeeded", "rejected-before-operation", "failed", "read-failed", "partial", "unknown" })
        {
            using (var call = AuditInvocation.Begin(true, "fixture", "21", "WriteFixture", log: approval))
                call!.Complete("{\"schemaVersion\":4,\"meta\":{\"outcome\":\"" + outcome + "\"}}");
            Check(approval.Read().Last().Outcome == outcome, "outcome " + outcome);
        }
        string concurrent = Fresh("concurrent");
        var children = Enumerable.Range(0, 4).Select(i => Process.Start(new ProcessStartInfo(Assembly.GetExecutingAssembly().Location,
            "audit-writer \"" + concurrent + "\" " + i) { UseShellExecute = false, CreateNoWindow = true })!).ToArray();
        foreach (var child in children) { Check(child.WaitForExit(60000) && child.ExitCode == 0, "child writer completed"); child.Dispose(); }
        var multi = new AuditLog(concurrent);
        Check(multi.Verify().Passed && multi.Verify().Count == 240 && multi.Read().Select(r => r.RequestId).Distinct().Count() == 240, "four processes: no lost, duplicate or interleaved records");
        Check(Directory.GetFiles(concurrent, "audit-*.jsonl").Length > 1, "concurrent rotation exercised");
        foreach (var sample in new[] { (Path: concurrent, Exit: 0), (Path: Fresh("removed"), Exit: 3) })
        {
            using (var child = Process.Start(new ProcessStartInfo(Assembly.GetExecutingAssembly().Location, "audit-verify \"" + sample.Path + "\"")
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!)
            {
                string text = child.StandardOutput.ReadToEnd(); child.WaitForExit();
                Check(child.ExitCode == sample.Exit && text.Trim().Split('\n').Length == 1 && JsonNode.Parse(text)!["passed"]!.GetValue<bool>() == (sample.Exit == 0), "CLI exit and one JSON report");
            }
        }
        string independent = Fresh("independent-writers");
        var writers = new[] { new AuditLog(independent, 1500), new AuditLog(independent, 1500) };
        using (var start = new System.Threading.ManualResetEventSlim(false))
        {
            var tasks = writers.Select((writer, id) => System.Threading.Tasks.Task.Run(() =>
            {
                start.Wait();
                for (int i = 0; i < 60; i++) writer.Append("request", id + ":" + i, "fixture", "21", "WriteFixture");
            })).ToArray();
            start.Set(); System.Threading.Tasks.Task.WaitAll(tasks);
        }
        var shared = new AuditLog(independent);
        Check(shared.Verify().Passed && shared.Verify().Count == 120 && shared.Read().Select(row => row.RequestId).Distinct().Count() == 120,
            "independent writers use the real cross-process lock and preserve one chain through rotation");
        using (var output = new StringWriter())
        {
            Check(AuditCli.Verify(new[] { independent, Fresh("removed") }, output) == 3, "both root chains verified separately");
            var reports = JsonNode.Parse(output.ToString())!.AsArray();
            Check(reports.Count == 2 && reports[0]!["passed"]!.GetValue<bool>() && !reports[1]!["passed"]!.GetValue<bool>()
                && reports[0]!["count"]!.GetValue<int>() == 120 && reports[1]!["breakIndex"]!.GetValue<int>() == 2
                && reports[1]!["chain"]!.GetValue<string>() == Fresh("removed") && reports[1]!["file"]!.GetValue<string>().StartsWith("audit-"),
                "CLI reports the chain and file without merging indices");
        }
        string readOnlyLock = Path.Combine(independent, ".audit.lock");
        File.SetAttributes(readOnlyLock, FileAttributes.ReadOnly);
        try { Check(shared.Verify().Passed && shared.Read().Count == 120, "read-only audit lock supports verification and reading without write access"); }
        finally { File.SetAttributes(readOnlyLock, FileAttributes.Normal); }
        Console.WriteLine("COMPLETE: " + checks + " audit chain checks passed; no TIA connection");
    }
}
