using ModelContextProtocol.Protocol;
using TiaMcp.Logic.V4.Inputs;
using TiaMcp.Logic.V4;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using TiaMcpServer.Siemens;
using static TiaMcpServer.ModelContextProtocol.McpServer;

namespace TiaMcpServer.ModelContextProtocol
{
    [McpServerToolType]
    internal sealed class ProjectSessionTools
    {
        private readonly IEngineeringSession _session;

        private readonly SessionTools _sessionTools;
        private readonly PlcBuildTools _plcBuild;
        private readonly DevicesTools _devices;
        private readonly DocumentsTools _documents;

        public ProjectSessionTools(IEngineeringSession session, SessionTools sessionTools,
            PlcBuildTools plcBuild, DevicesTools devices, DocumentsTools documents)
        {
            _session = session;
            _sessionTools = sessionTools;
            _plcBuild = plcBuild;
            _devices = devices;
            _documents = documents;
        }

        [McpServerTool(Name = "GetProjectInfo"), Description("[L1][Project] List all open local projects and multi-user sessions with their attributes. Requires: ConnectPortal. Use this to confirm which project is active, or to find the project name for AttachOpenProject.")]
        public CallToolResult GetProjectInfoV4()
            => SessionToolContract.Run("GetProjectInfo", false, false, () => GetProjects());

