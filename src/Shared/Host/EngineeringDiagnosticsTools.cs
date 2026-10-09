#if !TIA_ENGINE_PORTED
using ModelContextProtocol.Protocol;
using TiaMcp.Logic.V4.Inputs;
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
    [McpServerToolType]
    internal sealed class EngineeringDiagnosticsTools
    {
        [McpServerTool(Name = "InspectSimaticSdCompatibility"), Description("[L2][Diagnostics][READ] Offline SIMATIC SD format/patch preflight of one .s7dcl on the server (maximum 20 MiB). Reports V20 Update 4 language requirements, textual-interface data-loss warning and non-lossless format limits. installedUpdate=-1 means unknown; a supplied value is a caller assertion, never derived from SDK versions. Does not parse/compile or modify a project; not a syntax validator.")]
        public CallToolResult InspectSimaticSdCompatibilityV4([Description("Absolute input/output path on the MCP server, with the documented extension.")] string filePath, [Description("20 or 21; 0 selects the current engine.")] int tiaMajor = 0, [Description("Caller-known installed Update number; -1 means unknown.")] int installedUpdate = -1)
            => SessionToolContract.Run("InspectSimaticSdCompatibility", false, false, () => InspectSimaticSdCompatibility(filePath, tiaMajor, installedUpdate));

        public ResponseMessage InspectSimaticSdCompatibility([Description("Absolute input/output path on the MCP server, with the documented extension.")] string filePath, [Description("20 or 21; 0 selects the current engine.")] int tiaMajor = 0, [Description("Caller-known installed Update number; -1 means unknown.")] int installedUpdate = -1)
        {
            if (tiaMajor == 0) tiaMajor = TiaMcp.Versioning.TiaVersionCatalog.Get(McpServer.ReleaseKey).MajorVersion;
            if (tiaMajor != 20 && tiaMajor != 21 || installedUpdate < -1) throw new ArgumentException("tiaMajor must be 20/21; installedUpdate >= -1.");
            var file = new FileInfo(filePath);
            if (!Path.IsPathRooted(filePath) || !file.Exists || !file.Extension.Equals(".s7dcl", StringComparison.OrdinalIgnoreCase) || file.Length > 20 * 1024 * 1024)
                throw new ArgumentException("Provide an existing absolute .s7dcl path, at most 20 MiB.");
            var meta = EngineeringAuditLogic.DocumentPreflight(File.ReadAllText(file.FullName), tiaMajor, installedUpdate < 0 ? (int?)null : installedUpdate);
            meta["success"] = true; meta["installedUpdateSource"] = installedUpdate < 0 ? "unknown" : "caller assertion";
            return new ResponseMessage { Message = "Format compatibility risks inspected; no native call or project change.", Meta = meta };
        }

        [McpServerTool(Name = "GetOpennessCompatibility"), Description("[L2][Diagnostics][READ] Read this server's loaded Siemens.Engineering assembly file versions and compiled engine capabilities without connecting to TIA. SDK file versions do not establish installed TIA Update/Hotfix: installedPatch remains unknown. Includes links to relevant Siemens V20 SD and V21 stability fixes, native-cross-reference policy and unsupported faceplate-type authoring boundary.")]
        public CallToolResult GetOpennessCompatibilityV4()
            => SessionToolContract.Run("GetOpennessCompatibility", false, false, () => ReadOpennessCompatibility());

        public ResponseMessage ReadOpennessCompatibility()
        {
            var assemblies = new JsonArray();
            foreach (var assembly in HostToolServices.Observe("assemblies", new JsonObject())!.AsArray().OfType<JsonObject>().OrderBy(a => (string?)a["name"]))
            {
                var row = new JsonObject { ["name"] = assembly["name"]?.DeepClone(), ["assemblyVersion"] = assembly["version"]?.DeepClone() };
                try { row["fileVersion"] = FileVersionInfo.GetVersionInfo((string)assembly["location"]!).FileVersion; row["path"] = assembly["location"]?.DeepClone(); }
                catch (Exception ex) { row["metadataError"] = ex.GetType().Name; }
                assemblies.Add(row);
            }
            // envelope: legacy-multiple-dynamic-fields
            return new ResponseMessage { Message = "Local API metadata read. Actual installed patch and device option support need installation/project evidence.", Meta = new JsonObject {
                ["success"] = true, ["engineMajor"] = TiaMcp.Versioning.TiaVersionCatalog.Get(McpServer.ReleaseKey).MajorVersion, ["installedPatch"] = null, ["assemblies"] = assemblies,
                ["nativeCrossReferencesEnabled"] = CrossReferenceGuardLogic.PolicyRefusal(Environment.GetEnvironmentVariable(CrossReferenceGuardLogic.NativeQueryOptInVariable)) == null,
                ["reflectionCrossReferencesAllowed"] = false, ["v20OptionWrappersCompiled"] = TiaMcp.Versioning.TiaVersionCatalog.Get(McpServer.ReleaseKey).MajorVersion == 20,
                ["faceplateTypeInternalAuthoring"] = "No verified public API in the supplied V20/V21 SDK for creating a type from zero or editing its internal controls/scripts.",
                ["knownIssues"] = new JsonArray(
                    "https://docs.tia.siemens.cloud/r/en-us/v20-updates/tia-portal-updates-readme/improvements-in-step-7/improvements-in-update-4",
                    "https://docs.tia.siemens.cloud/r/en-us/v21.0/tia-portal-hotfixes-readme/improvements-in-update-2-hotfix-1/program-stability-when-using-eigen-engineering-agent",
                    "https://docs.tia.siemens.cloud/r/en-us/v21.0/tia-portal-hotfixes-readme/improvements-in-update-2-hotfix-1/deletion-of-invalid-networks-during-compilation") } };
        }

        [McpServerTool(Name = "GetNativeInvocationLog"), Description("[L2][Diagnostics][READ] Read recent BEFORE/RETURNED/THREW records, including rotated .previous files, from this MCP's configured diagnostics directory. Release builds instrument engine-owned Openness methods, properties, reflection and enumeration boundaries. nativeCallId pairs each call; callSite, object identity/access lineage, thread/apartment, cached binding and exception type chain locate interruption. Object/attribute selectors may appear; no passwords, scripts or variable values. No native calls or arbitrary file access. Missing completion in this bounded window does not prove crash causality. take 1..500.")]
        public CallToolResult GetNativeInvocationLogV4([Description("Number of recent entries, 1..500.")] int take = 100)
            => SessionToolContract.Run("GetNativeInvocationLog", false, false, () => ReadNativeInvocationLog(take));

        public ResponseMessage ReadNativeInvocationLog([Description("Number of recent entries, 1..500.")] int take = 100)
        {
            var meta = TiaMcp.Logic.ModelContextProtocol.NativeJournalReader.Read(TiaOpenness.Shared.DataLocations.Current.DiagnosticsDirectory, take);
            return new ResponseMessage { Message = "Recent journal entries read; partial trailing records are counted separately.", Meta = meta };
        }
    }
}

#endif
