using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.Logic.Generation;
using TiaMcp.Logic.V4;
using TiaMcp.Logic.V4.Inputs;

namespace TiaMcpServer.ModelContextProtocol
{
    [McpServerToolType]
    internal sealed class GenerationTools
    {
        private readonly Func<StandardPackageStore> store;
        private readonly string release;
        internal GenerationTools(string release, Func<StandardPackageStore>? store = null)
        { this.release = release; this.store = store ?? StandardPackageStore.Create; }

        [McpServerTool(Name = "ListStandardPackages"), Description("[L1][Project][READ] List validated repository and user standard packages with exact id/version, canonical content hash, inventory and release targets. Repository templates/standards is read-only; user packages live under bundle data/standards. Offline on all eight releases; no Openness calls.")]
        public CallToolResult ListStandardPackages() => Run("ListStandardPackages", false, s => s.List());

        [McpServerTool(Name = "ManageStandardPackage"), Description("[L1][Project][FILE] Offline standard package import (directory or bounded zip), export (new canonical zip), copy/fork (new id and version), or remove (user packages only). Exact identities; no overwrite. Source/licence notices and immutable inventories retained; JSON canonicalized, resource bytes preserved. Preview defaults true; writes require dryRun=false and expectedPlanHash from preview, with existing approval/audit. Zip limits: 1024 files, 2048 entries, 16 MiB/file, 64 MiB total, 32 MiB archive. No TIA calls.")]
        public CallToolResult ManageStandardPackage(
            [Description("import, export, copy, fork or remove.")] string action,
            [Description("Exact installed package id; empty for import.")] string packageId = "",
            [Description("Exact installed SemVer version; empty for import.")] string version = "",
            [Description("Existing absolute local package directory or zip; import only.")] string sourcePath = "",
            [Description("New absolute local .zip path under an existing parent; export only.")] string outputPath = "",
            [Description("New package id; copy/fork only, distinct from the original.")] string newId = "",
            [Description("Explicit new SemVer version; copy/fork only.")] string newVersion = "",
            [Description("true previews; false executes the file operation after approval.")] bool dryRun = true,
            [Description("Exact planHash returned by a fresh preview; required for execution.")] string expectedPlanHash = "")
            => Run("ManageStandardPackage", !dryRun, s => s.Manage(action, packageId, version, sourcePath, outputPath, newId, newVersion, dryRun, expectedPlanHash));

        [McpServerTool(Name = "ValidateStandardPackage"), Description("[L1][Project][OFFLINE] Validate an installed package or local directory/zip: bounded loader, schema, cross-references, inventory hashes, restricted declarations, pinned dependencies/inheritance and release implementation coverage. Coverage is reported separately from validity; native acceptance NOT RUN. No writes or Openness calls.")]
        public CallToolResult ValidateStandardPackage(
            [Description("Exact installed package id, or empty when sourcePath is supplied.")] string packageId = "",
            [Description("Exact installed SemVer version, or empty for sourcePath.")] string version = "",
            [Description("Optional existing absolute package directory/zip instead of an installed identity.")] string sourcePath = "")
            => Run("ValidateStandardPackage", false, s => sourcePath.Length != 0 && (packageId.Length != 0 || version.Length != 0)
                ? throw new ArgumentException("Choose sourcePath or packageId/version.", "sourcePath") : s.Validate(packageId, version, sourcePath));

        [McpServerTool(Name = "DescribeStandardPackage"), Description("[L1][Project][READ] Describe exact installed standard package and resolved inheritance: device parameter schemas/signals/emits, naming rules, library implementations, release coverage and source/licence notices. Exposes basic programAlarm=false for S7-1200; true selects the isolated S7-1500 adapter, with native CPU/firmware acceptance NOT RUN. Offline, no writes or Openness calls.")]
        public CallToolResult DescribeStandardPackage(
            [Description("Exact installed package id.")] string packageId,
            [Description("Exact installed SemVer version.")] string version)
            => Run("DescribeStandardPackage", false, s => s.Describe(packageId, version));

