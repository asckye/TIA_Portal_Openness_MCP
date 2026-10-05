using System;
using System.Collections.Generic;
using System.Linq;

namespace TiaMcpServer.Siemens
{
    internal static class PlcBlockLookup
    {
        internal static TBlock? Find<TGroup,TBlock>(TGroup root, string path,
            Func<TGroup,string> groupName, Func<TGroup,IEnumerable<TGroup>> children,
            Func<TGroup,IEnumerable<TBlock>> blocks, Func<IEnumerable<TBlock>,string,TBlock?> select)
            where TGroup : class where TBlock : class
        {
            var parts = (path ?? "").Replace('\\','/').Split(new[] {'/'}, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) throw new PortalException(PortalErrorCode.InvalidParams, "Block path is empty.");
            bool qualified = parts.Length > 1;
            if (parts.Length > 1 && (string.Equals(parts[0], groupName(root), StringComparison.OrdinalIgnoreCase)
                || string.Equals(parts[0], "Program blocks", StringComparison.OrdinalIgnoreCase) || parts[0] == "程序块"))
                parts = parts.Skip(1).ToArray();
            if (qualified)
            {
                var group = root;
                foreach (var segment in parts.Take(parts.Length - 1))
                {
                    var matches = children(group).Where(g => string.Equals(groupName(g), segment, StringComparison.OrdinalIgnoreCase)).Take(2).ToList();
                    if (matches.Count == 0) return null;
                    if (matches.Count > 1) throw new PortalException(PortalErrorCode.InvalidParams, "Ambiguous block group: " + segment);
                    group = matches[0];
                }
                return select(blocks(group), parts[parts.Length - 1]);
            }
            // Bare names search every user group. Selection must reject duplicate names.
            var all = new List<TBlock>();
            var pending = new Stack<TGroup>(); pending.Push(root);
            int count = 0;
            while (pending.Count > 0)
            {
                if (++count > 100000) throw new PortalException(PortalErrorCode.OpennessError, "Block group traversal limit reached; no partial match returned.");
                var group = pending.Pop(); all.AddRange(blocks(group));
                foreach (var child in children(group)) pending.Push(child);
            }
            return select(all, parts[0]);
        }
    }
}
