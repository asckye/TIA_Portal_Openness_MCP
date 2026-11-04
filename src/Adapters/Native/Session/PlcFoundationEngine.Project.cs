using System.Xml.Linq;
using System;
using TiaMcp.Adapters.Native.Plc;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using Siemens.Engineering;
using Siemens.Engineering.Compiler;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.Tags;
using Siemens.Engineering.SW.Types;
using TiaMcpServer.Siemens;
using TiaMcp.Adapters.Contracts;
#if PROJECT_BASE
using EngineeringProject = Siemens.Engineering.ProjectBase;
#else
using EngineeringProject = Siemens.Engineering.Project;
#endif

namespace TiaMcp.Adapters
{
    /// <summary>
    /// Shared typed PLC operations for the selected release.
    /// Native acceptance is tracked in docs/reference/real-machine-ledger.md.
    /// The host supplies exact assembly resolution and one release-specific owner thread.
    /// </summary>
    public sealed partial class PlcFoundationEngine : IDisposable
    {
        public PlcMutationResult BindProject(string projectName,string expectedProjectFile)
        {
            Portal(); lifecycle.RequireUnbound();
            string full;
            try { full=Path.GetFullPath(expectedProjectFile); }
            catch(ArgumentException ex) {
                throw new AdapterPreconditionException("An absolute expected project file is required.","expectedProjectFile",true,ex); }
            try { MutationIdentityPolicy.RequireSameProject(expectedProjectFile,full); }
            catch(ArgumentException ex) { throw new AdapterPreconditionException(ex.Message,"expectedProjectFile",true,ex); }
            bool session=PlcLifecyclePolicy.IsSessionFile(ReleaseKey,full);
#if PROJECT_MULTIUSER
            if(session)
            {
                localSession=portal!.LocalSessions
                    .SingleOrDefault(s=>s.Project.Name==projectName && string.Equals(s.Project.Path.FullName,full,StringComparison.OrdinalIgnoreCase))
                    ?? throw new AdapterPreconditionException("The exact named local session is not open.","expectedProjectFile");
                project=localSession.Project;
            }
            else
#endif
            {
                project=portal!.Projects.SingleOrDefault(p=>p.Name==projectName && string.Equals(p.Path.FullName,full,StringComparison.OrdinalIgnoreCase));
                if(project==null) {
                    RequireVisibleProject(full); throw new AdapterPreconditionException("The exact named project is not open.","expectedProjectFile"); }
            }
            lifecycle.Bound(full,false,session);
            if (_resetWorkerFamilyState != null) _resetWorkerFamilyState();
            else { hmiReadFault = null; _hmiTokenGeneration = Guid.NewGuid().ToString("N"); }
#if HMI_UNIFIED
            unifiedPages?.Dispose(); unifiedPages = null;
#endif
            return Mutation("BindProject",false);
        }
        // UMAC credentials (V15.1+) are optional; the password is converted to SecureString here and never logged or echoed.
        public PlcMutationResult OpenProject(string path, bool dryRun = true, string umacUserName = "", string umacPassword = "", string umacUserType = "")
        {
            var p = Portal();
            lifecycle.RequireUnbound();
            bool session=PlcLifecyclePolicy.IsSessionFile(ReleaseKey,path);
            PlcLifecyclePolicy.RequireLocalSessionExecution(session,dryRun);
            var input = new FileInfo(TiaOpenness.Shared.NativeInputPolicy.FullPath(Path.GetFullPath(path)));
            if (!input.Exists) throw new AdapterPreconditionException("Project file does not exist.", "path", true, new FileNotFoundException());
            if (p.Projects.Any(x => string.Equals(x.Path.FullName, input.FullName, StringComparison.OrdinalIgnoreCase)))
                throw new AdapterPreconditionException("Project is already open; bind it explicitly instead.","path");
#if PROJECT_MULTIUSER
            if(p.LocalSessions.Any(s=>string.Equals(s.Project.Path.FullName,input.FullName,StringComparison.OrdinalIgnoreCase)))
                throw new AdapterPreconditionException("Local session already open; bind it explicitly.","path");
#endif
            RequireNoOpenProject(p, "OpenProject");
            using var login = UmacCredentialsFor(umacUserName, umacPassword, umacUserType, "umacUserName");
            if (!dryRun)
            {
#if PROJECT_MULTIUSER
                if(session) { localSession=OpennessRefusalMapping.Run("open", login.Given, () => p.LocalSessions.Open(input)); project=localSession.Project; }
                else
#endif
                    project=OpenProjectFile(p, input, login);
                lifecycle.Bound(project.Path.FullName,true,session);
            }
            var result=Mutation("OpenProject",dryRun); result.ProjectFile=dryRun ? input.FullName : project!.Path.FullName; return result;
        }
        private static UmacLogin UmacCredentialsFor(string userName, string password, string userType, string parameter)
        {
#if !PROJECT_UMAC
            if (!string.IsNullOrEmpty(userName) || !string.IsNullOrEmpty(password) || !string.IsNullOrEmpty(userType))
                throw new AdapterPreconditionException("UMAC credentials need the UmacDelegate overloads of V15.1 or later.", parameter);
#endif
            try { return new UmacLogin(userName, password, userType); }
            catch (ArgumentException error) { throw new AdapterPreconditionException(error.Message, parameter, true, error); }
        }
        private static EngineeringProject OpenProjectFile(TiaPortal connection, FileInfo input, UmacLogin login)
        {
#if PROJECT_UMAC
            using (login.Authenticate(connection))
                return OpennessRefusalMapping
                    .Run("open", login.Given, () => login.Delegate == null ? connection.Projects.Open(input) : connection.Projects.Open(input, login.Delegate));
#else
            return OpennessRefusalMapping.Run("open", login.Given, () => connection.Projects.Open(input));
#endif
        }
        public PlcMutationResult CreateProject(string directoryPath, string projectName, bool dryRun = true)
        {
            var p = Portal();
            PlcFoundationPolicy.RequireName(projectName);
            lifecycle.RequireUnbound();
            var parent = new DirectoryInfo(TiaOpenness.Shared.NativeInputPolicy.FullPath(Path.GetFullPath(directoryPath)));
            if (!parent.Exists || Directory.Exists(Path.Combine(parent.FullName, projectName)) || File.Exists(Path.Combine(parent.FullName, projectName)))
                throw new AdapterPreconditionException("Use an existing parent directory and a new project name.",!parent.Exists ? "directoryPath"
                        : "projectName");
            var expected=PlcLifecyclePolicy.CreationFile(ReleaseKey,parent.FullName,projectName);
            RequireNoOpenProject(p, "CreateProject");
            if (!dryRun) {
                project = OpennessRefusalMapping.Run("create", false, () => p.Projects.Create(parent, projectName)); lifecycle
                    .Bound(project.Path.FullName,true,false);
            }
            var result=Mutation("CreateProject",dryRun); result.ProjectFile=dryRun ? expected : project!.Path.FullName; return result;
        }
        public PlcMutationResult SaveProject(bool dryRun = true)
        {
            PlcLifecyclePolicy.RequireLocalSessionExecution(lifecycle.IsLocalSession,dryRun);
            var p = Project();
            if (!dryRun)
            {
#if PROJECT_MULTIUSER
                if(localSession!=null) localSession.Save(); else
#endif
                    ((Project)p).Save();
            }
            return Mutation("SaveProject", dryRun);
        }
        public PlcMutationResult CloseProject(bool dryRun = true)
        {
            PlcLifecyclePolicy.RequireLocalSessionExecution(lifecycle.IsLocalSession,dryRun);
            var p = Project();
            lifecycle.RequireClose(p.IsModified);
            var file=p.Path.FullName;
            if (!dryRun)
            {
#if PROJECT_MULTIUSER
                if(localSession!=null) { localSession.Close(); localSession=null; } else
#endif
                    ((Project)p).Close();
                project=null; lifecycle.Unbound();
            }
            var result=Mutation("CloseProject",dryRun); result.ProjectFile=file; return result;
        }
    }
}
