using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;
using TiaMcpServer.Siemens;

namespace TiaMcpServer.ModelContextProtocol
{
    public static partial class McpServer
    {
        [McpServerTool(Name = "ReadPlcBlockScopes"), Description("[L2][PLC-Software][READ] Recursively discover blocks in the PLC root, every software unit and safety unit, including system block groups. Each result retains unitName, unitKind and exact blockPath. Enumeration failures fail the operation, never masquerade as an empty scope. offset/limit paginate the complete inventory; no compilation or cross-reference query.")]
        public static ResponseMessage ReadPlcBlockScopes([Description("Exact PLC software name/path; empty selects project only where documented.")] string softwarePath, [Description("Zero-based page offset, at least 0.")] int offset = 0, [Description("Page size 1..1000.")] int limit = 100)
            => Portal.ReadPlcBlockScopes(softwarePath, offset, limit);

        [McpServerTool(Name = "ManagePlcBlockDocuments"), Description("[L2][PLC-Software][WRITE] Exact scoped PLC block list/read/export/import using SIMATIC SD. unitName empty selects root; otherwise unitKind unit/safety. groupPath is relative to that scope; name is a single block/document basename. Existing output files are refused. Default dryRun=true. Actual import requires an Offline PLC. Import result distinguishes exact existence verification from unknown content completeness; verifyDocumentReadback exports one imported block to a fresh retained subdirectory and compares .s7dcl/.s7res text after line-ending normalization (sdDocumentsMatch). SIMATIC SD excludes some original properties; comparison is not proof of a lossless project round trip. No automatic save, compile or download.")]
        public static ResponseMessage ManagePlcBlockDocuments([Description("Exact PLC software name/path; empty selects project only where documented.")] string softwarePath, [Description("list | read | export | import. Operation name from the supported actions in the tool description.")] string action, [Description("One exact block name or SIMATIC SD basename without extension.")] string name = "", [Description("Exact relative user group path; empty selects the scope root.")] string groupPath = "", [Description("Exact software/safety unit name; empty selects PLC root.")] string unitName = "", [Description("unit | safety. Ignored for empty unitName.")] string unitKind = "unit", [Description("Existing absolute directory on the MCP server.")] string directoryPath = "", [Description("None | Override | SkipInactiveCultures | ActivateInactiveCultures.")] string importOption = "Override", [Description("true previews without invoking the native mutation/export; false executes.")] bool dryRun = true, [Description("Export imported block into a fresh retained folder and compare SD text.")] bool verifyDocumentReadback = false)
            => Portal.ManagePlcBlockDocuments(softwarePath, action, name, groupPath, unitName, unitKind, directoryPath, importOption, dryRun, verifyDocumentReadback);

        [McpServerTool(Name = "InspectSimaticSdCompatibility"), Description("[L2][Diagnostics][READ] Offline SIMATIC SD format/patch preflight of one .s7dcl on the server (maximum 20 MiB). Reports V20 Update 4 language requirements, textual-interface data-loss warning and non-lossless format limits. installedUpdate=-1 means unknown; a supplied value is a caller assertion, never derived from SDK versions. Does not parse/compile or modify a project; not a syntax validator.")]
        public static ResponseMessage InspectSimaticSdCompatibility([Description("Absolute input/output path on the MCP server, with the documented extension.")] string filePath, [Description("20 or 21; 0 selects the current engine.")] int tiaMajor = 0, [Description("Caller-known installed Update number; -1 means unknown.")] int installedUpdate = -1)
        {
            if (tiaMajor == 0) tiaMajor = Engineering.TiaMajorVersion;
            if (tiaMajor != 20 && tiaMajor != 21 || installedUpdate < -1) throw new ArgumentException("tiaMajor must be 20/21; installedUpdate >= -1.");
            var file = new FileInfo(filePath);
            if (!Path.IsPathRooted(filePath) || !file.Exists || !file.Extension.Equals(".s7dcl", StringComparison.OrdinalIgnoreCase) || file.Length > 20 * 1024 * 1024)
                throw new ArgumentException("Provide an existing absolute .s7dcl path, at most 20 MiB.");
            var meta = EngineeringAuditLogic.DocumentPreflight(File.ReadAllText(file.FullName), tiaMajor, installedUpdate < 0 ? (int?)null : installedUpdate);
            meta["success"] = true; meta["installedUpdateSource"] = installedUpdate < 0 ? "unknown" : "caller assertion";
            return new ResponseMessage { Message = "Format compatibility risks inspected; no native call or project change.", Meta = meta };
        }

