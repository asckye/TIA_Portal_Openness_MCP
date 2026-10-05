using System;
using System.Collections.Generic;
using System.Linq;

namespace TiaMcpServer.ModelContextProtocol
{
    // Pure selection policy: no Siemens references and no filesystem/native side effects.
    internal static class ImportSelectionPolicy
    {
        internal const string OrderingDescription = "Types-first lexical scheduling, not dependency resolution: types, tag tables, technology objects, then blocks by subtype; lexical file path within each phase/subtype.";

        internal sealed class Conflict
        {
            internal string Kind { get; set; } = "";
            internal string ObjectName { get; set; } = "";
            internal int CandidateCount { get; set; }
            internal bool PathsTruncated => CandidateCount > Paths.Count;
            internal List<string> Paths { get; set; } = new List<string>();
            internal string Message => $"Ambiguous import candidates for {DiagnosticText(Kind)} '{DiagnosticText(ObjectName)}': {string.Join("; ", Paths)} (candidateCount={CandidateCount}, pathsTruncated={PathsTruncated}). Entire selected batch rejected before import or compile; remove duplicates or narrow the selection.";
        }

        private static string DiagnosticText(string value)
        {
            var clean = new string(value.Take(256).Select(c => char.IsControl(c) ? ' ' : c).ToArray());
            return value.Length > 256 ? clean + "...[truncated]" : clean;
        }

        internal static List<Conflict> FindConflicts<T>(IEnumerable<T> candidates,
            Func<T, string> kind, Func<T, string> objectName, Func<T, string> path)
        {
            // Group separately to avoid delimiter collisions in compound identity strings.
            return candidates.GroupBy(kind, StringComparer.OrdinalIgnoreCase)
                .SelectMany(k => k.GroupBy(objectName, StringComparer.OrdinalIgnoreCase)
                    .Where(g => g.Count() > 1)
                    .Select(g => new Conflict
                    {
                        Kind = k.Select(kind).OrderBy(x => x, StringComparer.Ordinal).First(),
                        ObjectName = g.Select(objectName).OrderBy(x => x, StringComparer.Ordinal).First(),
                        CandidateCount = g.Count(),
                        Paths = g.Select(path).OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                            .ThenBy(x => x, StringComparer.Ordinal).Take(8).Select(DiagnosticText).ToList()
                    }))
                .OrderBy(x => x.Kind, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.Kind, StringComparer.Ordinal)
                .ThenBy(x => x.ObjectName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.ObjectName, StringComparer.Ordinal).ToList();
        }
    }
}
