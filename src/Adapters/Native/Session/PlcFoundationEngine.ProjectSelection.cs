using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Siemens.Engineering;
using TiaMcp.Adapters.Contracts;
#if PROJECT_BASE
using EngineeringProject = Siemens.Engineering.ProjectBase;
#else
using EngineeringProject = Siemens.Engineering.Project;
#endif

namespace TiaMcp.Adapters
{
    public sealed partial class PlcFoundationEngine
    {
        public PlcMutationResult BindProject(string projectName,string expectedProjectFile, string recordedSecondaryProjectFile = "",
            int recordedSecondaryProcessId = 0, long recordedSecondaryProcessStartTicks = 0)
        {
            Portal(); lifecycle.RequireUnbound("AttachOpenProject");
            string full;
            try { full=Path.GetFullPath(expectedProjectFile); }
            catch(ArgumentException ex) {
                throw new AdapterPreconditionException("An absolute expected project file is required.","expectedProjectFile",true,ex); }
            try { MutationIdentityPolicy.RequireSameProject(expectedProjectFile,full); }
            catch(ArgumentException ex) { throw new AdapterPreconditionException(ex.Message,"expectedProjectFile",true,ex); }
            string? reference = ValidateSecondaryRecovery(recordedSecondaryProjectFile, recordedSecondaryProcessId, recordedSecondaryProcessStartTicks);
            EngineeringProject selected;
#if PROJECT_MULTIUSER
            Siemens.Engineering.Multiuser.LocalSession? selectedSession = null;
#endif
            bool session=PlcLifecyclePolicy.IsSessionFile(ReleaseKey,full);
#if PROJECT_MULTIUSER
            if(session)
            {
                selectedSession=portal!.LocalSessions
                    .SingleOrDefault(s=>s.Project.Name==projectName && string.Equals(s.Project.Path.FullName,full,StringComparison.OrdinalIgnoreCase))
                    ?? throw new AdapterPreconditionException("The exact named local session is not open.","expectedProjectFile");
                selected=selectedSession.Project;
            }
            else
#endif
            {
                selected=portal!.Projects.SingleOrDefault(p=>p.Name==projectName && string.Equals(p.Path.FullName,full,StringComparison.OrdinalIgnoreCase));
                if(selected==null) {
                    RequireVisibleProject(full); throw new AdapterPreconditionException("The exact named project is not open.","expectedProjectFile"); }
            }
#if PROJECT_UMAC
            if (!selected.IsPrimary)
                throw new AdapterPreconditionException("AttachOpenProject requires a primary project; a secondary reference cannot be bound as the active " +
                    "project.",
                    "expectedProjectFile", false);
#endif
            project = selected;
#if PROJECT_MULTIUSER
            localSession = selectedSession;
#endif
            lifecycle.Bound(full,false,session);
            RestoreSecondaryRecord(reference, recordedSecondaryProcessId, recordedSecondaryProcessStartTicks);
            if (_resetWorkerFamilyState != null) _resetWorkerFamilyState();
            else { hmiReadFault = null; _hmiTokenGeneration = Guid.NewGuid().ToString("N"); }
#if HMI_UNIFIED
            unifiedPages?.Dispose(); unifiedPages = null;
#endif
            return Mutation("BindProject",false);
        }
        public PlcProjectDetails[] ListProjects()
        {
            var portal=Portal();
            var result=portal.Projects.Select(p=>ProjectDetails(p,false)).ToList();
#if PROJECT_MULTIUSER
            result.AddRange(portal.LocalSessions.Select(s=>ProjectDetails(s.Project,true)));
#endif
            return result.ToArray();
        }
        private static PlcProjectDetails ProjectDetails(EngineeringProject p,bool session) => new PlcProjectDetails {
            Name=p.Name,Attributes=ReadAttributes(p),Meta=new Dictionary<string,object> { ["projectFile"]=p.Path.FullName,["isLocalSession"]=session
#if PROJECT_UMAC
                , ["isPrimary"]=p.IsPrimary
#endif
            } };
        private EngineeringProject ExactOpenProject(string selector)
        {
            var projects = portal!.Projects.Cast<EngineeringProject>()
#if PROJECT_MULTIUSER
                .Concat(portal.LocalSessions.Select(s => (EngineeringProject)s.Project))
#endif
                .ToArray();
            bool byPath = Path.IsPathRooted(selector);
            string? file = byPath ? MutationIdentityPolicy.AbsoluteFile(selector) : SecondaryProjectFile;
            if (file != null)
            {
                var reference = projects.SingleOrDefault(p => string.Equals(p.Path.FullName, file, StringComparison.OrdinalIgnoreCase));
                if (byPath || reference != null && string.Equals(reference.Name, selector, StringComparison.Ordinal))
                    return reference ?? throw new PlcSoftwareException("NotFound", "Exact open project file not found in this Portal instance: " + file);
            }
            var candidates = projects.Where(p => string.Equals(p.Name, selector, StringComparison.Ordinal)).Take(2).ToArray();
            if (candidates.Length > 1) throw new InvalidOperationException("Ambiguous open project name: " + selector);
            return candidates.SingleOrDefault()
                ?? throw new PlcSoftwareException("NotFound", "Exact open project not found in this Portal instance: " + selector);
        }
    }
}
