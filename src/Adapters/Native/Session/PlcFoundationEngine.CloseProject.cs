using System;
using System.IO;
using System.Linq;
using Siemens.Engineering;
using TiaMcp.Adapters.Contracts;

namespace TiaMcp.Adapters
{
    public sealed partial class PlcFoundationEngine
    {
        private sealed class PrimaryProjectCloseResult : PlcMutationResult
        {
            public bool IsModified { get; set; }
            public bool DiscardChanges { get; set; }
            public string PlanHash { get; set; } = "";
        }

        public PlcMutationResult CloseProject(bool dryRun = true, string secondaryProjectFile = "", bool discardChanges = false,
            string expectedPlanHash = "", bool bindingOwnedByCaller = false, bool confirm = false, string expectedProjectFile = "")
        {
            if (secondaryProjectFile.Length > 0)
            {
                if (discardChanges)
                    throw new AdapterPreconditionException("Discard is available only for a primary project.", "secondaryProjectFile", false);
                return CloseSecondaryProject(secondaryProjectFile, dryRun);
            }
            RequireSecondaryClosed("CloseProject");
            PlcLifecyclePolicy.RequireLocalSessionExecution(lifecycle.IsLocalSession, dryRun);
            var target = Project();
            bool modified = target.IsModified;
            lifecycle.RequireClose(modified, discardChanges, ownsPortal == true && workerStartedPortal && bindingOwnedByCaller);
            string file = target.Path.FullName;
            string planHash = MutationIdentityPolicy.ClosePlanHash(file, modified, discardChanges);
            if (!dryRun)
            {
                if (discardChanges)
                    MutationIdentityPolicy.RequireConfirmedProject(confirm, expectedProjectFile, file);
                if ((discardChanges || expectedPlanHash.Length > 0) && !string.Equals(expectedPlanHash, planHash, StringComparison.Ordinal))
                    throw new AdapterPreconditionException("Preview CloseProject again with the same project, modified state and discardChanges.",
                        "expectedPlanHash", false);
#if PROJECT_MULTIUSER
                if (localSession != null) { localSession.Close(); localSession = null; } else
#endif
                    ((Project)target).Close();
                if (discardChanges && portal!.Projects.Any(open => string.Equals(open.Path.FullName, file, StringComparison.OrdinalIgnoreCase)))
                    throw new IOException("Project.Close returned but the project is still listed. Inspect GetProjectInfo before repeating the call.");
                project = null;
                lifecycle.Unbound();
            }
            return new PrimaryProjectCloseResult { Operation = "CloseProject", Executed = !dryRun, ProjectFile = file,
                IsModified = modified, DiscardChanges = discardChanges, PlanHash = planHash };
        }
    }
}
