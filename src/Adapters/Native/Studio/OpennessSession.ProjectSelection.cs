#nullable disable
using System;
using System.Linq;
using Siemens.Engineering;

namespace TiaOpenness.Openness
{
    internal sealed partial class OpennessSession
    {
        private static Project RequirePrimaryProject(Project candidate)
        {
#if PROJECT_UMAC || PROJECT_IS_PRIMARY
            if (!candidate.IsPrimary)
                throw new InvalidOperationException("OpenProject requires a primary project; an open secondary reference cannot be reused as the active " +
                    "project.");
#endif
            return candidate;
        }
        private static Project PrimaryProject(TiaPortal portal)
        {
            var projects = portal.Projects.SafeEnumerate().ToList();
#if PROJECT_UMAC || PROJECT_IS_PRIMARY
            var primary = projects.Where(p => p.IsPrimary).ToList();
            return primary.Count == 1 ? primary[0] : null;
#else
            return projects.Count == 1 ? projects[0] : null;
#endif
        }

    }
}