        [McpServerTool(Name = "ReadOpennessCompatibility"), Description("[L2][Diagnostics][READ] Read this server's loaded Siemens.Engineering assembly file versions and compiled engine capabilities without connecting to TIA. SDK file versions do not establish installed TIA Update/Hotfix: installedPatch remains unknown. Includes links to relevant Siemens V20 SD and V21 stability fixes, native-cross-reference policy and unsupported faceplate-type authoring boundary.")]
        public static ResponseMessage ReadOpennessCompatibility()
        {
            var assemblies = new JsonArray();
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies().Where(a => a.GetName().Name?.StartsWith("Siemens.Engineering", StringComparison.Ordinal) == true).OrderBy(a => a.GetName().Name))
            {
                var row = new JsonObject { ["name"] = assembly.GetName().Name, ["assemblyVersion"] = assembly.GetName().Version?.ToString() };
                try { row["fileVersion"] = FileVersionInfo.GetVersionInfo(assembly.Location).FileVersion; row["path"] = assembly.Location; }
                catch (Exception ex) { row["metadataError"] = ex.GetType().Name; }
                assemblies.Add(row);
            }
            return new ResponseMessage { Message = "Local API metadata read. Actual installed patch and device option support need installation/project evidence.", Meta = new JsonObject {
                ["success"] = true, ["engineMajor"] = Engineering.TiaMajorVersion, ["installedPatch"] = null, ["assemblies"] = assemblies,
                ["nativeCrossReferencesEnabled"] = CrossReferenceGuardLogic.PolicyRefusal(Environment.GetEnvironmentVariable(CrossReferenceGuardLogic.NativeQueryOptInVariable)) == null,
                ["reflectionCrossReferencesAllowed"] = false, ["v20OptionWrappersCompiled"] = Engineering.TiaMajorVersion == 20,
                ["faceplateTypeInternalAuthoring"] = "No verified public API in the supplied V20/V21 SDK for creating a type from zero or editing its internal controls/scripts.",
                ["knownIssues"] = new JsonArray(
                    "https://docs.tia.siemens.cloud/r/en-us/v20-updates/tia-portal-updates-readme/improvements-in-step-7/improvements-in-update-4",
                    "https://docs.tia.siemens.cloud/r/en-us/v21.0/tia-portal-hotfixes-readme/improvements-in-update-2-hotfix-1/program-stability-when-using-eigen-engineering-agent",
                    "https://docs.tia.siemens.cloud/r/en-us/v21.0/tia-portal-hotfixes-readme/improvements-in-update-2-hotfix-1/deletion-of-invalid-networks-during-compilation") } };
        }

        [McpServerTool(Name = "ReadNativeInvocationLog"), Description("[L2][Diagnostics][READ] Read recent BEFORE/RETURNED/THREW records, including rotated .previous files, from this MCP's configured diagnostics directory. Release builds instrument engine-owned Openness methods, properties, reflection and enumeration boundaries. nativeCallId pairs each call; callSite, object identity/access lineage, thread/apartment, cached binding and exception type chain locate interruption. Object/attribute selectors may appear; no passwords, scripts or variable values. No native calls or arbitrary file access. Missing completion in this bounded window does not prove crash causality. take 1..500.")]
        public static ResponseMessage ReadNativeInvocationLog([Description("Number of recent entries, 1..500.")] int take = 100)
        {
            if (take < 1 || take > 500) throw new ArgumentException("take must be 1..500.");
            var root = Environment.GetEnvironmentVariable("TIA_MCP_DIAGNOSTICS_DIRECTORY");
            if (string.IsNullOrWhiteSpace(root)) root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TiaMcp", "diagnostics");
            if (!Path.IsPathRooted(root)) throw new ArgumentException("Diagnostics directory must be absolute.");
            var files = Directory.Exists(root) ? new DirectoryInfo(root).GetFiles("calls-*.jsonl*")
                .Where(f => f.Name.EndsWith(".jsonl", StringComparison.Ordinal) || f.Name.EndsWith(".jsonl.previous", StringComparison.Ordinal))
                .OrderByDescending(f => f.LastWriteTimeUtc).Take(6).Reverse().ToArray() : Array.Empty<FileInfo>();
            var queue = new Queue<JsonNode>(); int malformed = 0;
            foreach (var file in files)
                using (var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new StreamReader(stream))
                    while (reader.ReadLine() is string line)
                    {
                        try { var row = JsonNode.Parse(line); if (row != null) { queue.Enqueue(row); if (queue.Count > take) queue.Dequeue(); } }
                        catch (System.Text.Json.JsonException) { /* swallow(parse-fallback): Malformed diagnostic records are counted and skipped so the remaining bounded log window can be read. */ malformed++; }
                    }
            return new ResponseMessage { Message = "Recent journal entries read; partial trailing records are counted separately.", Meta = new JsonObject {
                ["success"] = true, ["directory"] = root, ["records"] = new JsonArray(queue.ToArray()), ["malformedLines"] = malformed, ["filesRead"] = files.Length } };
        }
    }
}