        [McpServerTool(Name = "ManageMachineDescription"), Description("[L1][Project][FILE] Offline validate/import/export/exportTemplate for canonical MachineDescription JSON. Pass machine as a structured object. CSV uses a package-generated directory with Machine, Stations, Units, Devices and Lists.csv (UTF-8 BOM, LF, quoted cells); Param cells are JSON values, IO cells addresses/auto. CPU-specific programAlarm selection is validated. import without outputPath and validate only return JSON; file writes preview by default and require expectedPlanHash plus approval. Outputs never overwrite. JSON input <=4 MiB; CSV <=4 MiB/file,16 MiB total,10000 rows. xlsx deferred. No TIA calls.")]
        public CallToolResult ManageMachineDescription(
            [Description("validate, import, export or exportTemplate.")] string action,
            [Description("Exact installed package id.")] string packageId,
            [Description("Exact installed SemVer version.")] string version,
            [Description("Structured tiamcp.machine/1 JSON for validate/export; omitted for import/exportTemplate.")] ToolArguments? machine = null,
            [Description("Existing absolute JSON file or package-generated CSV directory; import only.")] string inputPath = "",
            [Description("Optional new absolute .json file for import, required new .json file or CSV directory for export/template.")] string outputPath = "",
            [Description("json or csv; xlsx is deferred. exportTemplate requires csv.")] string format = "json",
            [Description("true previews; false executes file writes after approval.")] bool dryRun = true,
            [Description("Exact planHash from fresh preview; required for file execution.")] string expectedPlanHash = "")
            => Run("ManageMachineDescription", !dryRun && outputPath.Length != 0,
                s => s.Machine(action, packageId, version, machine?.Json.GetRawText() ?? "", inputPath, outputPath, format, dryRun, expectedPlanHash));

        private CallToolResult Run(string name, bool writes, Func<StandardPackageStore, JsonObject> operation)
        {
            JsonObject? data = null; Error? error = null;
            var outcome = Outcome.Succeeded; var execution = writes ? Execution.Completed : Execution.ReadOnly;
            try { data = operation(store()); }
            catch (GenerationValidationException ex)
            {
                data = new JsonObject { ["valid"] = false, ["errors"] = new JsonArray(ex.Errors.Select(e => (JsonNode)new JsonObject { ["path"] = e.Path, ["rule"] = e.Rule, ["message"] = e.Message }).ToArray()) };
                error = new Error("Generation document validation failed.", new InvalidArgumentDetails(ex.Errors.First().Path, Array.Empty<string>()));
                outcome = Outcome.RejectedBeforeOperation; execution = Execution.NotStarted;
            }
            catch (ArgumentException ex)
            {
                error = new Error(ex.Message, new InvalidArgumentDetails(ex.ParamName ?? "arguments", Array.Empty<string>()));
                outcome = Outcome.RejectedBeforeOperation; execution = Execution.NotStarted;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                data = new JsonObject { ["mayHaveWrittenFiles"] = writes, ["diagnostic"] = ex.Message };
                error = new Error("The local generation file operation failed; inspect retained pending outputs before retrying.", new IoFailedDetails("standard-package", null));
                outcome = writes ? Outcome.Partial : Outcome.ReadFailed; execution = writes ? Execution.Partial : Execution.ReadOnly;
            }
            var envelope = Envelope.Create(data, error, new Meta(DateTimeOffset.UtcNow, release, name,
                Meta.Correlate(TiaOpenness.Shared.AuditInvocation.CurrentRequestId), outcome, execution, false, BehaviorPolicy.NotApplicable,
                outcome == Outcome.Succeeded ? Completeness.Complete : outcome == Outcome.Partial ? Completeness.Partial : Completeness.None, null, Array.Empty<Warning>()));
            var result = McpResult.From(envelope);
            return new CallToolResult { IsError = result.IsError, StructuredContent = JsonNode.Parse(result.StructuredContent.GetRawText()),
                Content = new[] { new TextContentBlock { Text = result.Content[0].Text } } };
        }
    }
}
