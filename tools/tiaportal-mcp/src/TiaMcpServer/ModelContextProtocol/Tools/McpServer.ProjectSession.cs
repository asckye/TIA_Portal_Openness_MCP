using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml.Linq;
using TiaMcpServer.Siemens;


namespace TiaMcpServer.ModelContextProtocol
{
    public static partial class McpServer
    {
        #region project/session

        public static ResponseGetProjects GetProjects()
            => ((ProjectSessionTools)EngineServices.Get(typeof(ProjectSessionTools))).GetProjects();

        public static ResponseOpenProject OpenProject(
            string path,
            bool closeForeignProject = false,
            string umacUserName = "",
            string umacPassword = "",
            string umacUserType = "")
            => ((ProjectSessionTools)EngineServices.Get(typeof(ProjectSessionTools))).OpenProject(path, closeForeignProject, umacUserName, umacPassword, umacUserType);

        public static ResponseMessage AttachToOpenProject(
            string projectName)
            => ((ProjectSessionTools)EngineServices.Get(typeof(ProjectSessionTools))).AttachToOpenProject(projectName);

        public static ResponseMessage CreateProject(
            string directoryPath,
            string projectName,
            bool closeForeignProject = false)
            => ((ProjectSessionTools)EngineServices.Get(typeof(ProjectSessionTools))).CreateProject(directoryPath, projectName, closeForeignProject);

        public static ResponseScaffold ScaffoldProject(
            string spec,
            bool dryRun = true)
            => ((ProjectSessionTools)EngineServices.Get(typeof(ProjectSessionTools))).ScaffoldProject(spec, dryRun);

        public static ResponseSaveProject SaveProject()
            => ((ProjectSessionTools)EngineServices.Get(typeof(ProjectSessionTools))).SaveProject();

        public static ResponseSaveAsProject SaveAsProject(
            string newProjectPath)
            => ((ProjectSessionTools)EngineServices.Get(typeof(ProjectSessionTools))).SaveAsProject(newProjectPath);

        public static ResponseCloseProject CloseProject()
            => ((ProjectSessionTools)EngineServices.Get(typeof(ProjectSessionTools))).CloseProject();

        internal static class SessionToolSupport
        {
            internal static IReadOnlyList<string> GetOnlineMonitoringSafetyPolicy() => McpServer.GetOnlineMonitoringSafetyPolicy();
            internal static List<string> GetMcpToolNames() => McpServer.GetMcpToolNames();
            internal static void ApplyScaffoldPlcElements(JsonNode root, string plcName, ResponseScaffold resp)
                => McpServer.ApplyScaffoldPlcElements(root, plcName, resp);
            internal static void CompileScaffoldPlc(string plcName, ResponseScaffold resp)
                => McpServer.CompileScaffoldPlc(plcName, resp);
            internal static void ApplyScaffoldHmi(JsonNode root, string plcName, string hmiName,
                string hmiSoftwarePathSpec, string connectionName, ResponseScaffold resp)
                => McpServer.ApplyScaffoldHmi(root, plcName, hmiName, hmiSoftwarePathSpec, connectionName, resp);
        }

        #endregion
    }
}
