using System;
using System.Collections.Generic;

namespace TiaOpenness.Shared
{
    internal static class NativePathSelection
    {
        // Caller paths reach the SDK as full paths with native separators (V14 SP1 refuses forward slashes).
        internal static string NormalizeSeparators(string path)
            => path.Replace(System.IO.Path.AltDirectorySeparatorChar, System.IO.Path.DirectorySeparatorChar);
        internal static string FullPath(string path) => System.IO.Path.GetFullPath(NormalizeSeparators(path));

        internal static bool ShortAlias(string[] parts, params string[] names)
        {
            if(parts.Length!=1) return false;
            foreach(var name in names) if(string.Equals(parts[0],name,StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        // Inventory construction belongs to each SDK wrapper. Exact addresses take
        // precedence over aliases; neither an ambiguous nor a traversal path selects.
        internal static int Select(string path, IReadOnlyList<string> exactPaths, Func<int, string[], bool> aliasMatch)
        {
            if (string.IsNullOrWhiteSpace(path)) return -3;
            var parts = path.Split('/');
            foreach (var part in parts) if (part.Length == 0 || part == "." || part == "..") return -3;
            int exact = -1;
            for (int i = 0; i < exactPaths.Count; i++)
                if (string.Equals(exactPaths[i], path, StringComparison.Ordinal))
                { if (exact >= 0) return -2; exact = i; }
            if (exact >= 0) return exact;
            int match = -1;
            for (int i = 0; i < exactPaths.Count; i++)
                if (aliasMatch(i, parts)) { if (match >= 0) return -2; match = i; }
            return match;
        }
    }
}
