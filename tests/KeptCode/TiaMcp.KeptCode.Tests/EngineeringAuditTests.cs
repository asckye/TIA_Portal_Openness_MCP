using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using TiaMcpServer.Siemens;
using TiaMcpServer.ModelContextProtocol;

internal static class EngineeringAuditTests
{
    internal static void Run(Action<bool, string> check)
    {
        check(EngineeringAuditLogic.Consistency(new bool?[] { true, false }, true) == false, "root true and unit false must report inconsistent");
        check(EngineeringAuditLogic.Consistency(new bool?[] { true, null }, true) == null, "unreadable block is not compiled evidence");
        check(EngineeringAuditLogic.Consistency(Array.Empty<bool?>(), true) == null, "empty PLC does not establish consistency");
        check(EngineeringAuditLogic.Consistency(new bool?[] { true }, false) == null, "partial enumeration must not report consistency");
        check(EngineeringAuditLogic.Consistency(new bool?[] { true, true }, true) == true, "complete consistent blocks establish block/type consistency");
        check(EngineeringAuditLogic.DownloadReady(true, true, true, false) == null, "offline preflight cannot establish actual download readiness");
        check(EngineeringAuditLogic.DownloadReady(true, true, false, false) == false, "inconsistent PLC is not download ready");
        check(EngineeringAuditLogic.DownloadReady(true, true, null, false) == null, "unknown consistency remains unknown");
        check(EngineeringAuditLogic.DownloadReady(false, true, true, false) == false, "missing download provider blocks readiness");
        check(EngineeringAuditLogic.DownloadReady(true, true, true, true) == false, "read errors prevent readiness");
        check(!EngineeringAuditLogic.ExactNamesPresent(new[] { "X" }, new[] { "X_backup" }), "regression: substring matches must not verify import");
        check(!EngineeringAuditLogic.ExactNamesPresent(Array.Empty<string>(), new[] { "X" }), "missing native imported names cannot verify preexisting block");
        check(!EngineeringAuditLogic.ExactNamesPresent(new[] { "X", "Y" }, new[] { "X" }), "verify every imported block, not just the first");
        check(EngineeringAuditLogic.ExactNamesPresent(new[] { "X" }, new[] { "X", "Y" }), "exact target names verify existence only");
        foreach (string suffix in new[] { "CrossReferenceService", "Siemens.Engineering.CrossReference.CrossReferenceService", "Service", "crossreferenceservice" })
            check(CrossReferenceGuardLogic.ReflectionRefusal(suffix, "ToString") != null, "block reflective native service acquisition: " + suffix);
        check(CrossReferenceGuardLogic.ReflectionRefusal(null, "GetCrossReferences") != null, "reflective object method cannot bypass policy");
        check(CrossReferenceGuardLogic.ReflectionRefusal("ChecksumProvider", "ToString") == null, "unrelated read services remain available");
        var oldPatch = EngineeringAuditLogic.DocumentPreflight("SCL TextualInterface", 20, 3);
        check(oldPatch["patchSupportKnown"]!.GetValue<bool>() == false && oldPatch["warnings"]!.AsArray().Count == 3, "V20 early patch and textual interface hazards reported together");
        var newPatch = EngineeringAuditLogic.DocumentPreflight("SCL TextualInterface", 20, 4);
        check(newPatch["patchSupportKnown"]!.GetValue<bool>() && !newPatch["losslessRoundTrip"]!.GetValue<bool>() && newPatch["warnings"]!.AsArray().Count == 2, "V20 U4 does not remove textual-interface/format limits");
        check(EngineeringAuditLogic.DocumentPreflight("LAD", 20, null)["installedUpdate"] == null, "no fabricated installed patch from SDK");

        string root = Path.Combine(Path.GetTempPath(), "tia-journal-test-" + Guid.NewGuid().ToString("N"));
        string? previous = Environment.GetEnvironmentVariable("TIA_MCP_DIAGNOSTICS_DIRECTORY");
        try
        {
            Environment.SetEnvironmentVariable("TIA_MCP_DIAGNOSTICS_DIRECTORY", root);
            string id = InvocationJournal.Begin("AuditRegression");
            check(InvocationJournal.Native("query", () => 42) == 42, "journal preserves native return value");
            var original = new InvalidOperationException("secret must not be logged"); Exception? caught = null;
            try { InvocationJournal.Native<int>("readResult", () => throw original); } catch (Exception ex) { caught = ex; }
            var lines = File.ReadAllLines(Directory.GetFiles(root).Single());
            var entries = lines.Select(line => JsonNode.Parse(line)!).ToArray();
            check(ReferenceEquals(caught, original), "native journal rethrows original failure");
            check(entries.All(e => e["id"]!.GetValue<string>() == id), "native breadcrumbs correlate to enclosing tool invocation");
            check(entries.Last()["phase"]!.GetValue<string>() == "THREW" && entries[2]["phase"]!.GetValue<string>() == "RETURNED", "journal distinguishes return from readback failure");
            check(!string.Join("", lines).Contains("secret"), "journal excludes argument/content/exception text");
        }
        finally { Environment.SetEnvironmentVariable("TIA_MCP_DIAGNOSTICS_DIRECTORY", previous); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
