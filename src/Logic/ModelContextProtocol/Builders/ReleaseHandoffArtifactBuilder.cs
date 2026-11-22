using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TiaMcpServer.ModelContextProtocol
{
    /// <summary>
    /// 发布交接材料重建器。
    /// 用于从已有 offline_release_validation_suite_*.json 重建 diagnostics/runbook/manifest，
    /// 方便外发包接收方不重跑完整探针也能得到交接材料。
    /// </summary>
    public static class ReleaseHandoffArtifactBuilder
    {
        public static JsonObject ReadSuite(string offlineReleaseSuiteJsonPath)
        {
            if (string.IsNullOrWhiteSpace(offlineReleaseSuiteJsonPath) || !File.Exists(offlineReleaseSuiteJsonPath))
                throw new FileNotFoundException("Offline release suite JSON report not found.", offlineReleaseSuiteJsonPath);
            static bool Boolean(JsonNode? node) => node is JsonValue value && value.TryGetValue<bool>(out _);
            try
            {
                if (JsonNode.Parse(File.ReadAllText(offlineReleaseSuiteJsonPath, Encoding.UTF8)) is JsonObject root
                    && root["mode"]?.ToString() == "tia-mcp-offline-release-validation-suite" && root["items"] is JsonArray items
                    && Boolean(root["ok"]) && (root["offlineOnly"] == null || Boolean(root["offlineOnly"]))
                    && items.All(item => item is JsonObject && Boolean(item["ok"]))) return root;
            }
            catch (JsonException error)
            {
                throw new ArgumentException("Invalid OfflineReleaseValidationSuite JSON.", nameof(offlineReleaseSuiteJsonPath), error);
            }
            throw new ArgumentException("Expected an OfflineReleaseValidationSuite report with mode, items and a boolean ok.",
                nameof(offlineReleaseSuiteJsonPath));
        }

        public static JsonObject RebuildFromSuiteJson(string offlineReleaseSuiteJsonPath, string outputDirectory)
            => RebuildFromSuiteJson(offlineReleaseSuiteJsonPath, outputDirectory, TiaMcp.Versioning.TiaVersionCatalog.Runnable.First().Key);

        public static JsonObject RebuildFromSuiteJson(string offlineReleaseSuiteJsonPath, string outputDirectory, string release)
        {
            var suiteRoot = ReadSuite(offlineReleaseSuiteJsonPath);
            if (string.IsNullOrWhiteSpace(outputDirectory))
                outputDirectory = Path.GetDirectoryName(Path.GetFullPath(offlineReleaseSuiteJsonPath)) ?? Directory.GetCurrentDirectory();

            Directory.CreateDirectory(outputDirectory);
            var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");

            var diagnostics = ReleaseDiagnosticReportBuilder.Build(suiteRoot);
            var diagJsonPath = Path.Combine(outputDirectory, "rebuilt_release_diagnostics_" + stamp + ".json");
            var diagMdPath = Path.Combine(outputDirectory, "rebuilt_release_diagnostics_" + stamp + ".md");
            WriteJsonAndMarkdown(diagnostics, diagJsonPath, diagMdPath, ReleaseDiagnosticReportBuilder.BuildMarkdown);
            diagnostics["jsonPath"] = diagJsonPath;
            diagnostics["markdownPath"] = diagMdPath;
            suiteRoot["diagnostics"] = diagnostics;
            suiteRoot["diagnosticJsonPath"] = diagJsonPath;
            suiteRoot["diagnosticMarkdownPath"] = diagMdPath;

            var runbook = ReleaseRunbookBuilder.Build(suiteRoot, diagnostics, release);
            var runbookJsonPath = Path.Combine(outputDirectory, "rebuilt_release_runbook_" + stamp + ".json");
            var runbookMdPath = Path.Combine(outputDirectory, "rebuilt_release_runbook_" + stamp + ".md");
            WriteJsonAndMarkdown(runbook, runbookJsonPath, runbookMdPath, ReleaseRunbookBuilder.BuildMarkdown);
            runbook["jsonPath"] = runbookJsonPath;
            runbook["markdownPath"] = runbookMdPath;
            suiteRoot["runbook"] = runbook;
            suiteRoot["runbookJsonPath"] = runbookJsonPath;
            suiteRoot["runbookMarkdownPath"] = runbookMdPath;

            var manifest = ReleaseManifestBuilder.Build(suiteRoot, diagnostics, runbook);
            var manifestJsonPath = Path.Combine(outputDirectory, "rebuilt_release_manifest_" + stamp + ".json");
            var manifestMdPath = Path.Combine(outputDirectory, "rebuilt_release_manifest_" + stamp + ".md");
            WriteJsonAndMarkdown(manifest, manifestJsonPath, manifestMdPath, ReleaseManifestBuilder.BuildMarkdown);
            manifest["jsonPath"] = manifestJsonPath;
            manifest["markdownPath"] = manifestMdPath;

            var readinessGate = manifest["releaseReadinessGate"] as JsonObject
                ?? ReleaseReadinessGateBuilder.Build(suiteRoot, diagnostics, runbook, manifest);
            var readinessGateJsonPath = Path.Combine(outputDirectory, "rebuilt_release_readiness_gate_" + stamp + ".json");
            var readinessGateMdPath = Path.Combine(outputDirectory, "rebuilt_release_readiness_gate_" + stamp + ".md");
            WriteJsonAndMarkdown(readinessGate, readinessGateJsonPath, readinessGateMdPath, ReleaseReadinessGateBuilder.BuildMarkdown);
            readinessGate["jsonPath"] = readinessGateJsonPath;
            readinessGate["markdownPath"] = readinessGateMdPath;

            return new JsonObject
            {
                ["format"] = "tia-mcp-release-handoff-artifacts-v1",
                ["timestamp"] = DateTime.Now.ToString("O"),
                ["offlineOnly"] = true,
                ["inputSuiteJsonPath"] = Path.GetFullPath(offlineReleaseSuiteJsonPath),
                ["outputDirectory"] = Path.GetFullPath(outputDirectory),
                ["diagnosticMarkdownPath"] = diagMdPath,
                ["diagnosticJsonPath"] = diagJsonPath,
                ["runbookMarkdownPath"] = runbookMdPath,
                ["runbookJsonPath"] = runbookJsonPath,
                ["manifestMarkdownPath"] = manifestMdPath,
                ["manifestJsonPath"] = manifestJsonPath,
                ["releaseReadinessGateMarkdownPath"] = readinessGateMdPath,
                ["releaseReadinessGateJsonPath"] = readinessGateJsonPath,
                ["releaseReady"] = manifest["releaseReady"]?.GetValue<bool>() == true,
                ["ok"] = true
            };
        }

        private static void WriteJsonAndMarkdown(JsonObject root, string jsonPath, string mdPath, Func<JsonObject, string, string> markdownBuilder)
        {
            File.WriteAllText(jsonPath, root.ToJsonString(new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            }), Encoding.UTF8);
            File.WriteAllText(mdPath, markdownBuilder(root, jsonPath), Encoding.UTF8);
        }
    }
}
