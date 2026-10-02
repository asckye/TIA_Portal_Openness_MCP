using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;

namespace TiaMcpServer.ModelContextProtocol
{
    public static partial class McpServer
    {
        [McpServerTool(Name = "ReadPlcBlockEditCapabilities"), Description("[L2][Validation][READ] Inspect one SimaticML PLC block file, or export an exact live block, to report each network's own language/source shape, existing multilingual title/comment targets, library binding and document fingerprint. Exactly one filePath or blockPath (+softwarePath). Describes the supported offline patch operations; never claims a generic rung editor or target CPU/native import validation. No compile/save/write to project.")]
        public static ResponseMessage ReadPlcBlockEditCapabilities(
            [Description("Absolute exported SimaticML XML; mutually exclusive with blockPath.")] string filePath = "",
            [Description("Exact PLC software path when exporting a live block.")] string softwarePath = "",
            [Description("Exact group-qualified live block path; empty for file-only mode.")] string blockPath = "")
            => RunOfflineAnalysisTool("ReadPlcBlockEditCapabilities", meta => {
                string? temp = null;
                try {
                    meta["offlineOnly"] = !string.IsNullOrWhiteSpace(filePath);
                    var path = ResolveCompareSide("document", filePath, blockPath, softwarePath, meta, out temp);
                    meta["data"] = PlcDocumentEditing.Inspect(PlcDocumentEditing.Read(path));
                    return "Exported block editing capabilities inspected. Network indexes are zero-based; native import remains unverified.";
                } finally { DeleteAnalysisTempDir(temp); }
            });

        [McpServerTool(Name = "AnalyzePlcReferences"), Description("[L2][Validation][OFFLINE] Query supplied exports without native CrossReferenceService. action summary/callers/callees/callPaths/unreachable/references. Recursively indexes single-block SimaticML XML (max 2000 files / 64 MiB); SCL/S7DCL and invalid files are reported unindexed. target is exact block name or relative file ID; references takes a quoted component path e.g. \"Motor\".\"Run\". Uses explicit CallInfo and global Symbol nodes only. Reports missing/ambiguous callees, cycles, truncation and failures; coverageComplete and safeToDelete are always false. An empty result is not proof of no references. No TIA calls or writes.")]
        public static ResponseMessage AnalyzePlcReferences(
            [Description("Absolute directory of exports from one PLC/scope, scanned recursively.")] string directory,
            [Description("summary, callers, callees, callPaths, unreachable or references.")] string action = "summary",
            [Description("Exact block name or relative file ID; references uses quoted symbol components.")] string target = "",
            [Description("Maximum call-path edge depth, 1..50.")] int maxDepth = 10,
            [Description("Zero-based result offset.")] int offset = 0,
            [Description("Page size, 1..500.")] int limit = 100)
            => RunOfflineAnalysisTool("AnalyzePlcReferences", meta => {
                meta["data"] = PlcOfflineReferences.Analyze(directory, action, target, maxDepth, offset, limit);
                meta["dataComplete"] = false;
                return "Partial offline reference query completed. Inspect failures, unresolvedCalls and scope; never use it alone to justify deletion.";
            });

        [McpServerTool(Name = "PatchPlcBlockDocument"), Description("[L2][PLC-Builders][FILE] Patch an exported single-block SimaticML file into a NEW file, preserving all unrelated XML. Requires expectedFingerprint from ReadPlcBlockEditCapabilities. changesJson array: setBlockText(field Title/Comment,culture,expectedValue,value), setNetworkText(same plus zero-based networkIndex), setMemberStartValue(section,memberPath slash-separated,expectedValue,value). Only existing scalar StartValue or existing multilingual entries; no guessed cultures/defaults, library-connected blocks or read-only members. Default dryRun=true previews; false writes outputPath. No native calls/import/compile; input remains unchanged.")]
        public static ResponseMessage PatchPlcBlockDocument(
            [Description("Absolute existing single-block SimaticML XML file.")] string filePath,
            [Description("Array of exact setBlockText/setNetworkText/setMemberStartValue operations with expectedValue and value.")] string changesJson,
            [Description("documentFingerprint returned by ReadPlcBlockEditCapabilities for this input.")] string expectedFingerprint,
            [Description("New absolute output file; existing parent directory required when dryRun=false.")] string outputPath = "",
            [Description("Preview only by default; false writes a new file, never imports into TIA.")] bool dryRun = true)
            => RunOfflineAnalysisTool("PatchPlcBlockDocument", meta => {
                var edited = PlcDocumentEditing.Patch(PlcDocumentEditing.Read(filePath), changesJson, expectedFingerprint);
                meta["dryRun"] = dryRun; meta["result"] = PlcDocumentEditing.Inspect(edited);
                meta["nativeImportValidated"] = false;
                if (!dryRun) {
                    if (string.IsNullOrWhiteSpace(outputPath)) throw new ArgumentException("New absolute outputPath required.");
                    var output = Siemens.NativeFileOutput.Plan(outputPath);
                    using (var stream = new FileStream(output.FullName, FileMode.CreateNew, FileAccess.Write))
                    using (var writer = new StreamWriter(stream, new UTF8Encoding(true))) writer.Write(edited);
                    meta["output"] = Siemens.NativeFileOutput.Verify(output);
                }
                return dryRun ? "Offline patch preview validated. No file or project changed." : "Patched document written; native import/compilation remain unverified.";
            });

        [McpServerTool(Name = "ImportPlcBlockVerified"), Description("[L2][PLC-Software][WRITE] Overwrite one existing non-library PLC block in its exact current user group with a reviewed SimaticML file. Default dryRun=true exports a retained backup, preserves omitted scalar block attributes and returns planned.xml plus binding/content token. Execution requires SAME arguments and expectedToken; re-exports current block, rechecks binding/content before import, then re-exports for strict document verification preserving wiring and literal values. Requires Offline and consistent exports. Refuses library-bound blocks, InstanceDB, changed name/type/number/layout/language and empty replacement logic. Missing interface members/networks still mean deletion. compileAfterImport=false by default; true explicitly compiles the imported block before readback, rejecting compile errors. Without compile, inconsistent readback remains failed/unverified. No save/download/rollback/retry; failures may leave changes. Native V20/V21 execution not yet validated.")]
        public static ResponseMessage ImportPlcBlockVerified(
            [Description("Exact PLC software path.")] string softwarePath,
            [Description("Exact existing block path relative to Program blocks, e.g. Pumps/FB_Pump.")] string blockPath,
            [Description("Absolute reviewed single-block SimaticML XML; name must equal the target.")] string importPath,
            [Description("Absolute local folder for retained before/planned/after evidence, one new subdirectory per invocation.")] string evidenceDirectory,
            [Description("True exports a backup and preview only; false attempts the native import.")] bool dryRun = true,
            [Description("Token returned by the matching preview; binds session, project, target, current document, candidate and compile option.")] string expectedToken = "",
            [Description("Explicitly compile only the imported block before readback; default false. Compilation is never run in preview.")] bool compileAfterImport = false)
            => Portal.ImportPlcBlockVerified(softwarePath, blockPath, importPath, evidenceDirectory, dryRun, expectedToken, compileAfterImport);
    }
}
