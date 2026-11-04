using TiaMcp.Logic.V4.Hmi;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
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
    public sealed partial class OfflineSuiteTools
    {

        [McpServerTool(Name = "BuildUnifiedHmiTemplateApplyDesign"),
                Description("[L2][HMI-Unified]Offline-only helper: convert one Unified HMI template JSON file into the execution JSON accepted by " +
                "ApplyUnifiedHmiScreenDesign, with layout QA attached. It does not connect to TIA Portal or modify projects.")]
        public CallToolResult BuildUnifiedHmiTemplateApplyDesignJsonV4(
            [Description("templateFile: full path to a Unified HMI template JSON file")] string templateFile,
            [Description("fallbackWidth: width used only if the template omits Screen.Width")] int fallbackWidth = 800,
            [Description("fallbackHeight: height used only if the template omits Screen.Height")] int fallbackHeight = 480)
            => OfflineContracts
                .Run("BuildUnifiedHmiTemplateApplyDesign", () => BuildUnifiedHmiTemplateApplyDesignJson(templateFile, fallbackWidth, fallbackHeight), writes
                    : false, current: false);

        public ResponseJsonReport BuildUnifiedHmiTemplateApplyDesignJson(
            [Description("templateFile: full path to a Unified HMI template JSON file")] string templateFile,
            [Description("fallbackWidth: width used only if the template omits Screen.Width")] int fallbackWidth = 800,
            [Description("fallbackHeight: height used only if the template omits Screen.Height")] int fallbackHeight = 480)
        {
            try
            {
                var layout = HmiTemplateLayoutAnalyzer.AnalyzeFile(templateFile, path =>
                {
                    var templateRoot = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
                    var expectedItems = (templateRoot?["Items"] as JsonArray ?? templateRoot?["items"] as JsonArray ?? new JsonArray()).Count;
                    var design = HmiTemplateDesignJsonBuilder.BuildApplyDesign(path, fallbackWidth, fallbackHeight);
                    return design["items"] is JsonArray executionItems && executionItems.Count == expectedItems;
                });
                var designJson = HmiTemplateDesignJsonBuilder.BuildApplyDesign(templateFile, fallbackWidth, fallbackHeight);
                var ok = string.Equals(layout["status"]?.ToString(), "pass", StringComparison.OrdinalIgnoreCase);
                var itemCount = (designJson["items"] as JsonArray)?.Count ?? 0;
                return new ResponseJsonReport
                {
                    Ok = ok,
                    Message = ok ? "Unified HMI template execution design JSON built offline"
                        : "Unified HMI template execution design JSON built with blocking layout findings",
                    // envelope: legacy-roundtrip-data-stamp
                    Data = new JsonObject
                    {
                        ["format"] = "tia-unified-hmi-template-apply-design-offline-v1",
                        ["timestamp"] = DateTime.Now.ToString("O"),
                        ["offlineOnly"] = true,
                        ["templateFile"] = templateFile,
                        ["ok"] = ok,
                        ["itemCount"] = itemCount,
                        ["layoutQa"] = layout,
                        ["applyDesign"] = designJson
                    },
                    Meta = ResponseMeta.Basic(DateTime.Now, ok, ("offlineOnly", true), ("itemCount", itemCount))
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error building Unified HMI template execution design JSON: {ex.Message}{McpHints.Recovery(ex)}", ex,
                        McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "BuildUnifiedHmiTemplateApplyDesignManifest"),
                Description("[L2][HMI-Unified]Offline-only helper: build a directory-level manifest for Unified HMI templates. It summarizes layout QA and " +
                "execution-design readiness for every unified_*.json template without returning full apply payloads. It does not connect to TIA Portal or " +
                "modify projects.")]
        public CallToolResult BuildUnifiedHmiTemplateApplyDesignManifestV4(
            [Description("templateDirectory: directory containing Unified HMI JSON templates")] string templateDirectory,
            [Description("fallbackWidth: width used only if a template omits Screen.Width")] int fallbackWidth = 800,
            [Description("fallbackHeight: height used only if a template omits Screen.Height")] int fallbackHeight = 480)
            => OfflineContracts
                .Run("BuildUnifiedHmiTemplateApplyDesignManifest", () =>
                    BuildUnifiedHmiTemplateApplyDesignManifest(templateDirectory, fallbackWidth, fallbackHeight), writes: false, current: false);

        public ResponseJsonReport BuildUnifiedHmiTemplateApplyDesignManifest(
            [Description("templateDirectory: directory containing Unified HMI JSON templates")] string templateDirectory,
            [Description("fallbackWidth: width used only if a template omits Screen.Width")] int fallbackWidth = 800,
            [Description("fallbackHeight: height used only if a template omits Screen.Height")] int fallbackHeight = 480)
        {
            try
            {
                var files = Directory.Exists(templateDirectory)
                    ? Directory.GetFiles(templateDirectory, "*.json", SearchOption.TopDirectoryOnly)
                        .Where(path => Path.GetFileName(path).StartsWith("unified_", StringComparison.OrdinalIgnoreCase))
                        .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                        .ToArray()
                    : Array.Empty<string>();
                var rows = new JsonArray();
                var referenceAnalysis = HmiTemplateReferenceAnalyzer.Analyze(templateDirectory, "", "");
                var referenceRows = (referenceAnalysis["templates"] as JsonArray ?? new JsonArray())
                    .OfType<JsonObject>()
                    .ToDictionary(
                        x => x["templateName"]?.ToString() ?? "",
                        x => x,
                        StringComparer.OrdinalIgnoreCase);
                var referenceRowsByFile = (referenceAnalysis["templates"] as JsonArray ?? new JsonArray())
                    .OfType<JsonObject>()
                    .Where(x => !string.IsNullOrWhiteSpace(x["file"]?.ToString()))
                    .ToDictionary(
                        x => Path.GetFullPath(x["file"]?.ToString() ?? ""),
                        x => x,
                        StringComparer.OrdinalIgnoreCase);
                foreach (var file in files)
                {
                    var templateName = Path.GetFileNameWithoutExtension(file);
                    referenceRowsByFile.TryGetValue(Path.GetFullPath(file), out var referenceRow);
                    if (referenceRow == null)
                    {
                        referenceRows.TryGetValue(templateName, out referenceRow);
                    }
                    rows.Add(BuildUnifiedHmiTemplateApplyDesignManifestRow(file, fallbackWidth, fallbackHeight, referenceRow));
                }

                var failed = rows.OfType<JsonObject>().Count(x => x["ok"]?.GetValue<bool>() != true);
                var totalItems = rows.OfType<JsonObject>().Sum(x => x["itemCount"]?.GetValue<int>() ?? 0);
                var root = new JsonObject
                {
                    ["format"] = "tia-unified-hmi-template-apply-design-manifest-v1",
                    ["timestamp"] = DateTime.Now.ToString("O"),
                    ["offlineOnly"] = true,
                    ["templateDirectory"] = templateDirectory,
                    ["templateCount"] = files.Length,
                    ["failed"] = failed,
                    ["totalItems"] = totalItems,
                    ["ok"] = failed == 0,
                    ["policy"] = new JsonObject
                    {
                        ["fullPayloadTool"] = "BuildUnifiedHmiTemplateApplyDesign",
                        ["applyTool"] = "ApplyUnifiedHmiScreenDesign",
                        ["rule"] = "Use this manifest as a pre-apply gate; inspect a full payload for any template before writing it to TIA."
                    },
                    ["templates"] = rows
                };

                var ok = failed == 0;
                return new ResponseJsonReport
                {
                    Ok = ok,
                    Message = ok ? "Unified HMI template execution design manifest built offline"
                        : "Unified HMI template execution design manifest has blocking findings",
                    Data = root,
                    // envelope: legacy-multiple-dynamic-fields
                    Meta = new JsonObject
                    {
                        ["timestamp"] = DateTime.Now,
                        ["success"] = ok,
                        ["offlineOnly"] = true,
                        ["templateCount"] = files.Length,
                        ["failed"] = failed,
                        ["totalItems"] = totalItems
                    }
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error building Unified HMI template execution design manifest: {ex.Message}{McpHints.Recovery(ex)}", ex,
                        McpErrorCode.InternalError);
            }
        }

        private static JsonObject BuildUnifiedHmiTemplateApplyDesignManifestRow(string templateFile, int fallbackWidth, int fallbackHeight, JsonObject
                ? referenceRow)
        {
            var layout = HmiTemplateLayoutAnalyzer.AnalyzeFile(templateFile, path =>
            {
                var templateRoot = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
                var expectedItems = (templateRoot?["Items"] as JsonArray ?? templateRoot?["items"] as JsonArray ?? new JsonArray()).Count;
                var design = HmiTemplateDesignJsonBuilder.BuildApplyDesign(path, fallbackWidth, fallbackHeight);
                return design["items"] is JsonArray executionItems && executionItems.Count == expectedItems;
            });
            var designJson = HmiTemplateDesignJsonBuilder.BuildApplyDesign(templateFile, fallbackWidth, fallbackHeight);
            var items = designJson["items"] as JsonArray ?? new JsonArray();
            var errors = layout["errors"] as JsonArray ?? new JsonArray();
            var warnings = layout["warnings"] as JsonArray ?? new JsonArray();
            var ok = string.Equals(layout["status"]?.ToString(), "pass", StringComparison.OrdinalIgnoreCase);
            var eventReadiness = BuildUnifiedHmiTemplateEventReadiness(referenceRow);
            var row = new JsonObject
            {
                ["templateFile"] = templateFile,
                ["templateName"] = layout["templateName"]?.DeepClone(),
                ["ok"] = ok,
                ["status"] = layout["status"]?.DeepClone(),
                ["width"] = designJson["width"]?.DeepClone(),
                ["height"] = designJson["height"]?.DeepClone(),
                ["itemCount"] = items.Count,
                ["errorCount"] = errors.Count,
                ["warningCount"] = warnings.Count,
                ["errors"] = new JsonArray(errors.Select(x => x?.DeepClone()).ToArray()),
                ["warnings"] = new JsonArray(warnings.Select(x => x?.DeepClone()).ToArray()),
                ["layoutDensity"] = layout["layoutDensity"]?.DeepClone(),
                ["applyDesignReady"] = layout["executionJsonChecked"]?.DeepClone(),
                ["requiredTagCount"] = GetManifestInt(referenceRow, "requiredTagCount"),
                ["dynamizationCount"] = GetManifestInt(referenceRow, "dynamizationCount"),
                ["actionCount"] = GetManifestInt(referenceRow, "actionCount"),
                ["eventReadiness"] = eventReadiness,
                ["recommendedNextAction"] = ok
                    ? "Inspect full payload with BuildUnifiedHmiTemplateApplyDesign, then apply in a temporary TIA project before using a real project."
                    : "Fix blocking layout/template findings before applying to TIA."
            };
            row["eventRecommendedNextAction"] = eventReadiness["recommendedNextAction"]?.DeepClone();
            return row;
        }

        private static JsonObject BuildUnifiedHmiTemplateEventReadiness(JsonObject? referenceRow)
        {
            if (referenceRow == null)
            {
                return new JsonObject
                {
                    ["status"] = "not-analyzed",
                    ["effectiveActionCount"] = 0,
                    ["safeDeterministicActionCount"] = 0,
                    ["apiDiscoveryRequiredCount"] = 0,
                    ["highRiskActionCount"] = 0,
                    ["todoActionCount"] = 0,
                    ["missingTargetCount"] = 0,
                    ["duplicateActionCount"] = 0,
                    ["commandActionCount"] = 0,
                    ["navigationActionCount"] = 0,
                    ["recommendedNextAction"] = "Run HmiTemplateReferenceAnalyzer first; event and binding readiness could not be joined for this template."
                };
            }

            var summary = referenceRow["actionRecipeSummary"] as JsonObject ?? new JsonObject();
            var effectiveRecipes = summary["effectiveRecipes"] as JsonArray ?? new JsonArray();
            var generated = new JsonArray();
            var safeDeterministic = 0;
            var apiDiscovery = 0;
            var todo = 0;
            var blocked = 0;
            var command = 0;
            var navigation = 0;

            foreach (var recipeNode in effectiveRecipes.OfType<JsonObject>())
            {
                var targetTags = (recipeNode["targetTags"] as JsonArray ?? new JsonArray()).Select(x => x?.ToString() ?? "");
                var built = HmiActionScriptRecipeBuilder.Build(
                    recipeNode["recipeKind"]?.ToString() ?? "",
                    recipeNode["event"]?.ToString() ?? "",
                    targetTags,
                    recipeNode["targetScreen"]?.ToString() ?? "",
                    recipeNode["targetPopup"]?.ToString() ?? "");
                generated.Add(built);
                var kind = built["recipeKind"]?.ToString() ?? "";
                var safety = built["safetyLevel"]?.ToString() ?? "";
                if (string.Equals(safety, "command", StringComparison.OrdinalIgnoreCase)) command++;
                if (string.Equals(safety, "navigation", StringComparison.OrdinalIgnoreCase)) navigation++;
                if (built["requiresApiDiscovery"]?.GetValue<bool>() == true) apiDiscovery++;
                if (built["applyBlocked"]?.GetValue<bool>() == true || !string.IsNullOrWhiteSpace(built["applyBlockedReason"]?.ToString())) blocked++;
                if ((built["script"]?.ToString() ?? "").IndexOf("TODO", StringComparison.OrdinalIgnoreCase) >= 0) todo++;
                if (built["ok"]?.GetValue<bool>() == true
                    && built["requiresApiDiscovery"]?.GetValue<bool>() != true
                    && (kind.Equals("set-bit", StringComparison.OrdinalIgnoreCase)
                        || kind.Equals("reset-bit", StringComparison.OrdinalIgnoreCase)
                        || kind.Equals("toggle-bit", StringComparison.OrdinalIgnoreCase)))
                {
                    safeDeterministic++;
                }
            }

            var missingTargets = summary["missingTargets"] as JsonArray ?? new JsonArray();
            var duplicateActions = summary["duplicateActions"] as JsonArray ?? new JsonArray();
            var highRisk = GetManifestInt(summary, "highRiskWrites");
            var missingRequiredTags = summary["missingRequiredTags"] as JsonArray ?? new JsonArray();
            var status = "ready-for-temp-project-validation";
            var recommended =
                "Generate safe deterministic scripts, then verify HMI tags, PLC-side symbols, TIA SyntaxCheck, and readback in a temporary project.";

            if (missingRequiredTags.Count > 0 || missingTargets.Count > 0 || duplicateActions.Count > 0)
            {
                status = "needs-template-fix";
                recommended = "Fix missing action tags, missing target screens/popups, or duplicate actions before applying events.";
            }
            else if (highRisk > 0)
            {
                status = "blocked-by-high-risk-actions";
                recommended =
                    "High-risk value writes require explicit operator confirmation, range validation, SyntaxCheck, and readback before any apply path is " +
                    "enabled.";
            }
            else if (apiDiscovery > 0 || todo > 0)
            {
                status = "needs-api-discovery";
                recommended = "Keep API-discovery actions blocked until the exact WinCC Unified V21 event/navigation/popup API is verified from TIA readback.";
            }

            return new JsonObject
            {
                ["status"] = status,
                ["requiredTagCount"] = GetManifestInt(referenceRow, "requiredTagCount"),
                ["dynamizationCount"] = GetManifestInt(referenceRow, "dynamizationCount"),
                ["actionCount"] = GetManifestInt(referenceRow, "actionCount"),
                ["effectiveActionCount"] = GetManifestInt(summary, "effectiveActionCount"),
                ["safeDeterministicActionCount"] = safeDeterministic,
                ["apiDiscoveryRequiredCount"] = apiDiscovery,
                ["blockedActionCount"] = blocked,
                ["highRiskActionCount"] = highRisk,
                ["todoActionCount"] = todo,
                ["missingRequiredTagCount"] = missingRequiredTags.Count,
                ["missingTargetCount"] = missingTargets.Count,
                ["duplicateActionCount"] = duplicateActions.Count,
                ["commandActionCount"] = command,
                ["navigationActionCount"] = navigation,
                ["recommendedNextAction"] = recommended
            };
        }
    }
}
