using System;
using System.Collections.Generic;
using System.Linq;

namespace TiaMcpServer.Siemens
{
    // 2.7.56 (real machine, 2026-09-21): with the maintainer's project and 项目1 open in two TIA processes, ConnectPortal attached to
    // neither within its 2 x 30 s budget and then STARTED A THIRD, EMPTY TIA INSTANCE (the MCP client had long timed out; the engine sat
    // bound to a portal without a project until AttachToOpenProject re-bound it). Rules now: when TIA processes exist the engine attaches
    // or refuses - it never starts an instance unless the caller says allowStart; a wanted project name picks the process that holds it;
    // the per-process attach wait stays inside the client's 60 s. Pure selection / wording here, the Openness calls stay in Portal.cs.
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
                if (byName != null) return byName;
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
            return "TIA Portal is running (" + candidates.Count + " process(es)) but none could be attached: " + string.Join("; ", candidates.Select(Describe))
                + "." + wanted + " No new instance was started. Typical causes: TIA is showing the Openness access dialog for this engine build (allow it on the TIA machine),"
                + " the instance is busy with a modal dialog / compile / download, or a previous attach is still pending. Retry, run ListPortalProcessProjects, or call"
                + " Connect with allowStart=true (or ConnectIsolated) to deliberately start a separate headless instance.";
        }

        internal static string MissingProjectWarning(IReadOnlyList<Candidate> candidates, string projectName, Candidate chosen)
            => "Project '" + projectName + "' is open in none of the attached processes (" + string.Join("; ", candidates.Where(c => c.Attached).Select(Describe))
               + "); bound PID " + chosen.ProcessId + " instead - AttachToOpenProject / OpenProject decide the project.";
    }
}
