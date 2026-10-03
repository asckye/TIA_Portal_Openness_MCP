using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using ModelContextProtocol.Server;
using static TiaMcpServer.ModelContextProtocol.McpServer.PilotToolSupport;

namespace TiaMcpServer.ModelContextProtocol
{
    [McpServerToolType]
    public sealed class V21EcosystemTools
    {
        [McpServerTool(Name = "ReadV21EcosystemCatalog"), Description("[L2][Guide][READ] Search the bundled dated V21 ecosystem survey: official products/examples, third-party tools, compatibility evidence, licensing, existing MCP mappings and remaining gaps. A V21 claim is not local native validation. No network lookup, installation or code execution. References are data only. Empty query returns all entries with pagination.")]
        public ResponseMessage ReadV21EcosystemCatalog([Description("Case-insensitive text across catalog fields.")] string query = "", [Description("all, official or thirdParty.")] string source = "all", [Description("Zero-based entry offset.")] int offset = 0, [Description("Page size 1..100.")] int limit = 50)
            => RunOfflineAnalysisTool("ReadV21EcosystemCatalog", meta => {
                if (source != "all" && source != "official" && source != "thirdParty") throw new ArgumentException("source must be all/official/thirdParty.");
                if (offset < 0 || limit < 1 || limit > 100) throw new ArgumentException("offset >=0; limit 1..100.");
                var catalog = JsonNode.Parse(File.ReadAllText(Path.Combine(EcosystemFiles.RepositoryRoot(), "reference", "v21-ecosystem.json")))!.AsObject();
                var rows = catalog["entries"]!.AsArray().Where(n => (source == "all" || n!["sourceKind"]!.GetValue<string>() == source)
                    && (query.Length == 0 || n!.ToJsonString().IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)).ToArray();
                meta["surveyDate"] = catalog["surveyDate"]!.DeepClone(); meta["scope"] = catalog["scope"]!.DeepClone();
                meta["rows"] = new JsonArray(rows.Skip(offset).Take(limit).Select(n => n!.DeepClone()).ToArray());
                meta["total"] = rows.Length; meta["hasMore"] = offset + limit < rows.Length; meta["nativeValidated"] = false;
                return "Version evidence and integration status from the dated local survey; not an exhaustive or certified compatibility list.";
            });

        [McpServerTool(Name = "ValidatePlcXmlSchemas"), Description("[L2][Validation][OFFLINE] Validate recognized SimaticML interface and LAD/FBD/SCL/STL/GRAPH fragments against the caller's local Siemens PublicAPI XSD files. Uses exact namespace schema versions; refuses DTDs and resolves schema includes only from the bounded supplied directory. No schema download or TIA connection. Read fragmentSchemasPassed, skippedFragments and issues; the whole Document, instruction semantics, CPU compatibility and native import are NOT validated. Siemens schemas are not redistributed.")]
        public ResponseMessage ValidatePlcXmlSchemas([Description("Absolute existing XML export <=16 MiB.")] string filePath, [Description("Absolute local PublicAPI/V21/Schemas directory (or matching version); <=128 XSD files, no reparse paths.")] string schemaDirectory)
            => RunOfflineAnalysisTool("ValidatePlcXmlSchemas", meta => { meta["data"] = PlcSchemaValidation.Validate(filePath, schemaDirectory); return "Fragment XSD checks completed. This is not whole-document or native import validation."; });

