using System;
using System.Collections.Generic;

namespace TiaMcpServer.Siemens
{
    // The native adapters and the offline regression use the same fail-closed traversal.
    internal static class CrossReferenceTreeReader
    {
        internal static List<TEntry> Read<TSource, TReference, TLocation, TEntry>(IEnumerable<TSource> roots,
            Func<TSource, IEnumerable<TReference>> references, Func<TReference, IEnumerable<TLocation>> locations,
            Func<TSource, IEnumerable<TSource>> children, Func<TSource, TReference, TLocation?, TEntry> entry) where TLocation : class
        {
            var rows = new List<TEntry>(); int visited = 0;
            void Add(TSource source, TReference reference, TLocation? location)
            {
                if (rows.Count >= 100000) throw new InvalidOperationException("Cross-reference row limit reached; result incomplete.");
                rows.Add(entry(source, reference, location));
            }
            void Walk(IEnumerable<TSource> sources, int depth)
            {
                if (depth > 64) throw new InvalidOperationException("Cross-reference depth limit reached; result incomplete.");
                foreach (var source in sources)
                {
                    if (++visited > 100000) throw new InvalidOperationException("Cross-reference source limit reached; result incomplete.");
                    foreach (var reference in references(source))
                    {
                        bool any = false;
                        foreach (var location in locations(reference)) { any = true; Add(source, reference, location); }
                        if (!any) Add(source, reference, null);
                    }
                    Walk(children(source), depth + 1);
                }
            }
            Walk(roots, 0);
            return rows; // No partial list escapes when a getter/enumerator/factory fails.
        }
    }
}
