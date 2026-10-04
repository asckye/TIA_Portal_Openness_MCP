using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml.Linq;
using TiaMcpServer.Siemens;

namespace TiaMcpServer.ModelContextProtocol
{
    public static partial class McpServer
    {

        /// <summary>
        /// Shared body of the compile-and-diagnose tools: compile one software and return the
        /// CompilerResult flattened into structured errors/warnings. The PLC and HMI tools differ
        /// only in the software they are pointed at, so they must not diverge here.
        /// </summary>
        private static ResponseCompileDiagnose CompileAndDiagnoseCore(string softwarePath, string password)
        {
            try
            {
                var compileWatch = System.Diagnostics.Stopwatch.StartNew();
                var result = EngineServices.Get<Siemens.Portal>().CompileSoftware(softwarePath, password);

                // CollectCompilerMessages 逐条收集诊断，拿不到的记进 CollectFailures，
                // 让调用方区分没有明细与诊断收集不完整。
                var compileMs = compileWatch.ElapsedMilliseconds;
                var collected = CollectCompilerMessages(result.Messages);
                var summary = collected.Summary(result.State.ToString(), result.ErrorCount, result.WarningCount);
                summary["compileElapsedMs"] = compileMs;
                summary["softwarePath"] = softwarePath;
                summary["timestamp"] = DateTime.Now;
                var raw = collected.Raw;
                var errs = collected.Errors;
                var warns = collected.Warnings;
                var info = collected.Info;

                if (collected.CollectFailures.Count > 0)
                {
                    // 放进 info 让人/模型直接看见，别只藏在 meta 里。
                    info = new List<string>(info);
                    foreach (var f in collected.CollectFailures)
                        info.Add("State=Information; Description=[诊断收集不完整] " + f);
                }

                return new ResponseCompileDiagnose
                {
                    Message = $"Software '{softwarePath}' compile state={summary["effectiveState"]}; root counts and diagnostics scopes are in Meta.",
                    State = summary["effectiveState"]!.ToString(),
                    ErrorCount = collected.HasError && result.ErrorCount == 0 ? (int?)null : result.ErrorCount,
                    WarningCount = collected.HasWarning && result.WarningCount == 0 ? (int?)null : result.WarningCount,
                    Errors = errs,
                    Warnings = warns,
                    Info = info,
                    RawMessages = raw,
                    Meta = summary
                };
            }
            catch (PortalException pex)
            {
                throw new McpException($"Failed compiling software '{softwarePath}' [{pex.Code}]: {pex.Message}", pex, McpErrorCode.InternalError);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error compiling software '{softwarePath}': {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        private static string ClassifyPlcXml(string file, out string subKind, out string objectName)
        {
            subKind = "";
            objectName = Path.GetFileNameWithoutExtension(file);
            try
            {
                var doc = XDocument.Load(file);
                var obj = doc.Root?.Elements().FirstOrDefault(e =>
                    e.Name.LocalName.StartsWith("SW.Types.", StringComparison.OrdinalIgnoreCase) ||
                    e.Name.LocalName.StartsWith("SW.Tags.", StringComparison.OrdinalIgnoreCase) ||
                    e.Name.LocalName.StartsWith("SW.Blocks.", StringComparison.OrdinalIgnoreCase) ||
                    e.Name.LocalName.StartsWith("SW.TechnologicalObjects.", StringComparison.OrdinalIgnoreCase));

                var local = obj?.Name.LocalName ?? "";
                subKind = local;
                var name = obj?.Element("AttributeList")?.Element("Name")?.Value;
                if (!string.IsNullOrWhiteSpace(name))
                {
                    objectName = name!.Trim();
                }
                if (local.StartsWith("SW.Types.", StringComparison.OrdinalIgnoreCase)) return "type";
                if (string.Equals(local, "SW.Tags.PlcTagTable", StringComparison.OrdinalIgnoreCase)) return "tagtable";
                if (local.StartsWith("SW.Blocks.", StringComparison.OrdinalIgnoreCase)) return "block";
                if (local.StartsWith("SW.TechnologicalObjects.", StringComparison.OrdinalIgnoreCase)) return "technology";
            }
            catch
            {
 /* swallow(parse-fallback): Unreadable XML remains unknown so the batch import reports or skips it using its existing classification policy. */                subKind = "";
            }

            return "unknown";
        }

        private static ResponsePlcProgramImport BuildPlcProgramImportResponse(
            string sourceDir,
            bool dryRun,
            List<string> discoveredTypes,
            List<string> discoveredTagTables,
            List<string> discoveredTechnologyObjects,
            List<string> discoveredBlocks,
            List<string> importedTypes,
            List<string> importedTagTables,
            List<string> importedTechnologyObjects,
            List<string> importedBlocks,
            List<ImportFailure> failed,
            ResponseCompile? compile)
        {
            var compileOk = compile == null || (compile.Meta?["success"]?.GetValue<bool>() ?? false);
            var success = failed.Count == 0 && compileOk;
            return new ResponsePlcProgramImport
            {
                Message = dryRun
                    ? $"PLC program dry-run from '{sourceDir}': types={discoveredTypes.Count}, tagTables={discoveredTagTables.Count}, technologyObjects={discoveredTechnologyObjects.Count}, blocks={discoveredBlocks.Count}, failed={failed.Count}"
                    : $"PLC program import from '{sourceDir}': types={importedTypes.Count}, tagTables={importedTagTables.Count}, technologyObjects={importedTechnologyObjects.Count}, blocks={importedBlocks.Count}, failed={failed.Count}, compileState={compile?.State ?? "-"}",
                DryRun = dryRun,
                DiscoveredTypes = discoveredTypes,
                DiscoveredTagTables = discoveredTagTables,
                DiscoveredTechnologyObjects = discoveredTechnologyObjects,
                DiscoveredBlocks = discoveredBlocks,
                ImportedTypes = importedTypes,
                ImportedTagTables = importedTagTables,
                ImportedTechnologyObjects = importedTechnologyObjects,
                ImportedBlocks = importedBlocks,
                Failed = failed,
                Compile = compile,
                Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = success, ["importOrdering"] = ImportSelectionPolicy.OrderingDescription, ["dependencyResolution"] = false }
            };
        }

        // Shared tool infrastructure remains with its existing owners during domain migration.
        internal static class PlcBlockToolSupport
        {
            internal static ResponseCompile BuildCompileResponse(string softwarePath, object result)
                => McpServer.BuildCompileResponse(softwarePath, result);
            internal static string ClassifyPlcXml(string file, out string subKind, out string objectName)
                => McpServer.ClassifyPlcXml(file, out subKind, out objectName);
            internal static ResponsePlcProgramImport BuildPlcProgramImportResponse(string sourceDir, bool dryRun,
                List<string> discoveredTypes, List<string> discoveredTagTables, List<string> discoveredTechnologyObjects, List<string> discoveredBlocks,
                List<string> importedTypes, List<string> importedTagTables, List<string> importedTechnologyObjects, List<string> importedBlocks,
                List<ImportFailure> failed, ResponseCompile? compile)
                => McpServer.BuildPlcProgramImportResponse(sourceDir, dryRun, discoveredTypes, discoveredTagTables, discoveredTechnologyObjects,
                    discoveredBlocks, importedTypes, importedTagTables, importedTechnologyObjects, importedBlocks, failed, compile);
            internal static Microsoft.Extensions.Logging.ILogger? Logger => McpServer.Logger;
            internal static string BuildBlockDidYouMean(string softwarePath, string blockPath)
                => McpServer.BuildBlockDidYouMean(softwarePath, blockPath);
            internal static ResponseCompileDiagnose CompileAndDiagnoseCore(string softwarePath, string password)
                => McpServer.CompileAndDiagnoseCore(softwarePath, password);
            internal static string ResolveCompareSide(string side, string filePath, string blockPath, string softwarePath, JsonObject meta, out string? temp)
                => McpServer.ResolveCompareSide(side, filePath, blockPath, softwarePath, meta, out temp);
            internal static void DeleteAnalysisTempDir(string? temp) => McpServer.DeleteAnalysisTempDir(temp);
        }
    }
}
