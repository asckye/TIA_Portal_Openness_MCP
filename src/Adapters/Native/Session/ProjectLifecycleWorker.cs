using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Siemens.Engineering;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Native.Hmi;
using TiaMcp.Adapters.Native.Session;
using TiaMcpServer.Siemens;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcp.Adapters;

public sealed partial class PlcFoundationEngine
{
    // I1 supplies the existing binding/lease owner. These hooks only copy handles
    // and cached identity, and run synchronously on this engine's owning lane.
    public Action<TiaPortal>? ProjectConnectionCreated { get; set; }
    public Action<object>? ProjectRetrieved { get; set; }
    public Action? ProjectSavedAs { get; set; }
    public Action? ProjectReleased { get; set; }
    public Func<Dictionary<string, object?>>? ProjectCachedBinding { get; set; }
    public Func<string?>? ProjectExpectedProjectName { get; set; }
    public Func<bool>? ProjectEnsureGroup { get; set; }
    public Func<ProjectScaffoldPrimitive, object>? ProjectScaffoldOperation { get; set; }
    public Action? ProjectClearConnectError { get; set; }
    internal TiaPortal? ProjectPortal => portal;
#if PROJECT_MULTIUSER
    internal Siemens.Engineering.Multiuser.LocalSession? ProjectLocalSession => localSession;
#endif
    private string? ProjectExpectedName => ProjectExpectedProjectName != null ? ProjectExpectedProjectName()
        : lifecycle.ProjectFile == null ? null : Path.GetFileNameWithoutExtension(lifecycle.ProjectFile);
    internal SecurityResult RunProjectLifecycleStep(string tool, Func<HmiObject, string> action, bool requiresProject = true)
    {
        Check();
        var reply = RunHmiStep(tool, values => {
            var meta = new HmiObject(values);
            try { return action(meta); }
            finally { foreach (var field in meta) values[field.Key] = field.Value; }
        }, requiresProject);
        return new SecurityResult { Message = reply.Message, Meta = reply.Meta, RequiresSessionReset = reply.RequiresSessionReset };
    }
    public SecurityResult ConnectIsolatedPortal()
    {
        Check();
        if (portal != null || project != null || lifecycle.IsLocalSession)
            throw new PortalException(PortalErrorCode.InvalidState,
                "ConnectIsolatedPortal: this MCP session already owns a TIA connection. Start a fresh MCP process before calling ConnectIsolatedPortal.");
        if (ProjectConnectionCreated == null)
            throw new InvalidOperationException("The shared process lease owner is unavailable for isolated creation.");
        ProjectClearConnectError?.Invoke();
        portal = InvocationJournal.Native("TiaPortal.CreateIsolated", () => new TiaPortal(TiaPortalMode.WithoutUserInterface));
        ProjectConnectionCreated(portal);
        ownsPortal = true;
        bool connected = portal != null;
        return new SecurityResult {
            Message = connected ? "Connected to isolated headless TIA Portal (no existing TIA window or project was touched)."
                : "Unverified: ConnectIsolatedPortal returned without an error, but the portal handle could not be read. Whether the isolated instance started is unknown. Inspect GetSessionState before continuing.",
            Meta = HmiMeta.Basic(connected, ("verified", connected), ("isolated", true))
        };
    }
    public void AdoptCreatedProjectLifecycleProcess(int processId, long processStartTicks)
    {
        Check();
        lifecycle.Attached(processId);
        sessionCandidateStart = new DateTimeOffset(new DateTime(processStartTicks, DateTimeKind.Utc));
        ownsPortal = true;
    }
    public void RefreshProjectLifecyclePath()
    {
        Check();
        lifecycle.Adopt(new PlcRuntimeState { ProcessId = lifecycle.ProcessId, IsAttached = true,
            ProjectFile = project!.Path.FullName, OwnsProject = lifecycle.OwnsProject, IsLocalSession = lifecycle.IsLocalSession });
        sessionCandidateAdapter = null; saveCloseAdapter = null;
    }
    public void AdoptProjectLifecycleRetrieved(object returned)
    {
        Check();
#if PLC_SAFETY
        project = returned as Siemens.Engineering.ProjectBase;
#else
        project = returned as Siemens.Engineering.Project;
#endif
        if (project == null) throw new InvalidOperationException("Retrieve returned no project; inspect destination for partial files.");
        lifecycle.Bound(project.Path.FullName, true, false);
    }
    public void ReleaseProjectLifecycleProject()
    {
        Check();
#if PROJECT_MULTIUSER
        localSession = null;
#endif
        project = null; lifecycle.Unbound();
    }

