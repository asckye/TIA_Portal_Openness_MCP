using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Logic.V4;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcp.FoundationHost;

internal static class PlcCrossReferenceAnalysis
{
    internal static readonly Definition Definition =
        new("AnalyzePlcCrossReferences", "AnalyzePlcCrossReferences",
            "[L2][PLC-Software][READ] Export and analyse an ordinary PLC software or exact block group in one call, bounded by maxBlocks (1..256). Uses the existing XML export path and offline AnalyzePlcReferences engine, never native CrossReferenceService. Exports retained under the session output root. Returns callers, callees and global reference sites per indexed block, export failures and unindexed files. Scope states exported block count and omitted blocks; SCL bodies, unselected units, system blocks, indirect calls, HMI and external clients are not covered. Partial/empty is never proof of no references or safe deletion. Optional unitName selects a standard software unit V16–V21; scope lists the selected units. No compile, save or project change.",
            new[]{
            new Argument("softwarePath"),new Argument("groupPath","string",false,""),new Argument("recursive","boolean",false,true),
                new Argument("maxBlocks","integer",false,128),
                new Argument("unitName","string",false,"","Exact standard software unit, V16–V21; paths relative to its block root.")
        }, "CrossReferenceAnalysis");
    internal static FoundationTool Create(IFoundationWorker worker, string release = "21") => new(FoundationTools.ForRelease(Definition, release), worker);

    private sealed class OutputRoot
    {
        internal readonly string Path = System.IO.Path.Combine(TiaOpenness.Shared.DataLocations.Current.ReportsDirectory,
            "cross-references", Guid.NewGuid().ToString("N"));
    }
    private static readonly ConditionalWeakTable<IFoundationWorker, OutputRoot> Roots = new();

    internal static async Task<JsonObject> Run(IFoundationWorker worker, JsonObject values, CancellationToken token)
    {
        string software = (string)values["softwarePath"]!;
        int maximum = (int)values["maxBlocks"]!;
        ValidateMaximum(maximum);
        string directory = Path.Combine(Roots.GetValue(worker, _ => new OutputRoot()).Path, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var export = new JsonObject {
            ["unitName"] = values["unitName"]?.DeepClone() ?? JsonValue.Create(""), ["softwarePath"] = software, ["groupPath"] = values["groupPath"]!
                .DeepClone(),
            ["recursive"] = values["recursive"]!.DeepClone(), ["exportPath"] = directory, ["maxItems"] = maximum,
            ["dryRun"] = true, ["expectedInventoryHash"] = "" };
        var preview = await worker.Call("ExportBlocks", export, token).ConfigureAwait(false);
        BatchExportContract.Validate(preview, true);
        export["expectedInventoryHash"] = (string?)preview?["InventoryHash"] ?? throw new InvalidDataException("Export inventory hash missing.");
        export["dryRun"] = false;
        token.ThrowIfCancellationRequested();
        var result = await worker.Call("ExportBlocks", export, token).ConfigureAwait(false);
        BatchExportContract.Validate(result, false);
        var report = Report(software, directory, result!.AsObject());
        report["unitName"] = values["unitName"]?.DeepClone() ?? JsonValue.Create("");
        report["scope"] = "Selected units: " + ((string?)values["unitName"] is { Length: > 0 } unit ? unit : "none (PLC root)") + ". "
            + (string)report["scope"]!;
        return report;
    }

    private static void ValidateMaximum(int maxBlocks)
    {
        if (maxBlocks < 1 || maxBlocks > 256) throw new ArgumentOutOfRangeException(nameof(maxBlocks), "maxBlocks must be 1..256.");
    }

    internal static Envelope Result(string? release, string id, JsonObject report)
    {
        int indexingFailures = report["analysis"]?["failures"]?.AsArray().Count ?? 0;
        int indexed = report["analysis"]?["rows"]?.AsArray().Count ?? 0;
        bool incomplete = (bool?)report["exportComplete"] != true || report["analysisFailure"] != null || indexingFailures > 0;
        bool partial = incomplete && indexed > 0 && report["analysisFailure"] == null;
        var outcome = partial ? Outcome.Partial : incomplete ? Outcome.Failed : Outcome.Succeeded;
        var items = report["exports"]!.AsArray();
        int failed = items.Count(n => (string?)n?["Status"] == "failed");
        var error = partial ? new Error("Export or indexing did not complete; inspect the retained scope and item evidence.",
            new PartialFailureDetails(indexed, failed + indexingFailures,
                items.Count(n => (string?)n?["Status"] == "not-attempted")))
            : incomplete ? new Error("No reference analysis completed; inspect the retained export and indexing evidence.",
                new IoFailedDetails(report["analysisFailure"] == null ? "export-plc-blocks" : "index-plc-exports", (string?)report["exportDirectory"])) : null;
        var details = new Dictionary<string, JsonElement>();
        var warnings = new[] {
            new Warning(WarningCode.UnverifiedBehavior, "Native XML export acceptance is NOT RUN.", details),
            new Warning(WarningCode.IncompleteData, (string)report["scope"]!, details) };
        return Envelope.Create(report, error, new Meta(DateTimeOffset.UtcNow, release, "AnalyzePlcCrossReferences", id,
            outcome, partial ? Execution.Partial : Execution.Completed,
            (bool?)report["requiresSessionReset"] == true, BehaviorPolicy.Current, Completeness.Partial, null, warnings));
    }

    internal static JsonObject Report(string software, string directory, JsonObject export)
    {
        var items = export["Items"]!.AsArray();
        int exported = items.Count(n => (string?)n?["Status"] == "exported");
        JsonObject? analysis = null;
        string? failure = null;
        if (exported > 0)
        {
            try { analysis = PlcOfflineReferences.Analyze(directory, "blocks", "", 10, 0, 500,
                items.Where(n => (string?)n?["Status"] == "exported").Select(n => (string)n!["OutputFile"]!).ToArray()); }
            catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException) { failure = error.Message; }
        }
        return new JsonObject {
            ["softwarePath"] = software, ["groupPath"] = export["GroupPath"]?.DeepClone(), ["recursive"] = export["Recursive"]?.DeepClone(),
            ["exportDirectory"] = directory, ["selectedBlocks"] = items.Count, ["exportedBlocks"] = exported,
            ["exportComplete"] = (bool?)export["InventoryComplete"] == true && exported == items.Count,
            ["exports"] = items.DeepClone(), ["analysis"] = analysis, ["analysisFailure"] = failure,
            ["analysisQueried"] = analysis != null, ["nativeCrossReferencesQueried"] = false,
            ["requiresSessionReset"] = (bool?)export["RequiresSessionReset"] == true,
            ["coverageComplete"] = false, ["dataComplete"] = false, ["safeToDelete"] = false,
            ["scope"] = "Covers " + exported + " exported blocks of software " + software
                + "; blocks not exported are not analysed. Only selected root/user or software-unit block groups; other units and system blocks are excluded. "
                + "Only explicit SimaticML calls and global symbol sites are indexed; text SCL, indirect/dynamic calls, HMI and external clients are not " +
                    "covered. "
                + "Not queried or partial never means zero references; an empty result is not proof of no references."
        };
    }
}
