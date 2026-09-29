using System;

namespace TiaMcpServer.Siemens
{
    internal static class ProjectOwnership
    {
        internal static bool Owns(object? openedProject, object? currentProject)
        {
            if (openedProject == null || currentProject == null) return false;
            try { return ReferenceEquals(openedProject, currentProject) || openedProject.Equals(currentProject); }
            catch { return false; } // A stale proxy never grants permission to close another project.
        }

        internal static void Release(bool ownsProject, Action close, Action detach, Action<Exception> report)
        {
            try { if (ownsProject) close(); }
            catch (Exception ex) { report(ex); }
            finally { try { detach(); } catch (Exception ex) { report(ex); } }
        }
    }
}
