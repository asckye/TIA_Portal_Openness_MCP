using System;
using System.Collections.Generic;
namespace TiaMcpServer.Siemens
{
    internal static class ExactSoftwareMatch
    {
        internal static T? Select<T>(IEnumerable<T> candidates, string name, Func<T,string?> nameOf) where T : class
        {
            T? found = null;
            foreach (var candidate in candidates)
            {
                if (!string.Equals(nameOf(candidate), name, StringComparison.OrdinalIgnoreCase)) continue;
                if (found != null && !ReferenceEquals(found, candidate))
                    throw new PortalException(PortalErrorCode.InvalidParams,
                        $"Ambiguous software name '{name}'; use a group-qualified device/software path.");
                found = candidate;
            }
            return found;
        }
    }
}
