using System;
using System.Collections.Generic;
using System.Linq;

namespace TiaMcp.Adapters.Engineering
{
    internal static class EngineeringEvidence
    {
        internal static List<object?> Page(List<object?> rows, int offset, int limit, Dictionary<string, object?> meta)
        {
            ValidatePage(offset, limit);
            var page = rows.Skip(offset).Take(limit).ToList();
            if (offset + page.Count > rows.Count) throw new ArgumentException("Page slice inconsistent with total.");
            meta["expectedCount"] = rows.Count; meta["actualCount"] = page.Count; meta["offset"] = offset; meta["limit"] = limit;
            meta["nextOffset"] = offset + page.Count < rows.Count ? offset + page.Count : (int?)null;
            meta["truncated"] = offset + page.Count < rows.Count;
            return page;
        }
        internal static void ValidatePage(int offset, int limit)
        {
            if (offset < 0 || limit < 1 || limit > 500) throw new ArgumentException("offset >= 0, limit 1..500 required.");
        }
        internal static void ValidateDepth(int maxDepth)
        {
            if (maxDepth < 1 || maxDepth > 32) throw new ArgumentException("maxDepth 1..32 required.");
        }
        internal static bool IsIdentical(string? state) => state != null && new[] { "ObjectsIdentical", "ContainerContentsIdentical", "FolderContentsIdentical", "CompareIrrelevant" }.Contains(state, StringComparer.Ordinal);
        internal static List<Dictionary<string, object?>> FlattenCompareTree(object? root, Func<object, IEnumerable<object>> children,
            Func<object, Dictionary<string, object?>> describe, int maxDepth, int maxRows, out bool truncated)
        {
            var rows = new List<Dictionary<string, object?>>(); truncated = false;
            if (root == null) return rows;
            var stack = new Stack<(object Node, string Path, int Depth)>(); stack.Push((root, "", 0));
            while (stack.Count > 0)
            {
                var (node, parentPath, depth) = stack.Pop();
                if (rows.Count >= maxRows) { truncated = true; break; }
                var row = describe(node); string name = (string?)row["leftName"] ?? "";
                if (name.Length == 0) name = (string?)row["rightName"] ?? "";
                string path = parentPath.Length == 0 ? name : parentPath + "/" + name;
                var kids = children(node).ToList(); row["path"] = path; row["depth"] = depth; row["childCount"] = kids.Count;
                if (depth >= maxDepth && kids.Count > 0) { row["childrenOmitted"] = true; truncated = true; kids.Clear(); }
                rows.Add(row);
                for (int i = kids.Count - 1; i >= 0; i--) stack.Push((kids[i], path, depth + 1));
            }
            return rows;
        }
        internal static Dictionary<string, object?> Summarize(IEnumerable<Dictionary<string, object?>> rows)
        {
            var summary = new Dictionary<string, object?>();
            foreach (var group in rows.GroupBy(r => (string?)r["comparisonResult"] ?? "Unknown").OrderBy(g => g.Key, StringComparer.Ordinal)) summary[group.Key] = group.Count();
            return summary;
        }
    }
}
