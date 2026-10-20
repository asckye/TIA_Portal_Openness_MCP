using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.Adapters.Contracts;
using TiaMcpServer.Siemens;
using TiaMcpServer.Siemens.Services;
namespace TiaMcpServer.ModelContextProtocol;
[McpServerToolType]
internal sealed class ProjectLifecycleProjectTools
{
    private readonly ProjectLifecyclePortService _service;
    public ProjectLifecycleProjectTools(ProjectLifecyclePortService service) => _service = service;
        [McpServerTool(Name = "BuildProjectScaffold"), Description("[L1][Project] One-shot project generator: from a single JSON spec it creates the project, adds PLC (and optional Unified HMI) hardware, builds UDTs/global DBs/PLC tag tables, imports SCL external sources and LAD S7DCL documents, compiles, sets up the HMI connection/screens/tags, and saves — collapsing the ~20-step runbook into one call. Auto-connects if needed. Critical-step failures (connect/createProject/PLC device) abort; per-element failures are collected and reported. Each device is created exactly as specified with one DeviceComposition.CreateWithItem call: no family default device, firmware-version stepping, alternative model or renaming; a TIA refusal is reported with the hardware-catalog candidates (V18+). Spec keys: projectName(required); directoryPath?(default %TEMP%); plcName?(PLC_1); plcFamily?(S7-1500, label only); plcMlfb(required: exact article number with its spaces, or the full TypeIdentifier from SearchHardwareCatalog); plcVersion?(exact catalog version; empty with a full TypeIdentifier); hmiName?(omit to skip all HMI); hmiFamily?(WinCCUnifiedPC, label only); hmiMlfb/hmiVersion(required with hmiName, same rules); hmiSoftwarePath?(HMI_RT_1); connectionName?(HMI_Connection_1); udt?/globalDb?/tagTable? = arrays of the same json objects BuildAndImportPlcArtifact accepts; sclSourceFiles? = array of .scl file paths; ladDocs? = array of {importPath,name}; hmiScreens? = array of {screenName,width,height,designJson(object)}; hmiTags? = array of {tagTableName?,tagName,hmiDataType?,plcTag?,address?}; compile?(true); save?(true). Returns a per-step report with compile error/warning counts. dryRun DEFAULTS TO TRUE (safety): the default call only validates the spec offline (device type keys, PLC block JSON shapes, SCL/LAD file paths, designJson) WITHOUT connecting to TIA or creating anything; after a clean dry run, call again with dryRun=false to actually create the project.")]
        public CallToolResult BuildProjectScaffoldV4(
            [Description("spec: JSON object describing the project to generate. See tool description for keys.")] string spec,
            [Description("dryRun: DEFAULT true — validates the spec offline (no TIA connection, nothing created). Pass dryRun=false explicitly to actually create the project (after a clean dry run).")] bool dryRun = true)
        {
            ScaffoldStep? refused;
            try
            {
                var root = JsonNode.Parse(spec);
                refused = root is JsonObject plan ? UnsupportedSteps(plan).FirstOrDefault() : null;
            }
            catch (Exception error) when (error is JsonException || error is ArgumentException)
            {
                return SessionToolContract.Run("BuildProjectScaffold", !dryRun, true, () => ScaffoldProject(spec, dryRun));
            }
            TiaMcp.Logic.V4.Error Refusal() => new TiaMcp.Logic.V4.Error(refused!.Detail!,
                new TiaMcp.Logic.V4.UnsupportedCapabilityDetails(_service.ReleaseKey, "BuildProjectScaffold", null));
            if (!dryRun && refused != null)
                return McpServer.V4Reject("BuildProjectScaffold", Refusal());
            var result = SessionToolContract.Run("BuildProjectScaffold", !dryRun, true, () => ScaffoldProject(spec, dryRun));
            if (dryRun && refused != null && result.StructuredContent?["data"] is JsonObject data)
            {
                return McpServer.V4Reject("BuildProjectScaffold", Refusal(), data.DeepClone().AsObject());
            }
            return result;
        }

