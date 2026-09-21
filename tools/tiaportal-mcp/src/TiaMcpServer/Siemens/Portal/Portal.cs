using Microsoft.Extensions.Logging;
using Siemens.Engineering;
using Siemens.Engineering.Cax;
using Siemens.Engineering.Compiler;
using Siemens.Engineering.Connection;
using Siemens.Engineering.Download;
using Siemens.Engineering.Download.Configurations;
using Siemens.Engineering.Hmi;
using Siemens.Engineering.Online;
using Siemens.Engineering.Online.Configurations;
using Siemens.Engineering.SW.Alarm;
using Siemens.Engineering.SW.OpcUa;
using Siemens.Engineering.HmiUnified;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.Multiuser;
using Siemens.Engineering.Safety;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.Types;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Security;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcpServer.Siemens
{
    public partial class Portal
    {
        // closing parantheses for regex characters ommitted, because they are not relevant for regex detection
        private readonly char[] _regexChars = ['.', '^', '$', '*', '+', '?', '(', '[', '{', '\\', '|'];

        private TiaPortal? _portal;
        // 2.7.40: the OS process behind _portal. Startdrive calls on a G120C can take TIA Portal V21 down with it (real project:
        // r2139, unwired BICO sinks, CanInsertMainTelegram); the next call then only sees EngineeringObjectDisposedException, so the
        // fault record and GetState say whether the process is still alive instead of leaving the operator to guess.
        private void RememberBoundProcess(int? processId = null)
        {
            if (processId != null) { _boundProcessId = processId; return; }
            try { _boundProcessId = _portal?.GetCurrentProcess().Id; } catch { _boundProcessId = null; }
        }
        private ProjectBase? _project;

        // 2.7.32 real project: with two TIA Portal processes open, the IsProjectNull self-heal re-ran ConnectPortal and silently
        // bound the OTHER instance's project between two tool calls. The name the caller bound explicitly (AttachToOpenProject /
        // OpenProject / CreateProject) is remembered; self-heal only rebinds to that name and every write re-checks it.
        private string? _expectedProjectName;
        /// <summary>Project name the caller bound explicitly, or null before any explicit bind (Connect resets it).</summary>
        public string? ExpectedProjectName => _expectedProjectName;
        private void RememberExpectedProject() { try { _expectedProjectName = _project?.Name; } catch { _expectedProjectName = null; } }
        /// <summary>Throws when the bound project no longer carries the explicitly bound name (stale handle or silent rebind).</summary>
        internal void EnsureBoundProjectUnchanged(string operation)
        {
            if (_expectedProjectName == null || _project == null) return;
            string actual;
            try { actual = _project.Name; }
            catch (Exception ex) { throw new PortalException(PortalErrorCode.InvalidState, operation + " refused: the bound project handle for '" + _expectedProjectName + "' is stale (" + ex.GetBaseException().Message + "). Re-attach with AttachToOpenProject before writing."); }
            if (!string.Equals(actual, _expectedProjectName, StringComparison.OrdinalIgnoreCase))
                throw new PortalException(PortalErrorCode.InvalidState, operation + " refused: the bound project changed from '" + _expectedProjectName + "' to '" + actual + "' since the last explicit AttachToOpenProject / OpenProject (another TIA Portal instance?). Re-attach explicitly before writing.");
        }

        /// <summary>
        /// The currently open project/session, or null. Exposed because some Openness features are
        /// only reachable as a service off the project root (e.g. VersionControlInterface) and the
        /// generic reflection helpers navigate properties, not services.
        /// </summary>
        public ProjectBase? CurrentProject => _project;

        // Did THIS session open the current project, or did we attach to one the user already had
        // open? ConnectPortal deliberately prefers a running Portal that already HAS a project --
        // right for AttachToOpenProject, catastrophic for CreateProject/OpenProject, whose first act
        // is to Close() whatever is open. Without this flag those two silently close the engineer's
        // work, unsaved edits included. Set true only where we ourselves opened/created it.
        private bool _projectOpenedByUs;

        /// <summary>True when the open project is one the user already had open (we merely attached).</summary>
        public bool HasForeignProject => _project != null && !_projectOpenedByUs;
        private LocalSession? _session;
        // Resolving a softwarePath walks the device tree via Openness (~40 COM calls per call). Cache it per
        // open project; ReferenceEquals(_project) auto-invalidates on any project open/close/create/attach
        // without a COM call. Device adds only introduce new paths (cache misses); there is no delete-device tool.
        private readonly Dictionary<string, SoftwareContainer> _softwareContainerCache = new Dictionary<string, SoftwareContainer>(StringComparer.OrdinalIgnoreCase);
        private ProjectBase? _softwareCacheProject;
        private readonly ILogger<Portal>? _logger;
        public string? LastConnectError { get; private set; }

        #region ctor

        public Portal(ILogger<Portal>? logger = null)
        {
            _logger = logger;
            EngineeringScalarProperties.ValueRenderer ??= HmiValueJson;   // 2.7.37: WinCC.Extension value types (V21)
        }

        #endregion

        #region helper for mcp server

        public bool ProjectIsValid
        {
            get
            {
                if (_project == null)
                {
                    return false;
                }

                // Check if the project is a valid Project instance
                if ((_session == null) && (_project is Project))
                {
                    return true;
                }

                // If it's a MultiuserProject, we can also check its validity
                if ((_session != null) && (_project is MultiuserProject))
                {
                    return true;
                }

                return false;
            }
        }

        public bool IsLocalSession
        {
            get
            {
                return _session != null;
            }
        }

        public bool IsLocalProject
        {
            get
            {
                return _session == null;
            }
        }

        #endregion

        #region helper for unit tests

        public static bool IsLocalSessionFile(string sessionPath)
        {
            // Check if the path ends with '.als\d+' using regex
            var regex = new Regex(@"\.als\d+$", RegexOptions.IgnoreCase);
            return regex.IsMatch(sessionPath);
        }

        public static bool IsLocalProjectFile(string projectPath)
        {
            // Check if the path ends with '.ap\d+' using regex
            var regex = new Regex(@"\.ap\d+$", RegexOptions.IgnoreCase);
            return regex.IsMatch(projectPath);
        }

        public void Dispose()
        {
            migrationPages.Dispose();
            try
            {
                (_project as Project)?.Close();
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Error closing the project on Dispose");
            }

            try
            {
                _portal?.Dispose();
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Error disposing TIA Portal on Dispose");
            }
        }

        #endregion

        #region portal

        // Attach to a portal process but never block longer than timeoutMs. An orphaned/dying
        // Siemens.Automation.Portal (e.g. its controlling process was killed) can otherwise hang the
        // COM Attach() call ~200s before throwing EngineeringSecurityException, stalling the whole
        // connect. On timeout we return null so the caller skips this process and tries the next /
        // launches a fresh instance. The worker thread is background and dies with the dead process.
        private TiaPortal? AttachWithTimeout(TiaPortalProcess proc, int timeoutMs)
        {
            TiaPortal? result = null;
            Exception? error = null;
            var worker = new System.Threading.Thread(() =>
            {
                try { result = proc.Attach(); }
                catch (Exception ex) { error = ex; }
            }) { IsBackground = true };
            worker.Start();
            if (!worker.Join(timeoutMs))
            {
                _logger?.LogWarning($"Attach to TIA Portal PID={proc.Id} exceeded {timeoutMs}ms; skipping (likely orphaned/dying instance).");
                return null;
            }
            if (error != null) throw error;
            return result;
        }

        // 判断一个 attach 失败是不是"白名单/授权被拒"（Openness 用户组、授权白名单）。
        // 这类拒绝跟具体是哪个 Portal 进程无关——换下一个候选、乃至自己新起一个实例，
        // 结果都一样被拒。用类型名字符串匹配而不是 catch 具体类型：
        // EngineeringSecurityException 来自运行时解析的 Openness 程序集（V20/V21 两套 csproj），
        // 不引入编译期依赖更稳。沿 InnerException 链走，因为它常被
        // EngineeringTargetInvocationException 之类包一层。
        private static bool IsSecurityRefusal(Exception? ex)
        {
            var e = ex;
            while (e != null)
            {
                if (e.GetType().Name == "EngineeringSecurityException") return true;
                e = e.InnerException;
            }
            return false;
        }

        public bool ConnectPortal() => ConnectPortal(null, false, null);

        /// <summary>Last connect diagnostics (process candidates, the bound process, whether an instance was started).</summary>
        public JsonObject? LastConnectInfo { get; private set; }

        // 2.7.56 (real machine, 2026-09-21): two TIA processes (the maintainer's project + 项目1) - neither attached within 2 x 30 s and
        // the old code then STARTED A THIRD, EMPTY INSTANCE, which the MCP client (already timed out) never learned about; the engine sat
        // bound to a portal without a project. Now: attach (by wanted project name, else the first with a project / session, else the
        // first attachable) or refuse with the per-process outcome; an instance is started only when no TIA process exists or the caller
        // passes allowStart. Per-process wait 20 s inside a 45 s total budget so the answer arrives before the client's 60 s.
        public bool ConnectPortal(string? projectName, bool allowStart, JsonObject? info)
        {
            _logger?.LogInformation("Connecting to TIA Portal...");
            info ??= new JsonObject();
            LastConnectInfo = info;
            var wanted = string.IsNullOrWhiteSpace(projectName) ? null : projectName!.Trim();
            info["projectName"] = wanted; info["allowStart"] = allowStart;

            try
            {
                LastConnectError = null;
                _project = null;
                _projectOpenedByUs = false;
                _session = null;
                _portal = null;
                _expectedProjectName = null;

                var processes = TiaPortal.GetProcesses().ToList();
                _logger?.LogInformation($"TIA Portal process count: {processes.Count}");
                info["processCount"] = processes.Count;
                if (processes.Count > 0)
                {
                    var candidates = new List<ConnectLogic.Candidate>();
                    var portals = new Dictionary<int, TiaPortal>();
                    var budget = System.Diagnostics.Stopwatch.StartNew();
                    foreach (var proc in processes)
                    {
                        var c = new ConnectLogic.Candidate { ProcessId = proc.Id };
                        candidates.Add(c);
                        var remaining = ConnectLogic.AttachTotalBudgetMs - (int)budget.ElapsedMilliseconds;
                        if (remaining < 2000) { c.Failure = "skipped, attach budget of " + ConnectLogic.AttachTotalBudgetMs + " ms exhausted"; continue; }
                        try
                        {
                            _logger?.LogInformation($"Trying attach to TIA Portal process PID={proc.Id}");
                            var candidate = AttachWithTimeout(proc, Math.Min(ConnectLogic.AttachTimeoutMsPerProcess, remaining));
                            if (candidate == null) { c.Failure = "attach did not answer within " + Math.Min(ConnectLogic.AttachTimeoutMsPerProcess, remaining) + " ms"; continue; }
                            c.Attached = true; portals[proc.Id] = candidate;
                            try { c.HasSession = candidate.LocalSessions.Any(); } catch { }
                            try { foreach (var pr in candidate.Projects) c.ProjectNames.Add(pr.Name); } catch { }
                            _logger?.LogInformation($"Portal PID={proc.Id}: hasSession={c.HasSession}, projects={string.Join("/", c.ProjectNames)}");
                            // The wanted project found: no need to probe the remaining processes (each probe can cost a dialog wait).
                            if (wanted != null && c.ProjectNames.Contains(wanted, StringComparer.Ordinal)) break;
                        }
                        catch (Exception ex)
                        {
                            _logger?.LogWarning(ex, $"Attach failed for TIA Portal PID={proc.Id}");
                            c.Failure = ex.Message;
                            LastConnectError = ex.ToString();
                            // A whitelist / authorization refusal is the same for every process and for a new instance: stop here and say so.
                            if (IsSecurityRefusal(ex))
                            {
                                foreach (var other in portals.Values) { try { other.Dispose(); } catch { } }
                                info["candidates"] = new JsonArray(candidates.Select(x => (JsonNode)JsonValue.Create(ConnectLogic.Describe(x))!).ToArray());
                                throw;
                            }
                        }
                    }
                    info["candidates"] = new JsonArray(candidates.Select(x => (JsonNode)JsonValue.Create(ConnectLogic.Describe(x))!).ToArray());
                    info["attachElapsedMs"] = budget.ElapsedMilliseconds;

                    var chosen = ConnectLogic.Choose(candidates, wanted);
                    if (chosen != null)
                    {
                        _portal = portals[chosen.ProcessId]; RememberBoundProcess(chosen.ProcessId);
                        foreach (var kv in portals) if (kv.Key != chosen.ProcessId) { try { kv.Value.Dispose(); } catch { } }
                        info["boundProcessId"] = chosen.ProcessId; info["startedNew"] = false;
                        if (chosen.HasSession)
                        {
                            try { _session = _portal.LocalSessions.First(); _project = _session.Project; _projectOpenedByUs = false; } catch { }
                        }
                        if (_project == null && chosen.ProjectNames.Count > 0)
                        {
                            try
                            {
                                _project = wanted != null ? _portal.Projects.FirstOrDefault(pr => pr.Name == wanted) ?? _portal.Projects.First() : _portal.Projects.First();
                            }
                            catch { }
                            _projectOpenedByUs = false;
                        }
                        if (wanted != null)
                        {
                            if (chosen.ProjectNames.Contains(wanted, StringComparer.Ordinal)) _expectedProjectName = wanted;
                            else { var warning = ConnectLogic.MissingProjectWarning(candidates, wanted, chosen); info["warning"] = warning; LastConnectError = warning; }
                        }
                        else if (chosen.ProjectNames.Count == 0 && !chosen.HasSession)
                        {
                            LastConnectError = $"Attached to PID {chosen.ProcessId}, but it has no visible projects/sessions.";
                            info["warning"] = LastConnectError;
                        }
                        _logger?.LogInformation($"Selected attached TIA Portal PID={chosen.ProcessId}");
                        return true;
                    }

                    if (!allowStart)
                    {
                        var refusal = ConnectLogic.Refusal(candidates, wanted);
                        LastConnectError = refusal;
                        info["startedNew"] = false;
                        throw new PortalException(PortalErrorCode.InvalidState, refusal);
                    }
                    LastConnectError = "No attachable TIA Portal process; starting a new instance because allowStart=true.";
                    _logger?.LogInformation(LastConnectError);
                }

                // No TIA process at all (or allowStart with nothing attachable): start one. Headless (WithoutUserInterface) is the default
                // because it starts far faster than booting the full GUI; --with-ui flips it for visual inspection.
                var launchMode = Engineering.LaunchWithUserInterface
                    ? TiaPortalMode.WithUserInterface
                    : TiaPortalMode.WithoutUserInterface;
                _logger?.LogInformation($"Starting a new TIA Portal instance ({launchMode}).");
                _portal = new TiaPortal(launchMode); RememberBoundProcess();
                info["startedNew"] = true; info["boundProcessId"] = _boundProcessId; info["launchMode"] = launchMode.ToString();
                return true;
            }
            catch (PortalException) { throw; }
            catch (Exception ex)
            {
                // 统一错误处理：硬失败抛结构化异常，替代 return false + LastConnectError 侧信道
                throw new PortalException(PortalErrorCode.OpennessError, $"ConnectPortal failed: {FormatExceptionDetail(ex)}", inner: ex);
            }
        }

        public List<string> ListPortalProcessProjects()
        {
            var lines = new List<string>();
            IReadOnlyList<TiaPortalProcess> processes;
            try
            {
                processes = TiaPortal.GetProcesses().ToList();
            }
            catch (Exception ex)
            {
                lines.Add("GetProcesses error: " + FormatExceptionDetail(ex));
                return lines;
            }

            lines.Add("TIA Portal process count: " + processes.Count);
            foreach (var proc in processes)
            {
                TiaPortal? candidate = null;
                try
                {
                    lines.Add("PID=" + proc.Id + " attach: trying");
                    candidate = proc.Attach();
                    if (candidate == null)
                    {
                        lines.Add("PID=" + proc.Id + " attach: <null>");
                        continue;
                    }

                    lines.Add("PID=" + proc.Id + " attach: OK");
                    try
                    {
                        var any = false;
                        foreach (var s in candidate.LocalSessions)
                        {
                            any = true;
                            lines.Add("PID=" + proc.Id + " sessionProject=" + (s.Project?.Name ?? "<null>"));
                        }
                        if (!any) lines.Add("PID=" + proc.Id + " sessions=<empty>");
                    }
                    catch (Exception ex)
                    {
                        lines.Add("PID=" + proc.Id + " sessions error: " + FormatExceptionDetail(ex));
                    }

                    try
                    {
                        var any = false;
                        foreach (var p in candidate.Projects)
                        {
                            any = true;
                            lines.Add("PID=" + proc.Id + " project=" + (p?.Name ?? "<null>"));
                        }
                        if (!any) lines.Add("PID=" + proc.Id + " projects=<empty>");
                    }
                    catch (Exception ex)
                    {
                        lines.Add("PID=" + proc.Id + " projects error: " + FormatExceptionDetail(ex));
                    }
                }
                catch (Exception ex)
                {
                    lines.Add("PID=" + proc.Id + " attach error: " + FormatExceptionDetail(ex));
                }
                finally
                {
                    if (candidate != null && candidate != _portal)
                    {
                        try { candidate.Dispose(); } catch { }
                    }
                }
            }

            return lines;
        }

        /// <summary>
        /// 起一个**全新的无头 TIA Portal 实例**，不去 attach 任何已经在跑的实例。
        ///
        /// 为什么需要它：ConnectPortal 会优先接管**已经开着工程**的那个实例，
        /// 而 OpenProject 又（正确地）拒绝动别人的工程 —— 于是用户只要博途里开着
        /// 任何工程，MCP 就**完全用不了**。那不是安全，那是把人挡在门外。
        /// 有了这条路，用户可以一边在界面里干活，一边让 MCP 在自己的实例里跑自己的工程。
        ///
        /// 必须是这个 MCP 进程里的第一个连接动作：已经绑了别的连接再起隔离实例，
        /// 只会留下一个没人管的 Siemens.Automation.Portal 进程，之后别的 attach 会把它
        /// 抢走，表现为间歇性的「no open project」。
        /// </summary>
        public bool ConnectIsolatedPortal()
        {
            if (_portal != null || _project != null || _session != null)
                throw new PortalException(PortalErrorCode.InvalidState,
                    "ConnectIsolated: this MCP session already owns a TIA connection. "
                    + "Start a fresh MCP process before calling ConnectIsolated.");
            LastConnectError = null;
            _portal = new TiaPortal(TiaPortalMode.WithoutUserInterface); RememberBoundProcess();
            _logger?.LogInformation("Started isolated headless TIA Portal instance.");
            return true;
        }

        public bool IsConnected()
        {
            return _portal != null;
        }

        public bool DisconnectPortal()
        {
            _logger?.LogInformation("Disconnecting from TIA Portal...");
            return Operation.Run(_logger, nameof(DisconnectPortal), () =>
            {
                _project = null;
                _projectOpenedByUs = false;
                _session = null;
                _expectedProjectName = null;
                _portal?.Dispose();
                _portal = null;
            });
        }

        #endregion

        #region status

        public State GetState()
        {
            _logger?.LogInformation("Getting TIA Portal state...");
            // 2.7.40 real project: after TIA Portal exits, every touch of _portal throws EngineeringObjectDisposedException. Answer
            // "not connected" and let the meta's portalProcess (processAlive=false) say why instead of throwing.
            if (_portal != null && GetPortalProcessHealth()["processAlive"] is JsonValue aliveValue && aliveValue.TryGetValue<bool>(out var alive) && !alive)
            {
                _logger?.LogWarning("GetState: the bound TIA Portal process {Pid} is no longer running.", _boundProcessId);
                return new State { IsConnected = false, Project = "-", Session = "-" };
            }
            if (_portal != null)
            {
                // 2.7.45 real project: with two TIA instances open (the maintainer's project in one, the scratch project in the other) this
                // "first accessible project" rebind silently moved the session from the explicitly bound scratch project to the other
                // instance's project, and the next AddDevice landed in the maintainer's project. With an explicit bind (_expectedProjectName)
                // only a project of that name may be (re)bound; anything else is reported, never adopted.
                bool Acceptable(ProjectBase? candidate)
                {
                    if (candidate == null) return false;
                    if (_expectedProjectName == null) return true;
                    try { return string.Equals(candidate.Name, _expectedProjectName, StringComparison.OrdinalIgnoreCase); } catch { return false; }
                }
                // check for existing local sessions
                if (_portal.LocalSessions.Any())
                {
                    // pick first session whose Project is accessible (and, once bound explicitly, named like the bound project)
                    foreach (var s in _portal.LocalSessions)
                    {
                        try
                        {
                            var p = s.Project;
                            var _ = p?.Name; // touch to validate not disposed
                            if (!Acceptable(p)) continue;
                            _session = s;
                            _project = p;
                            break;
                        }
                        catch
                        {
                            // skip disposed/inaccessible session projects
                        }
                    }
                }
                // checks for existing projects
                else if (_portal.Projects.Any())
                {
                    // pick first accessible project (avoid disposed placeholder; respect the explicit bind)
                    foreach (var p in _portal.Projects)
                    {
                        try
                        {
                            var _ = p?.Name;
                            if (!Acceptable(p)) continue;
                            _project = p;
                            break;
                        }
                        catch
                        {
                            // skip disposed
                        }
                    }
                }
            }

            if (_expectedProjectName != null && _project != null)
            {
                try { if (!string.Equals(_project.Name, _expectedProjectName, StringComparison.OrdinalIgnoreCase)) _logger?.LogWarning("GetState: bound project '{Actual}' differs from the explicitly bound '{Expected}'.", _project.Name, _expectedProjectName); }
                catch { }
            }
            return new State
            {
                IsConnected = IsConnected(),
                Project = _project != null ? _project.Name : "-",
                Session = _session != null ? _session.Project.Name : "-"
            };
        }

        public bool AttachToOpenProject(string projectName) => AttachToOpenProject(projectName, 15);

        private bool AttachToOpenProject(string projectName, int timeoutSeconds)
        {
            _logger?.LogInformation($"Attaching to open project: {projectName}");

            if (string.IsNullOrWhiteSpace(projectName)) return false;
            projectName = projectName.Trim();

            // Connect 之后 TIA 的 LocalSessions / Projects 是异步填充的，
            // 这里轮询最多 15s，避免 Connect+Attach 并行或刚启动时刷出 false。
            var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
            while (true)
            {
                try
                {
                    if (_portal != null && TryAttachProjectInPortal(_portal, projectName))
                    {
                        RememberExpectedProject();
                        return true;
                    }

                    foreach (var proc in TiaPortal.GetProcesses())
                    {
                        try
                        {
                            var candidate = proc.Attach();
                            if (candidate == null) continue;
                            if (TryAttachProjectInPortal(candidate, projectName))
                            {
                                if (_portal != null && !ReferenceEquals(_portal, candidate))
                                {
                                    try { _portal.Dispose(); } catch { }
                                }

                                _portal = candidate; RememberBoundProcess(proc.Id);
                                RememberExpectedProject();
                                return true;
                            }

                            if (!ReferenceEquals(_portal, candidate))
                            {
                                try { candidate.Dispose(); } catch { }
                            }
                        }
                        catch { }
                    }
                }
                catch { }

                if (DateTime.UtcNow >= deadline) break;
                System.Threading.Thread.Sleep(500);
            }

            return false;
        }

        private bool TryAttachProjectInPortal(TiaPortal portal, string projectName)
        {
            try
            {
                foreach (var s in portal.LocalSessions)
                {
                    try
                    {
                        var p = s.Project;
                        if (p != null && string.Equals(p.Name, projectName, StringComparison.OrdinalIgnoreCase))
                        {
                            _session = s;
                            _project = p;
                            InvalidateHmiSoftwareCache();
                            ResetHmiReadHealth();
                            return true;
                        }
                    }
                    catch { }
                }

                foreach (var p in portal.Projects)
                {
                    try
                    {
                        if (p != null && string.Equals(p.Name, projectName, StringComparison.OrdinalIgnoreCase))
                        {
                            _session = null;
                            _project = p;
                            InvalidateHmiSoftwareCache();
                            ResetHmiReadHealth();
                            return true;
                        }
                    }
                    catch { }
                }
            }
            catch { }

            return false;
        }

        #endregion

        #region project

        public List<ProjectBase> GetProjects()
        {
            _logger?.LogInformation("Getting open projects...");

            if (_portal == null)
            {
                _logger?.LogWarning("No TIA Portal instance available.");

                return [];
            }

            var projects = new List<ProjectBase>();

            if (_portal.Projects != null)
            {
                foreach (var project in _portal.Projects)
                {
                    projects.Add(project);
                }
            }

            return projects;
        }

        /// <summary>The refusal text. Written for the model: what happened, why, and the two ways out.</summary>
        private static string ForeignProjectRefusal(string projectName, string verb)
        {
            return verb + " refused: TIA Portal already has the project '" + projectName + "' open, and this " +
                   "session did not open it - it is the user's. " + verb + " closes the current project first, " +
                   "which would discard any unsaved edits. " +
                   "If you meant to work on that project, call AttachToOpenProject(projectName=\"" + projectName + "\"). " +
                   "If you really do want it closed, pass closeForeignProject=true (ask the user first).";
        }

        /// <summary>Name of the user's own open project when closing it would be collateral damage, else null.</summary>
        public string? ForeignOpenProjectName()
        {
            if (!HasForeignProject) return null;
            try { return _project?.Name ?? "(unnamed)"; }
            catch { return "(unnamed)"; }
        }

        public bool OpenProject(string projectPath, bool closeForeignProject = false) => OpenProject(projectPath, closeForeignProject, "", "", "");

        // 2.7.33: protected projects open through the UmacDelegate overloads (Projects.OpenWithUpgrade(file, umacDelegate)); the
        // credentials go in as UmacCredentials.Name / Type / SetPassword(SecureString) and are never logged.
        public bool OpenProject(string projectPath, bool closeForeignProject, string umacUserName, string umacPassword, string umacUserType)
        {
            _logger?.LogInformation($"Opening project: {projectPath} (credentials={(string.IsNullOrEmpty(umacUserName) ? "none" : "umac")})");
            BaseLeftoversLogic.ValidateUmacCredentials(umacUserName, umacPassword, umacUserType);
            UmacDelegate? umacDelegate = null; SecureString? umacSecret = null;
            if (!string.IsNullOrEmpty(umacUserName))
            {
                umacSecret = PlcBlockServicesLogic.ToSecureString(umacPassword);
                var type = (UmacUserType)Enum.Parse(typeof(UmacUserType), string.IsNullOrEmpty(umacUserType) ? "Project" : umacUserType);
                umacDelegate = credentials => { UmacCredentials umac = credentials; umac.Name = umacUserName; umac.Type = type; umac.SetPassword(umacSecret); };
            }
            try { return OpenProjectCore(projectPath, closeForeignProject, umacDelegate); }
            finally { umacSecret?.Dispose(); }
        }

        private bool OpenProjectCore(string projectPath, bool closeForeignProject, UmacDelegate? umacDelegate)
        {

            var foreign = ForeignOpenProjectName();
            if (foreign != null && !closeForeignProject)
            {
                LastConnectError = ForeignProjectRefusal(foreign, "OpenProject");
                return false;
            }

            if (IsPortalNull())
            {
                // ConnectPortal 现以 PortalException 报硬失败；此处保留 OpenProject 原有 bool 契约
                try { ConnectPortal(); }
                catch (PortalException ex)
                {
                    LastConnectError = $"Portal is null and reconnect failed: {ex.Message}";
                    return false;
                }
            }

            if (_project != null)
            {
                (_project as Project)?.Close();
                _project = null;
                _projectOpenedByUs = false;
            }

            if (_session != null)
            {
                _session.Close();
                _session = null;
            }

            try
            {
                LastConnectError = null;

                if (string.IsNullOrWhiteSpace(projectPath))
                {
                    LastConnectError = "projectPath is empty";
                    return false;
                }

                if (!File.Exists(projectPath))
                {
                    LastConnectError = $"Project file not found: {projectPath}";
                    return false;
                }

                var projects = GetProjects();
                var projectName = Path.GetFileNameWithoutExtension(projectPath);

                if (!string.IsNullOrEmpty(projectName) && projects.Any(p => p.Name.Equals(projectName)))
                {
                    // Project is already open
                    return AttachToOpenProject(projectName);
                }
                else
                {
                    // see [5.3.1 Projekt öffnen, S.113]
                    var fi = new FileInfo(projectPath);

                    try
                    {
                        _project = umacDelegate == null ? _portal?.Projects.OpenWithUpgrade(fi) : _portal?.Projects.OpenWithUpgrade(fi, umacDelegate);
                        _projectOpenedByUs = true;
                        RememberExpectedProject();
                    }
                    catch (Exception ex)
                    {
                        LastConnectError = $"OpenWithUpgrade failed: {ex}";
                        _project = null;
                        _projectOpenedByUs = false;
                    }

                    if (_project != null) { RememberExpectedProject(); return true; }

                    // Fallback: some environments expose Projects.Open(FileInfo) instead.
                    try
                    {
                        var projectsComp = _portal?.Projects;
                        if (projectsComp != null)
                        {
                            var mOpen = projectsComp.GetType().GetMethod("Open", new[] { typeof(FileInfo) });
                            if (mOpen != null)
                            {
                                var opened = mOpen.Invoke(projectsComp, new object[] { fi });
                                if (opened is ProjectBase pb)
                                {
                                    _project = pb;
                                    LastConnectError = null;
                                    RememberExpectedProject();
                                    return true;
                                }
                            }
                            else
                            {
                                LastConnectError ??= "Projects.Open(FileInfo) method not found";
                            }
                        }
                    }
                    catch (TargetInvocationException tie) when (tie.InnerException != null)
                    {
                        LastConnectError = $"Projects.Open failed: {tie.InnerException.GetType().FullName}: {tie.InnerException.Message}";
                    }
                    catch (Exception ex)
                    {
                        LastConnectError = $"Projects.Open failed: {ex}";
                    }

                    LastConnectError ??= "OpenProject returned null (no exception)";
                    return false;
                }
            }
            catch (Exception ex)
            {
                LastConnectError = ex.ToString();
                return false;
            }
        }

        public bool CreateProject(string directoryPath, string projectName, bool closeForeignProject = false)
        {
            var foreign = ForeignOpenProjectName();
            if (foreign != null && !closeForeignProject)
            {
                LastConnectError = ForeignProjectRefusal(foreign, "CreateProject");
                return false;
            }
            _logger?.LogInformation($"Creating project: dir={directoryPath}, name={projectName}");

            if (IsPortalNull())
            {
                return false;
            }

            try
            {
                if (_project != null)
                {
                    (_project as Project)?.Close();
                    _project = null;
                    _projectOpenedByUs = false;
                }

                if (_session != null)
                {
                    _session.Close();
                    _session = null;
                }

                Directory.CreateDirectory(directoryPath);
                var di = new DirectoryInfo(directoryPath);

                var created = _portal!.Projects.Create(di, projectName);
                _project = created;
                _projectOpenedByUs = true;
                RememberExpectedProject();
                return _project != null;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "CreateProject failed: dir={Dir}, name={Name}", directoryPath, projectName);
                return false;
            }
        }

        public object? GetProjectInfo()
        {
            _logger?.LogInformation("Getting project info...");

            if (IsPortalNull())
            {
                return null;
            }

            if (IsProjectNull())
            {
                return null;
            }

            var project = _project!;

            var info = new
            {
                Name = project.Name,
                Path = project.Path,
                Type = project.GetType().Name,
                IsMultiuserProject = project is MultiuserProject,
                IsLocalSession = _session != null,
                IsLocalProject = _session == null
            };

            return info;
        }

        public bool SaveProject()
        {
            _logger?.LogInformation("Saving project...");

            if (IsProjectNull())
            {
                return false;
            }

            (_project as Project)?.Save();

            return true;
        }

        public bool SaveAsProject(string path)
        {
            _logger?.LogInformation($"Saving project as: {path}");

            if (IsProjectNull())
            {
                return false;
            }

            var di = new DirectoryInfo(path);

            (_project as Project)?.SaveAs(di);

            return true;
        }

        public bool CloseProject()
        {
            _logger?.LogInformation("Closing project...");

            if (IsProjectNull())
            {
                return false;
            }

            (_project as Project)?.Close();
            _project = null;
            _projectOpenedByUs = false;
            _expectedProjectName = null;

            return true;
        }

        #endregion

        #region session

        public List<ProjectBase> GetSessions()
        {
            _logger?.LogInformation("Getting open local sessions...");

            if (IsPortalNull())
            {
                return [];
            }

            var sessions = new List<ProjectBase>();

            if (_portal?.LocalSessions != null)
            {
                foreach (var session in _portal.LocalSessions)
                {
                    sessions.Add(session.Project as ProjectBase);
                }
            }

            return sessions;
        }

        public bool OpenSession(string localSessionPath)
        {
            _logger?.LogInformation($"Opening session: {localSessionPath}");

            if (IsPortalNull())
            {
                return false;
            }

            if (_session != null)
            {
                _project = null;
                _projectOpenedByUs = false;
                _session?.Close();
                _session = null;
            }

            try
            {
                var sessions = GetSessions();
                var projectName = Path.GetFileNameWithoutExtension(localSessionPath);
                var sessionName = Regex.Replace(projectName, @"_(LS|ES)_\d$", string.Empty, RegexOptions.IgnoreCase);

                if (!string.IsNullOrEmpty(sessionName) && sessions.Any(s => s.Name.Equals(sessionName)))
                {
                    // Session is already open  
                    _session = _portal?.LocalSessions.FirstOrDefault(s => s.Project.Name == sessionName);
                    if (_session != null)
                    {
                        // Correctly cast MultiuserProject to Project  
                        _project = _session.Project;
                        return _project != null;
                    }
                }
                else
                {
                    _session = _portal?.LocalSessions.Open(new FileInfo(localSessionPath));
                    if (_session != null)
                    {
                        // Correctly cast MultiuserProject to Project
                        _project = _session.Project;
                        return _project != null;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "OpenSession failed: {Path}", localSessionPath);
                return false;
            }

            return false;
        }

        public bool SaveSession()
        {
            _logger?.LogInformation("Saving session...");

            if (IsSessionNull())
            {
                return false;
            }

            // Save session
            _session?.Save();

            return true;
        }

        public bool CloseSession()
        {
            _logger?.LogInformation("Closing session...");

            if (IsSessionNull())
            {
                return false;
            }

            _project = null;
            _projectOpenedByUs = false;
            _session?.Close();
            _session = null;

            return true;
        }

        #endregion

        // #region devices — moved to Portal.Devices.cs

        // #region software — moved to Portal.Software.cs

        // #region alarms — moved to Portal.Alarms.cs

        // #region opcua — moved to Portal.OpcUa.cs

        // #region download — moved to Portal.Download.cs

        // #region online — moved to Portal.Online.cs

        // #region blocks/types — moved to Portal.Blocks.cs

        // #region private helper — moved to Portal.Helpers.cs


    }


}
