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

    }
}