        public ResponseGetProjects GetProjects()
        {
            try
            {
                var list = _session.GetProjects();

                list.AddRange(_session.GetSessions());

                var responseList = new List<ResponseProjectInfo>();
                foreach (var project in list)
                {
                    var attributes = Helper.GetAttributeList(project);

                    if (project != null)
                    {
                        responseList.Add(new ResponseProjectInfo
                        {
                            Name = project.Name,
                            Attributes = attributes
                        });
                    }
                }

                return new ResponseGetProjects
                {
                    Message = "Open projects and sessions retrieved",
                    Items = responseList,
                    Meta = ResponseMeta.Basic(DateTime.Now, true)
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error retrieving open projects: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "OpenProject"), Description("[L1][Project] Open a local TIA Portal project (.apXX) or multi-user session (.alsXX) file, where XX is the TIA version number (e.g. .ap21, .als21). Requires: ConnectPortal. Closes any currently open project first. After success, call GetProjectTree to explore its structure. Native behaviorPolicy=current; V4 native acceptance is pending.")]
        public CallToolResult OpenProjectV4(
            [Description("path: defines the path where to the project/session")] string path,
            [Description("closeForeignProject: DEFAULT false. If TIA already has a project open that this session did not open, the call is REFUSED rather than closing the user's work. Only pass true after the user has agreed to close it.")] bool closeForeignProject = false,
            [Description("umacUserName: optional user for a UMAC-protected project (Projects.OpenWithUpgrade(file, UmacDelegate)); give it together with umacPassword.")] string umacUserName = "",
            [Description("umacPassword: password of umacUserName; converted to SecureString, never logged or echoed.")] string umacPassword = "",
            [Description("umacUserType: Project (default) or Global (UMC user).")] string umacUserType = "")
            => SessionToolContract.Run("OpenProject", true, true, () => OpenProject(path, closeForeignProject, umacUserName, umacPassword, umacUserType));

        public ResponseOpenProject OpenProject(
            [Description("path: defines the path where to the project/session")] string path,
            [Description("closeForeignProject: DEFAULT false. If TIA already has a project open that this session did not open, the call is REFUSED rather than closing the user's work. Only pass true after the user has agreed to close it.")] bool closeForeignProject = false,
            [Description("umacUserName: optional user for a UMAC-protected project (Projects.OpenWithUpgrade(file, UmacDelegate)); give it together with umacPassword.")] string umacUserName = "",
            [Description("umacPassword: password of umacUserName; converted to SecureString, never logged or echoed.")] string umacPassword = "",
            [Description("umacUserType: Project (default) or Global (UMC user).")] string umacUserType = "")
        {
            try
            {
                var foreign = _session.ForeignOpenProjectName();
                if (foreign != null && !closeForeignProject)
                    throw new TiaMcp.Adapters.Contracts.AdapterPreconditionException(
                        "OpenProject refused: TIA Portal already has the project '" + foreign + "' open and this " +
                        "session did not open it - it belongs to the user. OpenProject closes the current project " +
                        "first, which would discard any unsaved edits. To work on that project call " +
                        "AttachOpenProject(projectName=\"" + foreign + "\"). To close it anyway pass " +
                        "closeForeignProject=true - ask the user before you do.",
                        isArgument: false);

                if (_session.ProjectIsValid)
                {
                    _session.CloseProject();
                }

                // get project extension
                string extension = Path.GetExtension(path).ToLowerInvariant();

                // use regex to check if extension is .ap\d+ or .als\d+
                if (!Regex.IsMatch(extension, @"^\.ap\d+$") &&
                    !Regex.IsMatch(extension, @"^\.als\d+$"))
                {
                    throw new McpException("Invalid project file extension. Use .apXX for projects or .alsXX for sessions, where XX=18,19,20,....", McpErrorCode.InvalidParams);
                }

                bool success = false;

                if (extension.StartsWith(".ap"))
                {
                    success = _session.OpenProject(path, closeForeignProject, umacUserName, umacPassword, umacUserType);
                }
                if (extension.StartsWith(".als"))
                {
                    success = _session.OpenSession(path);
                }

                if (success)
                {
                    return new ResponseOpenProject
                    {
                        Message = $"Project '{path}' opened",
                        Meta = ResponseMeta.Basic(DateTime.Now, true)
                    };
                }
                else
                {
                    var detail = _session.LastConnectError;
                    throw new McpException(
                        string.IsNullOrWhiteSpace(detail)
                            ? $"Failed to open project '{path}'"
                            : $"Failed to open project '{path}': {detail}",
                        McpErrorCode.InternalError);
                }
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error opening project '{path}': {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "AttachOpenProject"), Description("[L1][Project]Attach MCP to an already-open TIA Portal project by name (avoids disposed project handles). Native behaviorPolicy=current; V4 native acceptance is pending.")]
        public CallToolResult AttachOpenProjectV4(
            [Description("projectName: exact name shown in TIA (e.g. 'Project1')")] string projectName)
            => SessionToolContract.Run("AttachOpenProject", true, true, () => AttachToOpenProject(projectName));

        public ResponseMessage AttachToOpenProject(
            [Description("projectName: exact name shown in TIA (e.g. 'Project1')")] string projectName)
        {
            try
            {
                var ok = _session.AttachToOpenProject(projectName);
                if (ok)
                {
                    return new ResponseMessage
                    {
                        Message = $"Attached to open project '{projectName}'",
                        Meta = ResponseMeta.Basic(DateTime.Now, true)
                    };
                }

                throw new McpException($"Failed to attach to open project '{projectName}'", McpErrorCode.InternalError);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error attaching to open project '{projectName}': {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "CreateProject"), Description("[L1][Project] Create a new empty TIA Portal project. Requires: ConnectPortal. After creation, call CreateDevice to add PLCs/HMIs, then GetProjectTree to verify. The project is automatically opened after creation — no separate OpenProject call needed.")]
        public CallToolResult CreateProjectV4(
            [Description("directoryPath: folder where project will be created")] string directoryPath,
            [Description("projectName: project name")] string projectName,
            [Description("closeForeignProject: DEFAULT false. If TIA already has a project open that this session did not open, the call is REFUSED rather than closing the user's work. Only pass true after the user has agreed to close it.")] bool closeForeignProject = false)
            => SessionToolContract.Run("CreateProject", true, true, () => CreateProject(directoryPath, projectName, closeForeignProject));

        public ResponseMessage CreateProject(
            [Description("directoryPath: folder where project will be created")] string directoryPath,
            [Description("projectName: project name")] string projectName,
            [Description("closeForeignProject: DEFAULT false. If TIA already has a project open that this session did not open, the call is REFUSED rather than closing the user's work. Only pass true after the user has agreed to close it.")] bool closeForeignProject = false)
        {
            try
            {
                var foreign = _session.ForeignOpenProjectName();
                if (foreign != null && !closeForeignProject)
                    throw new McpException(
                        "CreateProject refused: TIA Portal already has the project '" + foreign + "' open and this " +
                        "session did not open it - it belongs to the user. CreateProject closes the current project " +
                        "first, which would discard any unsaved edits. To work on that project call " +
                        "AttachOpenProject(projectName=\"" + foreign + "\"). To close it anyway pass " +
                        "closeForeignProject=true - ask the user before you do.",
                        McpErrorCode.InvalidRequest);

                if (_session.ProjectIsValid)
                {
                    _session.CloseProject();
                }
                var ok = _session.CreateProject(directoryPath, projectName, closeForeignProject);
                if (!ok)
                    throw new McpException($"Failed to create project '{projectName}' in '{directoryPath}'", McpErrorCode.InternalError);

                return new ResponseMessage
                {
                    Message = $"Project '{projectName}' created in '{directoryPath}'",
                    Meta = ResponseMeta.Basic(DateTime.Now, true)
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error creating project: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "BuildProjectScaffold"), Description("[L1][Project] One-shot project generator: from a single JSON spec it creates the project, adds PLC (and optional Unified HMI) hardware, builds UDTs/global DBs/PLC tag tables, imports SCL external sources and LAD S7DCL documents, compiles, sets up the HMI connection/screens/tags, and saves — collapsing the ~20-step runbook into one call. Auto-connects if needed. Critical-step failures (connect/createProject/PLC device) abort; per-element failures are collected and reported. Spec keys: projectName(required); directoryPath?(default %TEMP%); plcName?(PLC_1); plcFamily?(S7-1500); plcMlfb?; hmiName?(omit to skip all HMI); hmiFamily?(WinCCUnifiedPC); hmiSoftwarePath?(HMI_RT_1); connectionName?(HMI_Connection_1); udt?/globalDb?/tagTable? = arrays of the same json objects BuildAndImportPlcArtifact accepts; sclSourceFiles? = array of .scl file paths; ladDocs? = array of {importPath,name}; hmiScreens? = array of {screenName,width,height,designJson(object)}; hmiTags? = array of {tagTableName?,tagName,hmiDataType?,plcTag?,address?}; compile?(true); save?(true). Returns a per-step report with compile error/warning counts. dryRun DEFAULTS TO TRUE (safety): the default call only validates the spec offline (PLC block JSON shapes, SCL/LAD file paths, designJson) WITHOUT connecting to TIA or creating anything; after a clean dry run, call again with dryRun=false to actually create the project.")]
        public CallToolResult BuildProjectScaffoldV4(
            [Description("spec: JSON object describing the project to generate. See tool description for keys.")] string spec,
            [Description("dryRun: DEFAULT true — validates the spec offline (no TIA connection, nothing created). Pass dryRun=false explicitly to actually create the project (after a clean dry run).")] bool dryRun = true)
            => SessionToolContract.Run("BuildProjectScaffold", !dryRun, true, () => ScaffoldProject(spec, dryRun));

        public ResponseScaffold ScaffoldProject(
            [Description("spec: JSON object describing the project to generate. See tool description for keys.")] string spec,
            [Description("dryRun: DEFAULT true — validates the spec offline (no TIA connection, nothing created). Pass dryRun=false explicitly to actually create the project (after a clean dry run).")] bool dryRun = true)
        {
            var resp = new ResponseScaffold { Ok = true };
            void Step(string name, string status, string? detail = null)
                => resp.Steps.Add(new ScaffoldStep { Step = name, Status = status, Detail = detail });

            JsonNode root;
            try { root = JsonNode.Parse(spec) ?? throw new Exception("spec parsed to null"); }
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
            var hmiName = S("hmiName");
            var hmiFamily = S("hmiFamily", "WinCCUnifiedPC");
            var hmiSoftwarePathSpec = S("hmiSoftwarePath"); // empty = auto-probe the real path after device add
            var connectionName = S("connectionName", "HMI_Connection_1");
            resp.ProjectName = projectName;
            resp.DirectoryPath = directoryPath;

            // ---- dryRun: offline spec validation only (no TIA connection, nothing created) ----
            if (dryRun)
            {
                foreach (var pair in new[] { ("udt", "udt"), ("globalDb", "globaldb"), ("tagTable", "tagtable") })
                    foreach (var item in Arr(pair.Item1))
                    {
                        try { _plcBuild.PlcBuildAndImport(plcName, pair.Item2, item!.ToJsonString(), "", "", "", false, true); Step(pair.Item2, "ok", "dryRun: XML built"); }
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
                    var importPath = IS(item, "importPath"); var name = IS(item, "name");
                    bool exists = !string.IsNullOrWhiteSpace(importPath) && !string.IsNullOrWhiteSpace(name) && System.IO.File.Exists(System.IO.Path.Combine(importPath, name + ".s7dcl"));
                    Step("lad", exists ? "ok" : "failed", exists ? $"{name} (.s7dcl found)" : $"MISSING .s7dcl under {importPath} for {name}"); if (!exists) resp.Ok = false;
                }
                foreach (var item in Arr("hmiScreens"))
                {
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
            try
            {
                if (!_session.IsConnected()) { _sessionTools.Connect(); Step("connect", "ok"); }
                else Step("connect", "skipped", "already connected");
            }
            catch (Exception ex) { Step("connect", "failed", ex.Message); resp.Ok = false; throw new McpException($"BuildProjectScaffold aborted at connect: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError); }

            try { CreateProject(directoryPath, projectName); Step("createProject", "ok", directoryPath); }
            catch (Exception ex) { Step("createProject", "failed", ex.Message); resp.Ok = false; throw new McpException($"BuildProjectScaffold aborted at createProject: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError); }

            try
            {
                var d = _devices.AddDeviceWithFallback(plcMlfb, "", plcName, plcFamily);
                if (d.Ok == true) Step("addDevicePlc", "ok", $"{plcName} {d.MlfbUsed}");
                else throw new McpException($"PLC device add failed: {d.Error}", McpErrorCode.InternalError);
            }
            catch (McpException) { Step("addDevicePlc", "failed", "see error"); resp.Ok = false; throw; }
            catch (Exception ex) { Step("addDevicePlc", "failed", ex.Message); resp.Ok = false; throw new McpException($"BuildProjectScaffold aborted at addDevicePlc: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError); }

            // ---- optional HMI device ----
            bool hmiRequested = !string.IsNullOrWhiteSpace(hmiName);
            bool hmiDeviceOk = false;
            if (hmiRequested)
            {
                try
                {
                    var d = _devices.AddDeviceWithFallback("", "", hmiName, hmiFamily);
                    if (d.Ok == true) { hmiDeviceOk = true; Step("addDeviceHmi", "ok", $"{hmiName} {d.MlfbUsed}"); }
                    else { Step("addDeviceHmi", "failed", d.Error); resp.Ok = false; }
                }
                catch (Exception ex) { Step("addDeviceHmi", "failed", ex.Message); resp.Ok = false; }
            }

            // ---- PLC elements (per-item collect) ----
            ScaffoldOperations.ApplyScaffoldPlcElements(root, plcName, resp);

            foreach (var item in Arr("ladDocs"))
            {
                var importPath = IS(item, "importPath");
                var name = IS(item, "name");
                if (string.IsNullOrWhiteSpace(importPath) || string.IsNullOrWhiteSpace(name)) { Step("lad", "skipped", "missing importPath/name"); continue; }
                try { _documents.ImportFromDocuments(plcName, "", importPath, name); Step("lad", "ok", name); }
                catch (Exception ex) { Step("lad", "failed", $"{name}: {ex.Message}"); resp.Ok = false; }
            }

            // ---- compile ----
            if (B("compile", true)) ScaffoldOperations.CompileScaffoldPlc(plcName, resp);

            // ---- HMI connection / screens / tags ----
            if (hmiRequested && hmiDeviceOk)
            {
                // Resolve the real Unified HMI software path instead of assuming HMI_RT_1 — it varies
                // with device naming. Probe candidates with GetHmiProgramInfo (first that succeeds wins).
                ScaffoldOperations.ApplyScaffoldHmi(root, plcName, hmiName, hmiSoftwarePathSpec, connectionName, resp);
            }

            // ---- save ----
            if (B("save", true))
            {
                try { SaveProject(); Step("save", "ok"); }
                catch (Exception ex) { Step("save", "failed", ex.Message); resp.Ok = false; }
            }

            var okCount = resp.Steps.Count(s => s.Status == "ok");
            var failCount = resp.Steps.Count(s => s.Status == "failed");
            resp.Message = $"BuildProjectScaffold '{projectName}': {okCount} ok, {failCount} failed; compile state={resp.CompileState ?? "(skipped)"} errors={resp.CompileErrorCount}.";
            resp.Meta = ResponseMeta.Basic(DateTime.Now, resp.Ok);
            return resp;
        }

        [McpServerTool(Name = "SaveProject"), Description("[L1][Project] Save the currently open project or session to disk. Requires: ConnectPortal + OpenProject. Call after any significant change (device add, block import, HMI edit). Compile first if there are pending changes to ensure consistency. Native behaviorPolicy=current; V4 native acceptance is pending.")]
        public CallToolResult SaveProjectV4()
            => SessionToolContract.Run("SaveProject", true, true, () => SaveProject(), () => !_session.IsProjectNull());

        public ResponseSaveProject SaveProject()
        {
            try
            {
                if (_session.IsLocalSession)
                {
                    if (_session.SaveSession())
                    {
                        return new ResponseSaveProject
                        {
                            Message = "Local session saved",
                            Meta = ResponseMeta.Basic(DateTime.Now, true)
                        };
                    }
                    else
                    {
                        throw new McpException("Failed to save local session", McpErrorCode.InternalError);
                    }
                }
                else
                {
                    if (_session.SaveProject())
                    {
                        return new ResponseSaveProject
                        {
                            Message = "Local project saved",
                            Meta = ResponseMeta.Basic(DateTime.Now, true)
                        };
                    }
                    else
                    {
                        throw new McpException("Failed to save project", McpErrorCode.InternalError);
                    }
                }
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error saving local project/session: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "SaveProjectCopy"), Description("[L2][Project][SESSION] TIA Save As via Project.SaveAs: the open project and MCP binding switch to the new location. Unsaved changes are saved into the new copy; the original project file keeps its last saved state. Requires Workbench approval before dispatch, like SaveProject and CloseProject. Ordinary projects only; local sessions are refused. The result reports previousProjectFile and newProjectFile from the cached binding; confirm the new location with GetSessionState. Native behaviorPolicy=current; V4 native acceptance is pending.")]
        public CallToolResult SaveProjectCopyV4(
            [Description("newProjectPath: destination project directory for TIA Save As; switches the open project and MCP binding.")] string newProjectPath)
            => SessionToolContract.Run("SaveProjectCopy", true, true, () => SaveAsProject(newProjectPath), () => !_session.IsProjectNull());

        public ResponseSaveAsProject SaveAsProject(
            [Description("newProjectPath: destination project directory for TIA Save As; switches the open project and MCP binding.")] string newProjectPath)
        {
            try
            {
                if (_session.IsLocalSession)
                {
                    throw new McpException($"Cannot save local session as '{newProjectPath}'", McpErrorCode.InvalidParams);
                }
                else
                {
                    string previousProjectFile = (string?)_session.GetBindingIdentity()["identity"]?["projectPath"]
                        ?? throw new PortalException(PortalErrorCode.InvalidState, "Save As requires an exact cached project binding.");
                    if (_session.SaveAsProject(newProjectPath))
                    {
                        var binding = _session.GetBindingIdentity();
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

        [McpServerTool(Name = "CloseProject"), Description("[L1][Project] Close the currently open project or multi-user session. Requires: ConnectPortal + OpenProject. Any unsaved changes are lost — call SaveProject first. After closing, the connection remains active but no project is open. Native behaviorPolicy=current; V4 native acceptance is pending.")]
        public CallToolResult CloseProjectV4()
            => SessionToolContract.Run("CloseProject", true, true, () => CloseProject(), () => !_session.IsProjectNull());

        public ResponseCloseProject CloseProject()
        {
            try
            {
                bool success;

                if (_session.IsLocalSession)
                {
                    success = _session.CloseSession();
                    if (success)
                    {
                        return new ResponseCloseProject
                        {
                            Message = "Local session closed",
                            Meta = ResponseMeta.Basic(DateTime.Now, true)
                        };
                    }
                    else
                    {
                        throw new McpException("Failed closing local session", McpErrorCode.InternalError);
                    }
                }
                else
                {
                    success = _session.CloseProject();
                    if (success)
                    {
                        return new ResponseCloseProject
                        {
                            Message = "Local project closed",
                            Meta = ResponseMeta.Basic(DateTime.Now, true)
                        };
                    }
                    else
                    {
                        throw new McpException("Failed closing project", McpErrorCode.InternalError);
                    }
                }

            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error closing local project/session: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name="GetObjectIdentifier"), Description("[L2][Project][READ] ObjectIdentifierProvider (project service): GetIdentifier of the exact object (kind=device/deviceItem via devicePath/itemPath, kind=plcBlock/plcType/plcTagTable via softwarePath + objectPath) - a cross-session stable identifier - or, with identifier given, Find(identifier) and describe the object it resolves to (class, name, owner path, ISystemObject flag). Official support: Device, DeviceItem, code/data blocks, PLC tags, software units, TechnologicalInstanceDB, PlcStruct. Read-only.")]
        public CallToolResult GetObjectIdentifierV4(
            [Description("kind: device | deviceItem | plcBlock | plcType | plcTagTable.")] string kind="device",
            string[]? devicePath = null,
            string[]? itemPath = null,
            string softwarePath="",
            string objectPath="",
            [Description("identifier: exact identifier of the object as the read action lists it.")] string identifier="")
            => SessionToolContract.Run("GetObjectIdentifier", false, false, () => ReadObjectIdentifier(kind, V4Json.Serialize(devicePath ?? Array.Empty<string>()), V4Json.Serialize(itemPath ?? Array.Empty<string>()), softwarePath, objectPath, identifier));

        public ResponseMessage ReadObjectIdentifier(
            [Description("kind: device | deviceItem | plcBlock | plcType | plcTagTable.")] string kind="device",
            string devicePathJson="[]",
            string itemPathJson="[]",
            string softwarePath="",
            string objectPath="",
            [Description("identifier: exact identifier of the object as the read action lists it.")] string identifier="")
            => _session.ReadObjectIdentifier(kind,devicePathJson,itemPathJson,softwarePath,objectPath,identifier);

        [McpServerTool(Name="ShowObjectInEditor"), Description("[L2][Project][WRITE] IShowable.ShowInEditor on the exact object (kind=device via devicePath, kind=plcBlock/plcType/plcTagTable via softwarePath + objectPath): opens it in the TIA Portal editor for the engineer. UI only - no project data changes; needs a Portal started with user interface. Default dryRun=true.")]
        public CallToolResult ShowObjectInEditorV4(
            [Description("kind: device | deviceItem | plcBlock | plcType | plcTagTable.")] string kind="device",
            string[]? devicePath = null,
            string[]? itemPath = null,
            string softwarePath="",
            string objectPath="",
            bool dryRun=true)
            => SessionToolContract.Run("ShowObjectInEditor", !dryRun, false, () => ShowObjectInEditor(kind, V4Json.Serialize(devicePath ?? Array.Empty<string>()), V4Json.Serialize(itemPath ?? Array.Empty<string>()), softwarePath, objectPath, dryRun));

        public ResponseMessage ShowObjectInEditor(
            [Description("kind: device | deviceItem | plcBlock | plcType | plcTagTable.")] string kind="device",
            string devicePathJson="[]",
            string itemPathJson="[]",
            string softwarePath="",
            string objectPath="",
            bool dryRun=true)
            => _session.ShowObjectInEditor(kind,devicePathJson,itemPathJson,softwarePath,objectPath,dryRun);

        [McpServerTool(Name="RunToolTransaction"), Description("[L2][Project][WRITE] Run 1..20 supported synchronous project edits inside one ExclusiveAccess + Transaction(project, text). calls is [{name,arguments:{...}}]. Supported: CreatePlcTypeGroup, DeleteEmptyPlcBlockGroup, ManagePlcUserGroup, ManageDeviceUserGroup, ManageUnifiedHmiGroup, DeleteEmptyUnifiedHmiScreenGroup, SetUnifiedObjectProperties, SetUnifiedMultilingualProperty. Rejects all other tools, including compile, online, session, save, external files and nested orchestration. Forces inner dryRun=false; preflights every call before starting. Commits only with explicit operation success, CanCommit and CommitRequested, and successful disposal. dryRun=true validates arguments only, not native semantics. Real execution needs confirmChange. No save.")]
        public CallToolResult RunToolTransactionV4(
            [Description("calls: Array of {name, arguments:{...}} supported tool calls to run inside one transaction.")] ToolCall[] calls,
            [Description("text: transaction text shown in TIA's undo history.")] string text,
            bool confirmChange=false,
            bool dryRun=true)
            => RunTransaction(calls, text, confirmChange, dryRun);

        private CallToolResult RunTransaction(ToolCall[] calls, string text, bool confirmChange, bool dryRun)
        {
            const string tool = "RunToolTransaction";
            if (calls == null || calls.Length == 0) return V4Reject(tool, InvalidInput("calls"));
            if (calls.Length > 20) return V4Reject(tool, new Error("Transaction count exceeds its limit.", new LimitExceededDetails("calls", 20, calls.Length)));
            if (string.IsNullOrWhiteSpace(text) || text.Length > 200) return V4Reject(tool, InvalidInput("text"));
            if (!dryRun && !confirmChange) return V4Reject(tool, new Error("Transaction execution requires confirmation.", new ConfirmationRequiredDetails(null)));
            var plan = new JsonArray();
            var prepared = new List<ToolCall>();
            foreach (var call in calls)
            {
                if (call == null) return V4Reject(tool, InvalidInput("calls"));
                // Validate the supplied call before applying the transaction's explicit
                // dryRun=false policy, so an invalid supplied value is never erased.
                var error = BindV4Call(call.Name, call.Arguments, out var method, out _);
                if (error != null) return V4Reject(tool, error);
                if (!TransactionExecution.SupportedTools.Contains(call.Name, StringComparer.OrdinalIgnoreCase))
                    return V4Reject(tool, InvalidInput("calls"));
                var arguments = JsonNode.Parse(call.Arguments.Json.GetRawText())!.AsObject();
                if (method!.GetParameters().Any(p => p.Name == "dryRun")) arguments["dryRun"] = false;
                var preflight = PreflightToolCall(call.Name, arguments.ToJsonString());
                if (preflight.Meta?["ok"]?.GetValue<bool?>() != true)
                    return V4Reject(tool, new Error("Transaction preflight prerequisites are not satisfied.", new PreconditionFailedDetails("transaction-preflight", call.Name)));
                prepared.Add(new ToolCall(call.Name, new ToolArguments(JsonSerializer.SerializeToElement(arguments))));
                plan.Add(new JsonObject { ["name"] = call.Name, ["arguments"] = arguments });
            }
            var meta = ResponseMeta.Basic(DateTime.Now, false, ("dryRun", dryRun), ("mayHaveChanged", false), ("calls", plan), ("text", text));
            if (dryRun)
            {
                meta["success"] = true;
                return SessionToolContract.Map(tool, new ResponseMessage { Message = "Transaction preview: " + calls.Length + " call(s) validated, nothing executed.", Meta = meta }, false, false);
            }
            var results = new JsonArray(); meta["results"] = results;
            try
            {
                bool committed = TransactionExecution.Run(prepared.Count, () => _session.BeginTransaction(text), index =>
                {
                    var call = prepared[index];
                    var payload = ResultBody(CallTool(call.Name, call.Arguments));
                    bool ok = payload?["ok"]?.GetValue<bool?>() == true;
                    results.Add(new JsonObject { ["name"] = call.Name, ["ok"] = ok, ["result"] = payload });
                    if (!ok) meta["stoppedAt"] = call.Name;
                    return ok;
                }, meta);
                meta["success"] = committed; meta["operationSuccess"] = committed; meta["apiCallSuccess"] = true;
                return SessionToolContract.Map(tool, new ResponseMessage { Message = committed ? "Transaction committed as one undo unit (" + calls.Length + " call(s)). No save." : "Transaction not committed; project transaction disposed with rollback. Inspect results and commit/cancellation state.", Meta = meta }, true, false);
            }
            catch (Exception ex)
            {
                meta["exceptionType"] = ex.GetType().Name; meta["operationSuccess"] = false; meta["apiCallSuccess"] = false;
                // A throwing begin/commit/dispose cannot prove rollback, even if no
                // inner call returned. Retain all earlier results and do not replay.
                meta["outcome"] = "unknown";
                return SessionToolContract.Map(tool, new ResponseMessage { Message = "Transaction outcome is unconfirmed.", Meta = meta }, true, false);
            }
        }
    }
}