    // The OS group helper is managed, but stays on the selected session lane.
    public bool EnsureOpennessUserGroup()
    {
        Check();
        return (ProjectEnsureGroup ?? throw new InvalidOperationException("Openness group helper unavailable."))();
    }
    public Dictionary<string, object?> ReadProjectLifecycleBinding()
    {
        Check();
        return (ProjectCachedBinding ?? throw new InvalidOperationException("Cached project binding unavailable."))();
    }
    // Archive in the internal name selects P8-64's existing long-operation timeout
    // without changing that task's policy files or the MCP SaveProjectCopy name.
    public bool ArchiveSaveProjectCopy(string path, bool dryRun = true)
    {
        Check();
#if PROJECT_EXCHANGE
        if (dryRun) throw new ArgumentException("SaveProjectCopy has no native preview mode.");
        if (PlcB5Session.IsProjectNull()) return false;
        var directory = new DirectoryInfo(TiaOpenness.Shared.NativeInputPolicy.FullPath(path));
        (project as Project)?.SaveAs(directory);
        (ProjectSavedAs ?? throw new InvalidOperationException("Shared Save As binding callback unavailable."))();
        return true;
#else
        throw new NotSupportedException("Project.SaveAs requires V15.1 or later.");
#endif
    }
    public SecurityResult ArchiveSavedProject(string archivePath, bool dryRun = true)
    {
        Check();
#if PROJECT_EXCHANGE
        return RunProjectLifecycleStep("ArchiveSavedProject", meta => {
            using var exclusive = dryRun ? null : SecurityEditAccess();
            var result = ProjectArchive.Create(project!, archivePath, ReleaseKey, dryRun);
            foreach (var field in result) meta[field.Key] = field.Value?.DeepClone();
            meta["operationSuccess"] = result["success"]?.DeepClone();
            return dryRun ? "Native project archive preview; no file written."
                : "Native archive operation completed; inspect success and validation. Retrieval is not tested.";
        });
#else
        throw new NotSupportedException("Project.Archive requires V15.1 or later.");
#endif
    }
    public SecurityResult RetrieveProjectArchive(ProjectRetrieveRequest request)
    {
        Check();
#if PROJECT_EXCHANGE
        return RunProjectLifecycleStep("RetrieveProjectArchive", meta => {
            if (portal == null) throw new InvalidOperationException("Connect to TIA first.");
            if (project != null || lifecycle.IsLocalSession || portal.Projects.Any()
#if PROJECT_MULTIUSER
                || portal.LocalSessions.Any()
#endif
                ) throw new InvalidOperationException("Use a connected Portal with no open project/session. Existing projects will never be closed automatically.");
            var file = new FileInfo(TiaOpenness.Shared.NativeInputPolicy.FullPath(request.ArchivePath));
            if (!file.Exists) throw new FileNotFoundException("Archive not found.");
            if (!Path.IsPathRooted(request.DestinationDirectory)) throw new ArgumentException("Absolute destination required.");
            var directory = new DirectoryInfo(TiaOpenness.Shared.NativeInputPolicy.FullPath(request.DestinationDirectory));
            if (directory.Exists) throw new IOException("Destination must be new; merge/overwrite refused.");
            meta["dryRun"] = request.DryRun; meta["upgrade"] = request.Upgrade; meta["mayHaveWrittenFiles"] = false; meta["destination"] = directory.FullName;
            if (!request.DryRun)
            {
                meta["mayHaveWrittenFiles"] = true;
                var retrieved = request.Upgrade ? portal.Projects.RetrieveWithUpgrade(file, directory) : portal.Projects.Retrieve(file, directory);
                (ProjectRetrieved ?? throw new InvalidOperationException("Shared retrieval binding callback unavailable."))(retrieved);
                meta["project"] = HmiScalar.Read(project!); meta["openedProject"] = true;
            }
            return request.DryRun ? "Archive retrieval preview; no project opened or files created."
                : "Archive retrieved and returned project bound. No compile/download; native retrieval may upgrade only when upgrade=true.";
        }, requiresProject: false);
#else
        throw new NotSupportedException("ProjectComposition.Retrieve requires V15.1 or later.");
#endif
    }
    internal void ProjectReleaseBinding()
        => (ProjectReleased ?? throw new InvalidOperationException("Shared session release callback unavailable."))();
    public SecurityResult ExecuteMultiuserSession(MultiuserRequest request)
    {
        Check();
#if PROJECT_MULTIUSER
        return new ProjectMultiuserAdapter(this, request).ManageMultiuserSession(request.Action, request.ServerName,
            request.ProjectName, request.Protocol, request.Host, request.Port, request.CommitComment,
            request.Offset, request.Limit, request.ConfirmChange, request.DryRun);
#else
        throw new NotSupportedException("Multiuser local sessions require V17 or later.");
#endif
    }
    public object ExecuteProjectScaffoldPrimitive(ProjectScaffoldPrimitive request)
    {
        Check();
#if PROJECT_SCAFFOLD
        return (ProjectScaffoldOperation ?? throw new InvalidOperationException("Scaffold family bridge unavailable."))(request);
#else
        throw new NotSupportedException("The scaffold S7DCL import requires V20 or later.");
#endif
    }
}
