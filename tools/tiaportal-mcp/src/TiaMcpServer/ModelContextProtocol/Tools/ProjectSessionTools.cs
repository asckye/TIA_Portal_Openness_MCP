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
using static TiaMcpServer.ModelContextProtocol.McpServer.PilotToolSupport;
using static TiaMcpServer.ModelContextProtocol.McpServer.SessionToolSupport;

namespace TiaMcpServer.ModelContextProtocol
{
    [McpServerToolType]
    internal sealed class ProjectSessionTools
    {
        private readonly IEngineeringSession _session;

        public ProjectSessionTools(IEngineeringSession session) => _session = session;

        [McpServerTool(Name = "GetProject"), Description("[L1][Project] List all open local projects and multi-user sessions with their attributes. Requires: Connect. Use this to confirm which project is active, or to find the project name for AttachToOpenProject.")]
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
                    Meta = new JsonObject
                    {
                        ["timestamp"] = DateTime.Now,
                        ["success"] = true
                    }
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error retrieving open projects: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "OpenProject"), Description("[L1][Project] Open a local TIA Portal project (.apXX) or multi-user session (.alsXX) file, where XX is the TIA version number (e.g. .ap21, .als21). Requires: Connect. Closes any currently open project first. After success, call GetProjectTree to explore its structure.")]
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
                    throw new McpException(
                        "OpenProject refused: TIA Portal already has the project '" + foreign + "' open and this " +
                        "session did not open it - it belongs to the user. OpenProject closes the current project " +
                        "first, which would discard any unsaved edits. To work on that project call " +
                        "AttachToOpenProject(projectName=\"" + foreign + "\"). To close it anyway pass " +
                        "closeForeignProject=true - ask the user before you do.",
                        McpErrorCode.InvalidRequest);

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
                        Meta = new JsonObject
                        {
                            ["timestamp"] = DateTime.Now,
                            ["success"] = true
                        }
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

        [McpServerTool(Name = "AttachToOpenProject"), Description("[L1][Project]Attach MCP to an already-open TIA Portal project by name (avoids disposed project handles).")]
        public ResponseMessage AttachToOpenProject(
            [Description("projectName: name shown in TIA (e.g. '项目1')")] string projectName)
        {
            try
            {
                var ok = _session.AttachToOpenProject(projectName);
                if (ok)
                {
                    return new ResponseMessage
                    {
                        Message = $"Attached to open project '{projectName}'",
                        Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = true }
                    };
                }

                throw new McpException($"Failed to attach to open project '{projectName}'", McpErrorCode.InternalError);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error attaching to open project '{projectName}': {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "CreateProject"), Description("[L1][Project] Create a new empty TIA Portal project. Requires: Connect. After creation, call AddDevice to add PLCs/HMIs, then GetProjectTree to verify. The project is automatically opened after creation — no separate OpenProject call needed.")]
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
                        "AttachToOpenProject(projectName=\"" + foreign + "\"). To close it anyway pass " +
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
                    Meta = new JsonObject
                    {
                        ["timestamp"] = DateTime.Now,
                        ["success"] = true
                    }
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error creating project: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "ScaffoldProject"), Description("[L1][Project] One-shot project generator: from a single JSON spec it creates the project, adds PLC (and optional Unified HMI) hardware, builds UDTs/global DBs/PLC tag tables, imports SCL external sources and LAD S7DCL documents, compiles, sets up the HMI connection/screens/tags, and saves — collapsing the ~20-step runbook into one call. Auto-connects if needed. Critical-step failures (connect/createProject/PLC device) abort; per-element failures are collected and reported. Spec keys: projectName(required); directoryPath?(default %TEMP%); plcName?(PLC_1); plcFamily?(S7-1500); plcMlfb?; hmiName?(omit to skip all HMI); hmiFamily?(WinCCUnifiedPC); hmiSoftwarePath?(HMI_RT_1); connectionName?(HMI_Connection_1); udt?/globalDb?/tagTable? = arrays of the same json objects PlcBuildAndImport accepts; sclSourceFiles? = array of .scl file paths; ladDocs? = array of {importPath,name}; hmiScreens? = array of {screenName,width,height,designJson(object)}; hmiTags? = array of {tagTableName?,tagName,hmiDataType?,plcTag?,address?}; compile?(true); save?(true). Returns a per-step report with compile error/warning counts. dryRun DEFAULTS TO TRUE (safety): the default call only validates the spec offline (PLC block JSON shapes, SCL/LAD file paths, designJson) WITHOUT connecting to TIA or creating anything; after a clean dry run, call again with dryRun=false to actually create the project.")]
        public ResponseScaffold ScaffoldProject(
            [Description("spec: JSON object describing the project to generate. See tool description for keys.")] string spec,
            [Description("dryRun: DEFAULT true — validates the spec offline (no TIA connection, nothing created). Pass dryRun=false explicitly to actually create the project (after a clean dry run).")] bool dryRun = true)
        {
            var resp = new ResponseScaffold { Ok = true };
            void Step(string name, string status, string? detail = null)
                => resp.Steps.Add(new ScaffoldStep { Step = name, Status = status, Detail = detail });

            JsonNode root;
            try { root = JsonNode.Parse(spec) ?? throw new Exception("spec parsed to null"); }
            catch (Exception ex) { throw new McpException($"ScaffoldProject: invalid spec JSON: {ex.Message}", McpErrorCode.InvalidParams); }

            string S(string key, string def = "") { try { return root[key]?.GetValue<string>() ?? def; } catch /* swallow(parse-fallback): malformed optional scaffold values retain the caller-provided default */ { return def; } }
            bool B(string key, bool def) { try { return root[key] is JsonNode n ? n.GetValue<bool>() : def; } catch /* swallow(parse-fallback): malformed optional scaffold values retain the caller-provided default */ { return def; } }
            JsonArray Arr(string key) => root[key] as JsonArray ?? new JsonArray();
            string IS(JsonNode? n, string key, string def = "") { try { return n?[key]?.GetValue<string>() ?? def; } catch /* swallow(parse-fallback): malformed optional scaffold values retain the caller-provided default */ { return def; } }

            var projectName = S("projectName");
            if (string.IsNullOrWhiteSpace(projectName))
                throw new McpException("ScaffoldProject: 'projectName' is required", McpErrorCode.InvalidParams);
            var directoryPath = S("directoryPath");
            if (string.IsNullOrWhiteSpace(directoryPath))
                directoryPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tia_mcp_scaffold_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));
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
                        try { PlcBuildAndImport(plcName, pair.Item2, item!.ToJsonString(), "", "", "", false, true); Step(pair.Item2, "ok", "dryRun: XML built"); }
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
                resp.Message = $"ScaffoldProject dryRun '{projectName}': {okN} ok, {failN} failed (offline validation, nothing created)." +
                    (failN == 0 ? " Spec is valid — call ScaffoldProject again with dryRun=false to actually create the project." : " Fix the failed steps, then re-run.");
                resp.Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = resp.Ok, ["dryRun"] = true };
                return resp;
            }

            // ---- critical: connect + create project + PLC device ----
            try
            {
                if (!_session.IsConnected()) { Connect(); Step("connect", "ok"); }
                else Step("connect", "skipped", "already connected");
            }
            catch (Exception ex) { Step("connect", "failed", ex.Message); resp.Ok = false; throw new McpException($"ScaffoldProject aborted at connect: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError); }

            try { CreateProject(directoryPath, projectName); Step("createProject", "ok", directoryPath); }
            catch (Exception ex) { Step("createProject", "failed", ex.Message); resp.Ok = false; throw new McpException($"ScaffoldProject aborted at createProject: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError); }

            try
            {
                var d = AddDeviceWithFallback(plcMlfb, "", plcName, plcFamily);
                if (d.Ok == true) Step("addDevicePlc", "ok", $"{plcName} {d.MlfbUsed}");
                else throw new McpException($"PLC device add failed: {d.Error}", McpErrorCode.InternalError);
            }
            catch (McpException) { Step("addDevicePlc", "failed", "see error"); resp.Ok = false; throw; }
            catch (Exception ex) { Step("addDevicePlc", "failed", ex.Message); resp.Ok = false; throw new McpException($"ScaffoldProject aborted at addDevicePlc: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError); }

            // ---- optional HMI device ----
            bool hmiRequested = !string.IsNullOrWhiteSpace(hmiName);
            bool hmiDeviceOk = false;
            if (hmiRequested)
            {
                try
                {
                    var d = AddDeviceWithFallback("", "", hmiName, hmiFamily);
                    if (d.Ok == true) { hmiDeviceOk = true; Step("addDeviceHmi", "ok", $"{hmiName} {d.MlfbUsed}"); }
                    else { Step("addDeviceHmi", "failed", d.Error); resp.Ok = false; }
                }
                catch (Exception ex) { Step("addDeviceHmi", "failed", ex.Message); resp.Ok = false; }
            }

            // ---- PLC elements (per-item collect) ----
            ApplyScaffoldPlcElements(root, plcName, resp);

            foreach (var item in Arr("ladDocs"))
            {
                var importPath = IS(item, "importPath");
                var name = IS(item, "name");
                if (string.IsNullOrWhiteSpace(importPath) || string.IsNullOrWhiteSpace(name)) { Step("lad", "skipped", "missing importPath/name"); continue; }
                try { ImportFromDocuments(plcName, "", importPath, name); Step("lad", "ok", name); }
                catch (Exception ex) { Step("lad", "failed", $"{name}: {ex.Message}"); resp.Ok = false; }
            }

            // ---- compile ----
            if (B("compile", true)) CompileScaffoldPlc(plcName, resp);

            // ---- HMI connection / screens / tags ----
            if (hmiRequested && hmiDeviceOk)
            {
                // Resolve the real Unified HMI software path instead of assuming HMI_RT_1 — it varies
                // with device naming. Probe candidates with GetHmiProgramInfo (first that succeeds wins).
                ApplyScaffoldHmi(root, plcName, hmiName, hmiSoftwarePathSpec, connectionName, resp);
            }

            // ---- save ----
            if (B("save", true))
            {
                try { SaveProject(); Step("save", "ok"); }
                catch (Exception ex) { Step("save", "failed", ex.Message); resp.Ok = false; }
            }

            var okCount = resp.Steps.Count(s => s.Status == "ok");
            var failCount = resp.Steps.Count(s => s.Status == "failed");
            resp.Message = $"ScaffoldProject '{projectName}': {okCount} ok, {failCount} failed; compile state={resp.CompileState ?? "(skipped)"} errors={resp.CompileErrorCount}.";
            resp.Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = resp.Ok };
            return resp;
        }

        [McpServerTool(Name = "SaveProject"), Description("[L1][Project] Save the currently open project or session to disk. Requires: Connect + OpenProject. Call after any significant change (device add, block import, HMI edit). Compile first if there are pending changes to ensure consistency.")]
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
                            Meta = new JsonObject
                            {
                                ["timestamp"] = DateTime.Now,
                                ["success"] = true
                            }
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
                            Meta = new JsonObject
                            {
                                ["timestamp"] = DateTime.Now,
                                ["success"] = true
                            }
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

        [McpServerTool(Name = "SaveAsProject"), Description("[L2][Project]Save current TIA-Portal project/session with a new name")]
        public ResponseSaveAsProject SaveAsProject(
            [Description("newProjectPath: defines the new path where to save the project")] string newProjectPath)
        {
            try
            {
                if (_session.IsLocalSession)
                {
                    throw new McpException($"Cannot save local session as '{newProjectPath}'", McpErrorCode.InvalidParams);
                }
                else
                {
                    if (_session.SaveAsProject(newProjectPath))
                    {
                        return new ResponseSaveAsProject
                        {
                            Message = $"Local project saved as '{newProjectPath}'",
                            Meta = new JsonObject
                            {
                                ["timestamp"] = DateTime.Now,
                                ["success"] = true
                            }
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

        [McpServerTool(Name = "CloseProject"), Description("[L1][Project] Close the currently open project or multi-user session. Requires: Connect + OpenProject. Any unsaved changes are lost — call SaveProject first. After closing, the connection remains active but no project is open.")]
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
                            Meta = new JsonObject
                            {
                                ["timestamp"] = DateTime.Now,
                                ["success"] = true
                            }
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
                            Meta = new JsonObject
                            {
                                ["timestamp"] = DateTime.Now,
                                ["success"] = true
                            }
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

        [McpServerTool(Name="ReadObjectIdentifier"), Description("[L2][Project][READ] ObjectIdentifierProvider (project service): GetIdentifier of the exact object (kind=device/deviceItem via devicePathJson/itemPathJson, kind=plcBlock/plcType/plcTagTable via softwarePath + objectPath) - a cross-session stable identifier - or, with identifier given, Find(identifier) and describe the object it resolves to (class, name, owner path, ISystemObject flag). Official support: Device, DeviceItem, code/data blocks, PLC tags, software units, TechnologicalInstanceDB, PlcStruct. Read-only.")]
        public ResponseMessage ReadObjectIdentifier(
            [Description("kind: device | deviceItem | plcBlock | plcType | plcTagTable.")] string kind="device",
            string devicePathJson="[]",
            string itemPathJson="[]",
            string softwarePath="",
            string objectPath="",
            [Description("identifier: exact identifier of the object as the read action lists it.")] string identifier="")
            => _session.ReadObjectIdentifier(kind,devicePathJson,itemPathJson,softwarePath,objectPath,identifier);

        [McpServerTool(Name="ShowObjectInEditor"), Description("[L2][Project][WRITE] IShowable.ShowInEditor on the exact object (kind=device via devicePathJson, kind=plcBlock/plcType/plcTagTable via softwarePath + objectPath): opens it in the TIA Portal editor for the engineer. UI only - no project data changes; needs a Portal started with user interface. Default dryRun=true.")]
        public ResponseMessage ShowObjectInEditor(
            [Description("kind: device | deviceItem | plcBlock | plcType | plcTagTable.")] string kind="device",
            string devicePathJson="[]",
            string itemPathJson="[]",
            string softwarePath="",
            string objectPath="",
            bool dryRun=true)
            => _session.ShowObjectInEditor(kind,devicePathJson,itemPathJson,softwarePath,objectPath,dryRun);

        [McpServerTool(Name="RunToolsInTransaction"), Description("[L2][Project][WRITE] Run 1..20 supported synchronous project edits inside one ExclusiveAccess + Transaction(project, text). callsJson is [{name,arguments:{...}}]. Supported: CreatePlcTypeGroup, DeleteEmptyPlcBlockGroup, ManagePlcUserGroup, ManageDeviceUserGroup, ManageUnifiedHmiGroup, DeleteEmptyUnifiedHmiScreenGroup, UpdateUnifiedObjectProperties, UpdateUnifiedMultilingualProperty. Rejects all other tools, including compile, online, session, save, external files and nested orchestration. Forces inner dryRun=false; preflights every call before starting. Commits only with explicit operation success, CanCommit and CommitRequested, and successful disposal. dryRun=true validates arguments only, not native semantics. Real execution needs confirmChange. No save.")]
        public ResponseMessage RunToolsInTransaction(
            [Description("callsJson: JSON array of {name, arguments:{...}} supported tool calls to run inside one transaction.")] string callsJson,
            [Description("text: transaction text shown in TIA's undo history.")] string text,
            bool confirmChange=false,
            bool dryRun=true)
        {
            var meta = new JsonObject { ["timestamp"] = DateTime.Now, ["tool"] = "RunToolsInTransaction", ["success"] = false, ["dryRun"] = dryRun, ["mayHaveChanged"] = false };
            try
            {
                var calls = BaseLeftoversLogic.ParseToolCalls(callsJson);
                BaseLeftoversLogic.ValidateTransactionRequest(text, calls, confirmChange, dryRun);
                var all = AllToolMethods(); var plan = new JsonArray(); meta["calls"] = plan;
                foreach (var call in calls)
                {
                    if (!all.ContainsKey(call.Name)) throw new ArgumentException("No tool named '" + call.Name + "'.");
                    TransactionExecution.RequireSupported(call.Name);
                    call.ArgumentsJson = BaseLeftoversLogic.ForceRealExecution(call.ArgumentsJson);
                    var preflight = PreflightToolCall(call.Name, call.ArgumentsJson);
                    if (preflight.Meta?["ok"]?.GetValue<bool?>() != true)
                        throw new ArgumentException("Preflight failed for " + call.Name + ": " + preflight.Message);
                    plan.Add(new JsonObject { ["name"] = call.Name, ["arguments"] = JsonNode.Parse(call.ArgumentsJson) });
                }
                meta["text"] = text;
                if (dryRun) { meta["success"] = true; meta["operationSuccess"] = true; return new ResponseMessage { Message = "Transaction preview: " + calls.Length + " call(s) validated, nothing executed.", Meta = meta }; }
                var results = new JsonArray(); meta["results"] = results;
                bool committed = TransactionExecution.Run(calls.Length, () => _session.BeginTransaction(text), index =>
                {
                    var call = calls[index];
                    var response = CallTool(call.Name, call.ArgumentsJson);
                    bool ok = response.Meta?["operationSuccess"]?.GetValue<bool?>() == true;
                    JsonNode? payload; try { payload = JsonNode.Parse(response.Message); } catch /* swallow(parse-fallback): plain-text tool responses are retained as the transaction result */ { payload = response.Message; }
                    results.Add(new JsonObject { ["name"] = call.Name, ["ok"] = ok, ["result"] = payload });
                    if (!ok) meta["stoppedAt"] = call.Name;
                    return ok;
                }, meta);
                meta["success"] = committed; meta["operationSuccess"] = committed; meta["apiCallSuccess"] = true;
                return new ResponseMessage { Message = committed ? "Transaction committed as one undo unit (" + calls.Length + " call(s)). No save." : "Transaction not committed; project transaction disposed with rollback. Inspect results and commit/cancellation state.", Meta = meta };
            }
            catch (Exception ex)
            {
                meta["error"] = ex.ToString(); meta["operationSuccess"] = false; meta["apiCallSuccess"] = false;
                return new ResponseMessage { Message = "RunToolsInTransaction failed: " + ex.Message, Meta = meta };
            }
        }
    }
}
