using System;

namespace TiaMcpServer.Siemens
{
    internal static class UnifiedScriptAccess
    {
        internal static object Scripts(object hmi) => MigrationRead.Get(hmi, "Scripts")
            ?? throw new NotSupportedException("This HMI has no public Scripts composition.");

        // The Unified composition exposes Count/Item but does not necessarily expose Find.
        internal static object Module(object hmi, string moduleName)
        {
            if (string.IsNullOrWhiteSpace(moduleName)) throw new ArgumentException("An exact moduleName is required.");
            return MigrationRead.Named(Scripts(hmi), moduleName);
        }

        internal static object Resolve(Func<string, object> resolveHmi, string objectPath, string softwarePath, bool collection)
        {
            var path = objectPath ?? "";
            var software = softwarePath ?? "";
            if (string.IsNullOrWhiteSpace(software))
            {
                int colon = path.IndexOf(':');
                if (colon <= 0) throw new ArgumentException("Specify softwarePath; alternatively use HMI:Navigation or HMI:Scripts.");
                software = path.Substring(0, colon);
                path = path.Substring(colon + 1);
            }
            if (collection)
            {
                if (path != "" && path != "Scripts" && path != "/Scripts")
                    throw new ArgumentException("HmiScripts objectPath must be /Scripts (or empty) with an explicit softwarePath.");
                return Scripts(resolveHmi(software));
            }
            if (path.StartsWith("/Scripts/", StringComparison.Ordinal))
            {
                var segment = path.Substring("/Scripts/".Length);
                if (segment.Length == 0 || segment.IndexOf('/') >= 0) throw new ArgumentException("Use /Scripts/<URI-escaped exact module name>.");
                path = Uri.UnescapeDataString(segment);
            }
            else if (path.StartsWith("/", StringComparison.Ordinal)) throw new ArgumentException("Unsupported script path; use an exact module name or /Scripts/<name>.");
            return Module(resolveHmi(software), path);
        }
    }
}
