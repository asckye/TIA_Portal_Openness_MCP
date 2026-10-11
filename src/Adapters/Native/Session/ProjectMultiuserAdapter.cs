using System;
using System.Collections.Generic;
using System.Linq;
using Siemens.Engineering;
#if PROJECT_MULTIUSER
using Siemens.Engineering.Multiuser;
#endif
using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Hardware;
using TiaMcp.Adapters.Native.Hmi;
using TiaMcpServer.Siemens;
namespace TiaMcp.Adapters.Native.Session;
#if PROJECT_MULTIUSER
internal sealed class ProjectMultiuserAdapter
{
    private readonly PlcFoundationEngine engine;
    private readonly MultiuserRequest request;
    internal ProjectMultiuserAdapter(PlcFoundationEngine engine, MultiuserRequest request) { this.engine = engine; this.request = request; }
    private void Validate(string stage)
    {
        if (!request.Faults.TryGetValue(stage, out var error)) return;
        if (error.Kind == "state") throw new InvalidOperationException(error.Message);
        throw new ArgumentException(error.Message);
    }
    private static HmiArray Page(List<object?> items, int offset, int limit, HmiObject meta)
    {
        if (offset < 0 || limit < 1 || limit > 500) throw new ArgumentException("offset >= 0, limit 1..500 required.");
        var result = new HmiArray(items.Skip(offset).Take(limit).Select(row => row?.DeepClone()).ToArray());
        if (result.Count < 0 || result.Count > limit || offset + result.Count > items.Count) throw new ArgumentException("Page slice inconsistent with total.");
        meta["expectedCount"] = items.Count; meta["actualCount"] = result.Count; meta["offset"] = offset; meta["limit"] = limit;
        meta["nextOffset"] = offset + result.Count < items.Count ? offset + result.Count : (int?)null;
        meta["truncated"] = offset + result.Count < items.Count;
        return result;
    }
        // ---- Multiuser ----
        private ProjectServer ExactProjectServer(string serverName)
        {
            if (engine.ProjectPortal == null) throw new PortalException(PortalErrorCode.InvalidState, "Connect to TIA first.");
            var matches = engine.ProjectPortal.ProjectServers.Where(s => string.Equals(s.ServerName, serverName, StringComparison.Ordinal)).Take(2).ToArray();
            if (matches.Length > 1) throw new InvalidOperationException("Ambiguous server alias: " + serverName);
            return matches.SingleOrDefault() ?? throw new PortalException(PortalErrorCode.NotFound, "Exact project server alias not found: " + serverName);
        }
        private static ServerProjectInfo ExactServerProject(ProjectServer server, string projectName)
        {
            var matches = server.GetServerProjects().Where(p => string.Equals(p.ProjectName, projectName, StringComparison.Ordinal)).Take(2).ToArray();
            if (matches.Length > 1) throw new InvalidOperationException("Ambiguous server project name: " + projectName);
            return matches.SingleOrDefault() ?? throw new PortalException(PortalErrorCode.NotFound, "Exact server project not found: " + projectName);
        }
        internal SecurityResult ManageMultiuserSession(string action = "read", string serverName = "", string projectName = "", string protocol = "Https", string host = "", int port = 0,
            string commitComment = "", int offset = 0, int limit = 100, bool confirmChange = false, bool dryRun = true)
            => engine.RunProjectLifecycleStep("ManageMultiuserSession", meta => {
                meta["mayHaveChanged"] = false; meta["mayHaveWrittenFiles"] = false;
                Validate("action");
                Validate("page");
                if (engine.ProjectPortal == null) throw new PortalException(PortalErrorCode.InvalidState, "Connect to TIA first.");
                meta["action"] = action; meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false;
                switch (action)
                {
                    case "read":
                    {
                        meta["projectServers"] = new HmiArray(engine.ProjectPortal.ProjectServers.Select(s => (object?)HmiScalar.Read(s)).ToArray());
                        meta["localSessions"] = new HmiArray(engine.ProjectPortal.LocalSessions.Select(s => (object?)s.Project?.Name).ToArray());
                        meta["boundSessionActive"] = engine.ProjectLocalSession != null;
                        if (engine.ProjectLocalSession != null)
                        {
                            var bound = new HmiObject { ["projectName"] = engine.ProjectLocalSession.Project?.Name };
                            try { bound["isUpToDate"] = engine.ProjectLocalSession.IsUptoDate(); } catch (Exception ex) { bound["isUpToDate"] = null; bound["isUpToDateError"] = ex.GetBaseException().Message; }
#if PROJECT_MULTIUSER_MARKINGS
                            Markings markings = engine.ProjectLocalSession.MarkingService.GetMarkings();
                            var all = HardwareGroupOperations.Items(markings.AllMarkings).ToArray();
                            bound["conflictedMarkingCount"] = HardwareGroupOperations.Items(markings.ConflictedMarkings).Count();
                            var rows = Page(all.Select(m => (object?)new HmiObject {
                                ["markState"] = ((Marking)m).MarkState.ToString(), ["objectType"] = ((Marking)m).MarkedObject?.GetType().FullName,
                                ["objectName"] = ((Marking)m).MarkedObject == null ? null : ((Marking)m).MarkedObject.GetType().GetProperty("Name")?.GetValue(((Marking)m).MarkedObject)?.ToString() }).ToList(), offset, limit, meta);
                            bound["markings"] = rows; meta["boundSession"] = bound;
#else
                            meta["dataComplete"] = true;
#endif
#if PROJECT_MULTIUSER_MARKINGS
                            meta["dataComplete"] = offset == 0 && rows.Count == all.Length;
#endif
                            meta["boundSession"] = bound;
                        }
                        else meta["dataComplete"] = true;
                        meta["apiCallSuccess"] = true;
                        return "Multiuser state read: configured servers, open local sessions, bound session up-to-date flag and markings.";
                    }
                    case "listServerProjects":
                    {
                        var server = ExactProjectServer(serverName); meta["server"] = HmiScalar.Read(server);
                        var projects = server.GetServerProjects();
                        var rows = Page(projects.Select(p => (object?)HmiScalar.Read(p)).ToList(), offset, limit, meta);
                        meta["records"] = rows;
#if PROJECT_MULTIUSER_MARKINGS
                        meta["serverGroups"] = new HmiArray(server.GetProjectServerGroups().Select(g => { ProjectServerGroup group = g; return (object?)group.Name; }).ToArray());
#else
                        meta["serverGroups"] = null;
                        meta["unsupportedFields"] = new HmiArray("serverGroups");
#endif
                        meta["apiCallSuccess"] = true; meta["dataComplete"] = offset == 0 && rows.Count == projects.Count;
                        return "Server projects listed for the exact server alias; nothing opened.";
                    }
                    case "readLockState":
                    {
                        var server = ExactProjectServer(serverName); var info = ExactServerProject(server, projectName);
                        LockStateProvider lockState = server.GetLockStateProvider(info);
                        meta["serverProject"] = HmiScalar.Read(info);
                        meta["isProjectLocked"] = lockState.IsProjectLocked(); meta["lockOwner"] = lockState.GetLockOwner();
                        meta["apiCallSuccess"] = true; meta["dataComplete"] = true;
                        return "Server project lock state read.";
                    }
                    case "listLocalSessions":
                    {
                        var server = ExactProjectServer(serverName); var info = ExactServerProject(server, projectName);
                        var sessions = server.GetLocalSessions(info);
                        var rows = Page(sessions.Select(s => (object?)new HmiObject { ["sessionId"] = s.SessionId, ["projectFile"] = s.ProjectFileInfo?.FullName }).ToList(), offset, limit, meta);
                        meta["records"] = rows; meta["serverProject"] = HmiScalar.Read(info);
                        meta["apiCallSuccess"] = true; meta["dataComplete"] = offset == 0 && rows.Count == sessions.Count;
                        return "Local sessions registered on the server for the exact project listed.";
                    }
                    case "connectServer":
                    {
                        if (engine.ProjectPortal.ProjectServers.Any(s => string.Equals(s.ServerName, serverName, StringComparison.Ordinal))) throw new InvalidOperationException("Server alias already configured: " + serverName);
                        var mode = (Protocol)Enum.Parse(typeof(Protocol), protocol);
                        meta["request"] = new HmiObject { ["alias"] = serverName, ["protocol"] = protocol, ["host"] = host, ["port"] = port };
                        if (dryRun) return "Project server connection preview; nothing created.";
                        meta["mayHaveChanged"] = true;
                        var created = engine.ProjectPortal.ProjectServers.Create(serverName, mode, host, port);
                        meta["apiCallSuccess"] = true; meta["after"] = HmiScalar.Read(created);
                        if (created.ServerName != serverName || created.Host != host || created.Port != port) throw new InvalidOperationException("Server connection created but readback differs.");
                        return "Project server connection created and read back. This is a persistent TIA configuration; no project opened.";
                    }
                    case "disconnectServer":
                    {
                        var server = ExactProjectServer(serverName); meta["before"] = HmiScalar.Read(server);
                        if (dryRun) return "Project server connection removal preview; nothing changed.";
                        meta["mayHaveChanged"] = true;
                        server.DeleteConnection(); meta["apiCallSuccess"] = true;
                        if (engine.ProjectPortal.ProjectServers.Any(s => string.Equals(s.ServerName, serverName, StringComparison.Ordinal))) throw new InvalidOperationException("DeleteConnection returned but the alias is still configured.");
                        meta["verifiedAbsent"] = true;
                        return "Project server connection removed and verified absent.";
                    }
                    default: // commit
                    {
                        if (engine.ProjectLocalSession == null) throw new PortalException(PortalErrorCode.InvalidState, "No bound local session; commit applies only to a multiuser local session opened via OpenSession.");
                        meta["sessionProject"] = engine.ProjectLocalSession.Project?.Name; meta["commitComment"] = commitComment;
                        if (dryRun) return "Commit preview: CloseAndCommit would commit the local session changes to the server AND close the bound session. Nothing executed.";
                        meta["mayHaveChanged"] = true; meta["sessionClosed"] = false;
                        int committed = engine.ProjectLocalSession.CloseAndCommit(commitComment);
                        meta["apiCallSuccess"] = true; meta["nativeResult"] = committed; meta["sessionClosed"] = true;
                        engine.ProjectReleaseBinding();
                        return "Local session committed and closed by the native CloseAndCommit; the MCP project binding was cleared. Re-open a session explicitly to continue.";
                    }
                }
            }, requiresProject: false);

}
#endif
