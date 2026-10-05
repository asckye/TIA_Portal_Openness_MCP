using System;
using System.Collections.Generic;
using System.Linq;

namespace TiaMcpServer.Siemens
{
    // Real-machine observation, TIA V21, 2026-09-21 (docs/reference/real-machine-ledger.md): with two projects
    // including 项目1 open in separate TIA processes, ConnectPortal attached to neither within its 2 x 30 s budget,
    // then started a third, empty instance after the MCP client timed out. AttachToOpenProject restored the binding.
    // When TIA processes exist, attach or refuse; start an instance only with allowStart. A requested project name
    // must select its owning process, and each attach wait must fit within the client's 60 s budget.
    // Pure selection / wording here; the Openness calls stay in Portal.cs.
    internal static class ConnectLogic
    {
        internal const int AttachTimeoutMsPerProcess = 20000;
        internal const int AttachTotalBudgetMs = 45000;

        internal sealed class Candidate
        {
            public int ProcessId;
            public bool Attached;
            public bool HasSession;
            public List<string> ProjectNames = new List<string>();
            public string? Failure;
        }

        // The process to bind: the one holding the wanted project first, else the first one with a project or session, else the first
        // attachable one. Null when nothing attached.
        internal static Candidate? Choose(IReadOnlyList<Candidate> candidates, string? projectName)
        {
            var attached = candidates.Where(c => c.Attached).ToList();
            if (attached.Count == 0) return null;
            if (!string.IsNullOrWhiteSpace(projectName))
            {
                var wantedName = projectName!.Trim();
                var byName = attached.FirstOrDefault(c => c.ProjectNames.Any(n => string.Equals(n, wantedName, StringComparison.Ordinal)));
                // An empty instance can be used to open the requested project later.
                // A different open project is never a substitute for an explicit name.
                return byName ?? attached.FirstOrDefault(c => !c.HasSession && c.ProjectNames.Count == 0);
            }
            return attached.FirstOrDefault(c => c.HasSession || c.ProjectNames.Count > 0) ?? attached[0];
        }

        internal static string Describe(Candidate c)
            => "PID " + c.ProcessId + ": " + (c.Attached
                ? (c.ProjectNames.Count > 0 ? "project " + string.Join(" / ", c.ProjectNames) : c.HasSession ? "multi-user session" : "no project")
                : "not attachable (" + (c.Failure ?? "timeout") + ")");

        internal static string Refusal(IReadOnlyList<Candidate> candidates, string? projectName)
        {
            var wanted = string.IsNullOrWhiteSpace(projectName) ? "" : " Project '" + projectName!.Trim() + "' was not found in any of them.";
            return "TIA Portal is running (" + candidates.Count + " process(es)) but no acceptable target could be bound: " + string.Join("; ", candidates.Select(Describe))
                + "." + wanted + " No new instance was started. Typical causes: TIA is showing the Openness access dialog for this engine build (allow it on the TIA machine),"
                + " the instance is busy with a modal dialog / compile / download, or a previous attach is still pending. Retry, run ListPortalProcessProjects, or call"
                + " ConnectPortal with allowStart=true (or ConnectIsolatedPortal) to deliberately start a separate headless instance.";
        }

        internal static string MissingProjectWarning(IReadOnlyList<Candidate> candidates, string projectName, Candidate chosen)
            => "Project '" + projectName + "' is open in none of the attached processes (" + string.Join("; ", candidates.Where(c => c.Attached).Select(Describe))
               + "); bound PID " + chosen.ProcessId + " instead - AttachOpenProject / OpenProject decide the project.";
    }
}
