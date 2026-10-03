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
    public partial class Portal : IDisposable
    {
        // closing parantheses for regex characters ommitted, because they are not relevant for regex detection
        private readonly char[] _regexChars = ['.', '^', '$', '*', '+', '?', '(', '[', '{', '\\', '|'];

        private TiaPortal? _portal;
        // 2.7.40: the OS process behind _portal. Startdrive calls on a G120C can take TIA Portal V21 down with it (real project:
        // r2139, unwired BICO sinks, CanInsertMainTelegram); the next call then only sees EngineeringObjectDisposedException, so the
        // fault record and GetState say whether the process is still alive instead of leaving the operator to guess.
        private void RememberBoundProcess(int? processId = null)
        {
            int pid = processId ?? _portal!.GetCurrentProcess().Id;
            _processStartTicks = ProcessStart(pid);
            _processLease = Reserve(pid, _processStartTicks);
            _boundProcessId = pid;
        }
        private ProjectBase? _project;

        // The cached name is retained for compatibility; all safety checks use the
        // full process/path/generation identity in Portal.Binding.cs.
        private string? _expectedProjectName;
        /// <summary>Project name the caller bound explicitly, or null before any explicit bind (Connect resets it).</summary>
        public string? ExpectedProjectName => _expectedProjectName;
        private void RememberExpectedProject() => CaptureBinding();
        internal void EnsureBoundProjectUnchanged(string operation) => VerifyBinding(operation);

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
        private ProjectBase? _openedProject;
        private bool _projectOpenedByUs
        {
            get => ProjectOwnership.Owns(_openedProject, _project);
            set => _openedProject = value ? _project : null;
        }

        /// <summary>True when the open project is one the user already had open (we merely attached).</summary>
        public bool HasForeignProject => _project != null && !_projectOpenedByUs;
        private LocalSession? _session;
        // Resolving a softwarePath walks the device tree via Openness (~40 COM calls per call). Cache it per
        // open project; ReferenceEquals(_project) auto-invalidates on any project open/close/create/attach
        // without a COM call. Device adds only introduce new paths (cache misses); there is no delete-device tool.
        private readonly Dictionary<string, SoftwareContainer> _softwareContainerCache = new Dictionary<string, SoftwareContainer>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, SoftwareContainer> _plcResolutionCache = new Dictionary<string, SoftwareContainer>(StringComparer.OrdinalIgnoreCase);
        private ProjectBase? _softwareCacheProject;
        private readonly ILogger<Portal>? _logger;
        public string? LastConnectError { get; private set; }

        #region ctor

        public Portal(ILogger<Portal>? logger = null)
        {
            _logger = logger;
            InvocationJournal.BindingSnapshot = () => _binding?.ToJson();
            PortalFailureClassifier.ProcessLostObserved += reason => {
                if (_portal != null) _bindingFault = "Native channel fault: " + reason + "; explicit recovery required.";
            };
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
            ProjectOwnership.Release(_projectOpenedByUs,
                () => (_project as Project)?.Close(),
                () => DisconnectPortal(),
                ex => _logger?.LogWarning(ex, "Error releasing the Openness session"));
        }

        #endregion

        #region portal

        // Timeout bounds the caller's wait, not the native operation. A late successful
        // attachment must be detached; it must never become an untracked live session.
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

        // Metadata selection precedes Attach; ambiguous names never choose an arbitrary instance.
        public bool ConnectPortal(string? projectName, bool allowStart, JsonObject? info)
        {
            if (_projectOpenedByUs) throw new PortalException(PortalErrorCode.InvalidState,
                "Explicitly save/close/disconnect the MCP-owned project before replacing the connection.");
            string? wanted = string.IsNullOrWhiteSpace(projectName) ? null : projectName.Trim();
            var processes = InvocationJournal.Native("TiaPortal.GetProcesses", () => TiaPortal.GetProcesses().ToList());
            var candidates = wanted == null ? processes : processes.Where(p => p.ProjectPath != null &&
                string.Equals(Path.GetFileNameWithoutExtension(p.ProjectPath.FullName), wanted, StringComparison.OrdinalIgnoreCase)).ToList();
            if (candidates.Count > 1) throw new PortalException(PortalErrorCode.InvalidState,
                "Multiple TIA instances match. Use ListPortalProcessProjects then ConnectToProject with PID, start time and full project path.");
            if (candidates.Count == 1)
            {
                var selected = candidates[0];
                var path = selected.ProjectPath?.FullName;
                ConnectSelectedProcess(selected, string.IsNullOrWhiteSpace(path) ? null : ProjectBindingIdentity.CanonicalPath(path));
            }
            else
            {
                if (wanted != null || (processes.Count != 0 && !allowStart))
                    throw new PortalException(PortalErrorCode.NotFound, "Requested project is not open. Inspect ListPortalProcessProjects or explicitly use ConnectIsolated for a new instance.");
                if (_portal != null) DisconnectPortal();
                _portal = InvocationJournal.Native("TiaPortal.Create", () => new TiaPortal(Engineering.LaunchWithUserInterface ? TiaPortalMode.WithUserInterface : TiaPortalMode.WithoutUserInterface));
                RememberBoundProcess();
                LastConnectInfo = new JsonObject { ["boundProcessId"] = _boundProcessId, ["startedNew"] = true };
            }
            LastConnectError = null;
            if (info != null && LastConnectInfo != null) foreach (var field in LastConnectInfo) info[field.Key] = field.Value?.DeepClone();
            return true;
        }

        public List<string> ListPortalProcessProjects()
        {
            var lines = new List<string>();
            foreach (var process in InvocationJournal.Native("TiaPortal.GetProcesses", () => TiaPortal.GetProcesses().ToList()))
            {
                try { lines.Add(new JsonObject { ["processId"] = process.Id,
                    ["processStartUtc"] = new DateTime(ProcessStart(process.Id), DateTimeKind.Utc).ToString("O"),
                    ["projectPath"] = process.ProjectPath?.FullName, ["tiaMajorVersion"] = Engineering.TiaMajorVersion }.ToJsonString()); }
                catch (Exception ex) { lines.Add("PID=" + process.Id + " metadata unavailable: " + ex.GetType().Name); }
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
            _portal = InvocationJournal.Native("TiaPortal.CreateIsolated", () => new TiaPortal(TiaPortalMode.WithoutUserInterface)); RememberBoundProcess();
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
                var connection = _portal;
                HmiExactAccess.InvalidateTokens();
                _portal = null;
                _project = null; _binding = null;
                _projectOpenedByUs = false;
                _session = null;
                _expectedProjectName = null;
                _boundProcessId = null;
                _softwareContainerCache.Clear(); _plcResolutionCache.Clear(); _softwareCacheProject = null;
                InvalidateHmiSoftwareCache(); ResetHmiReadHealth();
                // TiaPortal.Dispose detaches an attached client; never call
                // TiaPortalProcess.Dispose, which terminates the actual TIA process.
                bool uncertain = _bindingFault != null || Isolation.IsolatedWorkerHost.NativeFault != null;
                _binding = null; _bindingFault = null;
                var lease = _processLease; _processLease = null;
                try { connection?.Dispose(); if (!uncertain) lease?.ReleaseCleanly(); }
                finally { lease?.Dispose(); }
            });
        }

        #endregion

        #region status

        public State GetState()
        {
            // Diagnostics do not inspect collections or silently adopt another project.
            bool alive = _portal != null;
            if (alive && _boundProcessId != null)
            { try { alive = ProcessStart(_boundProcessId.Value) == _processStartTicks; } catch { alive = false; } }
            return new State { IsConnected = alive && _bindingFault == null,
                Project = _binding?.ProjectName ?? "-", Session = _session != null ? _binding?.ProjectName ?? "-" : "-" };
        }

        public bool AttachToOpenProject(string projectName)
        {
            if (string.IsNullOrWhiteSpace(projectName)) throw new ArgumentException("projectName is required.");
            if (_project != null && string.Equals(_binding?.ProjectName, projectName.Trim(), StringComparison.Ordinal))
            { VerifyBinding("AttachToOpenProject"); return true; }
            return ConnectPortal(projectName, false, null);
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

            EnsureBoundProjectUnchanged("Open/create project");
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
                _project = null; _binding = null;
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
                                    _projectOpenedByUs = true;
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
            EnsureBoundProjectUnchanged("Open/create project");
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
                    _project = null; _binding = null;
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
            CaptureBinding();

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
            _binding = null;

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
                _project = null; _binding = null;
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
                        CaptureBinding();
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
                        CaptureBinding();
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

            EnsureBoundProjectUnchanged("Session operation");
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

            EnsureBoundProjectUnchanged("Session operation");
            if (IsSessionNull())
            {
                return false;
            }

            _project = null;
            _projectOpenedByUs = false;
            _session?.Close();
            _session = null;
            _binding = null; _expectedProjectName = null;

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