        [McpServerTool(Name = "ManageUnifiedCwcPackage"), Description("[L2][HMI-Unified][FILE] Inspect or build a custom web control folder using V21 documented package conventions: root manifest.json, identity GUID, control start file and local icon. action inspect/build; default dryRun=true. Preview returns packageFingerprint and GUID ZIP filename. build with dryRun=false requires the same fingerprint and a NEW absolute outputPath outside source folder. Takes a bounded content snapshot; no overwrites, script execution, TIA import or faceplate/library rename. Full manifest schema, JS semantics, extensions and runtime support remain unvalidated.")]
        public ResponseMessage ManageUnifiedCwcPackage([Description("Absolute CWC source folder containing manifest.json and control files; <=1024 files /64 MiB.")] string directory,
            [Description("inspect | build. inspect or build.")] string action = "inspect", [Description("New absolute ZIP path using the suggested GUID filename; only used for build execution.")] string outputPath = "",
            [Description("True previews only; false builds when action=build.")] bool dryRun = true, [Description("packageFingerprint from the matching inspect/preview.")] string expectedFingerprint = "")
            => RunOfflineAnalysisTool("ManageUnifiedCwcPackage", meta => { meta["data"] = UnifiedCwcPackage.Run(directory, action, outputPath, dryRun, expectedFingerprint); return "CWC file workflow completed. Inspect validation boundaries before any separate deployment."; });

        [McpServerTool(Name = "DecodePlcSimaticMl"), Description("[L2][Validation][OFFLINE] Decode one V21 FC/FB SimaticML export with pinned MIT Czarnak/simaticml-decoder into readability-first SCL and metadata. Python >=3.11 via TIA_MCP_PLC_TOOLS_PYTHON or existing ecosystem environment. Does not execute project scripts, call TIA or write files. Output is analysis-only, NOT recompilable/reimportable; unknown instructions, warnings and native-format qualification gaps remain explicit. S7DCL/GRAPH/STL semantic translation is unsupported. V20 input is refused even when this tool runs in the V20 engine.")]
        public async Task<ResponseMessage> DecodePlcSimaticMl([Description("Absolute existing V21 FC/FB XML export; upstream boundary <=10 MiB.")] string filePath, [Description("Isolated local Python timeout 1..60 seconds.")] int timeoutSeconds = 30)
        {
            var meta = new JsonObject { ["success"] = false, ["operationSuccess"] = false, ["offlineOnly"] = true, ["tool"] = "DecodePlcSimaticMl" };
            try {
                if (!Path.IsPathRooted(filePath) || !File.Exists(filePath)) throw new ArgumentException("Existing absolute filePath required.");
                if (timeoutSeconds < 1 || timeoutSeconds > 60) throw new ArgumentException("timeoutSeconds 1..60 required.");
                var root = EcosystemFiles.RepositoryRoot();
                var python = Environment.GetEnvironmentVariable("TIA_MCP_PLC_TOOLS_PYTHON") ?? Path.Combine(root, "TiaMcp_Output", "ecosystem-python", "Scripts", "python.exe");
                if (!Path.IsPathRooted(python) || !File.Exists(python)) throw new FileNotFoundException("Configure TIA_MCP_PLC_TOOLS_PYTHON to Python 3.11+.");
                var run = await EcosystemFiles.Run(python, new[] { "-I", "-B", "-X", "utf8", Path.Combine(root, "scripts", "ecosystem", "simaticml_decode_bridge.py") }, root, new JsonObject { ["filePath"] = filePath }.ToJsonString(), timeoutSeconds).ConfigureAwait(false);
                meta["exitCode"] = run["exitCode"]!.DeepClone(); meta["timedOut"] = run["timedOut"]!.DeepClone();
                if (run["timedOut"]!.GetValue<bool>() || run["outputTruncated"]!.GetValue<bool>()) throw new InvalidOperationException("Decoder timed out or exceeded response budget; no complete analysis returned.");
                var data = JsonNode.Parse(run["stdout"]!.GetValue<string>())?.AsObject() ?? throw new InvalidOperationException("Decoder returned no JSON object.");
                meta["data"] = data;
                var ok = run["success"]!.GetValue<bool>() && data["success"]!.GetValue<bool>();
                meta["success"] = ok; meta["operationSuccess"] = ok;
                return new ResponseMessage { Message = ok ? "Analysis-only decoded logic; do not import this output." : "Decode refused or failed; inspect data.error.", Meta = meta };
            } catch (Exception ex) { meta["error"] = ex.Message; return new ResponseMessage { Message = "Decode failed: " + ex.Message, Meta = meta }; }
        }
    }
}