        private IEnumerable<ScaffoldStep> UnsupportedSteps(JsonNode root)
        {
            IEnumerable<ScaffoldStep> Refuse(string key, string kind, string step)
            {
                if (!ProjectScaffoldCapabilities.Supports(_service.ReleaseKey, kind) && root[key] is JsonArray items)
                    foreach (var item in items)
                        yield return new ScaffoldStep { Step = step, Status = "failed",
                            Detail = ProjectScaffoldCapabilities.Refusal(_service.ReleaseKey, kind) };
            }
            foreach (var step in Refuse("ladDocs", "lad", "lad")) yield return step;
            bool hmiRequested = root["hmiName"] is JsonValue name && name.TryGetValue<string>(out var hmiName)
                && !string.IsNullOrWhiteSpace(hmiName);
            if (!hmiRequested) yield break;
            if (!ProjectScaffoldCapabilities.Supports(_service.ReleaseKey, "hmiConnect"))
                yield return new ScaffoldStep { Step = "hmiConnection", Status = "failed",
                    Detail = ProjectScaffoldCapabilities.Refusal(_service.ReleaseKey, "hmiConnect") };
            foreach (var step in Refuse("hmiScreens", "hmiScreen", "hmiScreen")) yield return step;
            if (!ProjectScaffoldCapabilities.Supports(_service.ReleaseKey, "hmiDesign") && root["hmiScreens"] is JsonArray screens)
                foreach (var screen in screens)
                    if (screen is JsonObject entry && entry["designJson"] != null)
                        yield return new ScaffoldStep { Step = "hmiDesign", Status = "failed",
                            Detail = ProjectScaffoldCapabilities.Refusal(_service.ReleaseKey, "hmiDesign") };
            foreach (var step in Refuse("hmiTags", "hmiTag", "hmiTag")) yield return step;
        }

        public ResponseScaffold ScaffoldProject(
            [Description("spec: JSON object describing the project to generate. See tool description for keys.")] string spec,
            [Description("dryRun: DEFAULT true — validates the spec offline (no TIA connection, nothing created). Pass dryRun=false explicitly to actually create the project (after a clean dry run).")] bool dryRun = true)
        {
            var resp = new ResponseScaffold { Ok = true };
            void Step(string name, string status, string? detail = null)
                => resp.Steps.Add(new ScaffoldStep { Step = name, Status = status, Detail = detail });

            JsonNode root;
            try { root = JsonNode.Parse(spec) ?? throw new JsonException("spec parsed to null"); }
            catch (Exception ex) { throw new McpException($"BuildProjectScaffold: invalid spec JSON: {ex.Message}", McpErrorCode.InvalidParams); }

            string S(string key, string def = "") { try { return root[key]?.GetValue<string>() ?? def; } catch /* swallow(parse-fallback): malformed optional scaffold values retain the caller-provided default */ { return def; } }
            bool B(string key, bool def) { try { return root[key] is JsonNode n ? n.GetValue<bool>() : def; } catch /* swallow(parse-fallback): malformed optional scaffold values retain the caller-provided default */ { return def; } }
            JsonArray Arr(string key) => root[key] as JsonArray ?? new JsonArray();
            string IS(JsonNode? n, string key, string def = "") { try { return n?[key]?.GetValue<string>() ?? def; } catch /* swallow(parse-fallback): malformed optional scaffold values retain the caller-provided default */ { return def; } }

            var projectName = S("projectName");
            if (string.IsNullOrWhiteSpace(projectName))
                throw new McpException("BuildProjectScaffold: 'projectName' is required", McpErrorCode.InvalidParams);
            var directoryPath = S("directoryPath");
            if (string.IsNullOrWhiteSpace(directoryPath))
                directoryPath = System.IO.Path.Combine(TiaOpenness.Shared.DataLocations.Current.TempDirectory, "tia_mcp_scaffold_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));
            var plcName = S("plcName", "PLC_1");
            var plcFamily = S("plcFamily", "S7-1500");
            var plcMlfb = S("plcMlfb");
            var plcVersion = S("plcVersion");
            var hmiName = S("hmiName");
            var hmiFamily = S("hmiFamily", "WinCCUnifiedPC");
            var hmiMlfb = S("hmiMlfb");
            var hmiVersion = S("hmiVersion");
            var hmiSoftwarePathSpec = S("hmiSoftwarePath"); // empty = auto-probe the real path after device add
            var connectionName = S("connectionName", "HMI_Connection_1");
            resp.ProjectName = projectName;
            resp.DirectoryPath = directoryPath;

            var unsupported = UnsupportedSteps(root).ToArray();
            if (!dryRun && unsupported.Length != 0)
                throw new NotSupportedException(unsupported[0].Detail);

            // Devices are created exactly as requested (one CreateWithItem each); there is no family default device.
            string DeviceLabel(string mlfb, string version) => string.IsNullOrWhiteSpace(version) ? mlfb.Trim() : mlfb.Trim() + " " + version.Trim();
            var plcDeviceProblem = DeviceCreationRules.SpecProblem(plcMlfb, plcVersion, "plcMlfb", "plcVersion");
            bool hmiRequested = !string.IsNullOrWhiteSpace(hmiName);
            var hmiDeviceProblem = hmiRequested ? DeviceCreationRules.SpecProblem(hmiMlfb, hmiVersion, "hmiMlfb", "hmiVersion") : null;

            // ---- dryRun: offline spec validation only (no TIA connection, nothing created) ----
            if (dryRun)
            {
                resp.Steps.AddRange(unsupported);
                if (unsupported.Length != 0) resp.Ok = false;
                if (plcDeviceProblem != null) { Step("addDevicePlc", "failed", plcDeviceProblem); resp.Ok = false; }
                if (hmiDeviceProblem != null) { Step("addDeviceHmi", "failed", hmiDeviceProblem); resp.Ok = false; }
                foreach (var pair in new[] { ("udt", "udt"), ("globalDb", "globaldb"), ("tagTable", "tagtable") })
                    foreach (var item in Arr(pair.Item1))
                    {
                        try { _service.PlcBuild.PlcBuildAndImport(plcName, pair.Item2, item!.ToJsonString(), "", "", "", false, true); Step(pair.Item2, "ok", "dryRun: XML built"); }
                        catch (Exception ex) { Step(pair.Item2, "failed", ex.Message); resp.Ok = false; }
                    }
                foreach (var item in Arr("sclSourceFiles"))
                {
                    string path; try { path = item?.GetValue<string>() ?? ""; } catch /* swallow(parse-fallback): non-string scaffold source entries are skipped as empty paths */ { path = ""; }
                    if (string.IsNullOrWhiteSpace(path)) continue;
                    bool exists = System.IO.File.Exists(path);
                    Step("scl", exists ? "ok" : "failed", (exists ? "exists: " : "MISSING: ") + path); if (!exists) resp.Ok = false;
                }
                foreach (var item in Arr("ladDocs"))
                {
                    if (!ProjectScaffoldCapabilities.Supports(_service.ReleaseKey, "lad")) continue;
                    var importPath = IS(item, "importPath"); var name = IS(item, "name");
                    bool exists = !string.IsNullOrWhiteSpace(importPath) && !string.IsNullOrWhiteSpace(name) && System.IO.File.Exists(System.IO.Path.Combine(importPath, name + ".s7dcl"));
                    Step("lad", exists ? "ok" : "failed", exists ? $"{name} (.s7dcl found)" : $"MISSING .s7dcl under {importPath} for {name}"); if (!exists) resp.Ok = false;
                }
                foreach (var item in Arr("hmiScreens"))
                {
                    if (unsupported.Any(step => step.Step == "hmiScreen")) continue;
                    var screenName = IS(item, "screenName");
                    bool ok = !string.IsNullOrWhiteSpace(screenName) && item?["designJson"] != null;
                    Step("hmiScreen", ok ? "ok" : "failed", ok ? screenName : "missing screenName/designJson"); if (!ok) resp.Ok = false;
                }
                var okN = resp.Steps.Count(s => s.Status == "ok");
                var failN = resp.Steps.Count(s => s.Status == "failed");
                resp.Message = $"BuildProjectScaffold dryRun '{projectName}': {okN} ok, {failN} failed (offline validation, nothing created)." +
                    (failN == 0 ? " Spec is valid — call BuildProjectScaffold again with dryRun=false to actually create the project." : " Fix the failed steps, then re-run.");
                resp.Meta = ResponseMeta.Basic(DateTime.Now, resp.Ok, ("dryRun", true));
                return resp;
            }

            // ---- critical: connect + create project + PLC device ----
            if (plcDeviceProblem != null) throw new McpException("BuildProjectScaffold refused before connect: " + plcDeviceProblem, McpErrorCode.InvalidParams);
            try
            {
                if (!_service.IsConnected) { _service.Primitive<ResponseConnect>("connect"); Step("connect", "ok"); }
                else Step("connect", "skipped", "already connected");
            }
            catch (Exception ex) { Step("connect", "failed", ex.Message); resp.Ok = false; throw new McpException($"BuildProjectScaffold aborted at connect: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError); }

            try { _service.Primitive<ResponseMessage>("createProject", directoryPath, projectName); Step("createProject", "ok", directoryPath); }
            catch (Exception ex) { Step("createProject", "failed", ex.Message); resp.Ok = false; throw new McpException($"BuildProjectScaffold aborted at createProject: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError); }

            try
            {
                var d = _service.AddDeviceWithFallback(plcMlfb, plcVersion, plcName, plcFamily);
                if (d.Ok == true) Step("addDevicePlc", "ok", $"{plcName} {DeviceLabel(plcMlfb, plcVersion)}");
                else throw new McpException($"PLC device add failed: {d.Error}", McpErrorCode.InternalError);
            }
            catch (McpException) { Step("addDevicePlc", "failed", "see error"); resp.Ok = false; throw; }
            catch (Exception ex) { Step("addDevicePlc", "failed", ex.Message); resp.Ok = false; throw new McpException($"BuildProjectScaffold aborted at addDevicePlc: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError); }

            // ---- optional HMI device ----
            bool hmiDeviceOk = false;
            if (hmiRequested && hmiDeviceProblem != null) { Step("addDeviceHmi", "failed", hmiDeviceProblem); resp.Ok = false; }
            else if (hmiRequested)
            {
                try
                {
                    var d = _service.AddDeviceWithFallback(hmiMlfb, hmiVersion, hmiName, hmiFamily);
                    if (d.Ok == true) { hmiDeviceOk = true; Step("addDeviceHmi", "ok", $"{hmiName} {DeviceLabel(hmiMlfb, hmiVersion)}"); }
                    else { Step("addDeviceHmi", "failed", d.Error); resp.Ok = false; }
                }
                catch (Exception ex) { Step("addDeviceHmi", "failed", ex.Message); resp.Ok = false; }
            }

            // ---- PLC elements (per-item collect) ----
            ScaffoldOperations.ApplyScaffoldPlcElements(_service, root, plcName, resp);

            foreach (var item in Arr("ladDocs"))
            {
                var importPath = IS(item, "importPath");
                var name = IS(item, "name");
                if (string.IsNullOrWhiteSpace(importPath) || string.IsNullOrWhiteSpace(name)) { Step("lad", "skipped", "missing importPath/name"); continue; }
                try { _service.ImportFromDocuments(plcName, "", importPath, name); Step("lad", "ok", name); }
                catch (Exception ex) { Step("lad", "failed", $"{name}: {ex.Message}"); resp.Ok = false; }
            }

            // ---- compile ----
            if (B("compile", true)) ScaffoldOperations.CompileScaffoldPlc(_service, plcName, resp);

            // ---- HMI connection / screens / tags ----
            if (hmiRequested && hmiDeviceOk)
            {
                // Resolve the real Unified HMI software path instead of assuming HMI_RT_1 — it varies
                // with device naming. Probe candidates with GetHmiProgramInfo (first that succeeds wins).
                ScaffoldOperations.ApplyScaffoldHmi(_service, root, plcName, hmiName, hmiSoftwarePathSpec, connectionName, resp);
            }

            // ---- save ----
            if (B("save", true))
            {
                try { _service.Primitive<ResponseSaveProject>("save"); Step("save", "ok"); }
                catch (Exception ex) { Step("save", "failed", ex.Message); resp.Ok = false; }
            }

            var okCount = resp.Steps.Count(s => s.Status == "ok");
            var failCount = resp.Steps.Count(s => s.Status == "failed");
            resp.Message = $"BuildProjectScaffold '{projectName}': {okCount} ok, {failCount} failed; compile state={resp.CompileState ?? "(skipped)"} errors={resp.CompileErrorCount}.";
            resp.Meta = ResponseMeta.Basic(DateTime.Now, resp.Ok);
            return resp;
        }


        [McpServerTool(Name = "SaveProjectCopy"), Description("[L2][Project][SESSION] TIA Save As via Project.SaveAs: the open project and MCP binding switch to the new location. Unsaved changes are saved into the new copy; the original project file keeps its last saved state. Requires Workbench approval before dispatch, like SaveProject and CloseProject. Ordinary projects only; local sessions are refused. The result reports previousProjectFile and newProjectFile from the cached binding; confirm the new location with GetSessionState. Native behaviorPolicy=current; V4 native acceptance is pending.")]
        public CallToolResult SaveProjectCopyV4(
            [Description("newProjectPath: destination project directory for TIA Save As; switches the open project and MCP binding.")] string newProjectPath)
            => SessionToolContract.Run("SaveProjectCopy", true, true, () => SaveAsProject(newProjectPath), () => !!_service.HasProject);

        public ResponseSaveAsProject SaveAsProject(
            [Description("newProjectPath: destination project directory for TIA Save As; switches the open project and MCP binding.")] string newProjectPath)
        {
            try
            {
                if (_service.IsLocalSession)
                {
                    throw new McpException($"Cannot save local session as '{newProjectPath}'", McpErrorCode.InvalidParams);
                }
                else
                {
                    string previousProjectFile = (string?)_service.GetBindingIdentity()["identity"]?["projectPath"]
                        ?? throw new PortalException(PortalErrorCode.InvalidState, "Save As requires an exact cached project binding.");
                    if (_service.SaveAsProject(newProjectPath))
                    {
                        var binding = _service.GetBindingIdentity();
                        string newProjectFile = (string?)binding["identity"]?["projectPath"]
                            ?? throw new InvalidOperationException("Save As returned without a refreshed project binding.");
                        return new ResponseSaveAsProject
                        {
                            Message = $"TIA Save As switched the open project and MCP binding from '{previousProjectFile}' to '{newProjectFile}'. Unsaved changes were saved into the new copy; the original file keeps its last saved state.",
                            PreviousProjectFile = previousProjectFile, NewProjectFile = newProjectFile,
                            Meta = ResponseMeta.Unstamped(true, ("binding", binding))
                        };
                    }
                    else
                    {
                        throw new McpException($"Failed saving local project as '{newProjectPath}'", McpErrorCode.InternalError);
                    }
                }

            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error saving local project/session as '{newProjectPath}': {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }


}
