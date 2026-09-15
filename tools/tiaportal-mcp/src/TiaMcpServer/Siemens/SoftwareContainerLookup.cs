using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace TiaMcpServer.Siemens
{
    // Traverses only the hardware/group tree. Never reads block groups or HMI contents.
    internal static class SoftwareContainerLookup
    {
        internal static T? FindUnique<T>(IEnumerable<object> roots,
            Func<object, IEnumerable<object>> children, Func<object, string?> alias,
            Func<object, T?> container, Func<T, string?> softwareName, string name,
            int maxNodes = 100000) where T : class
        {
            var stack = new Stack<(object Node, bool AliasMatched)>();
            var visited = new HashSet<object>(IdentityComparer.Instance);
            var matches = new HashSet<object>(IdentityComparer.Instance);
            T? found = null;
            int count = 0;
            void Push(IEnumerable<object> nodes, bool inherited)
            {
                foreach (var node in nodes)
                {
                    if (++count > maxNodes)
                        throw new PortalException(PortalErrorCode.OpennessError,
                            "Software lookup node limit reached; uniqueness was not established.");
                    if (node != null) stack.Push((node, inherited));
                }
            }
            Push(roots, false);
            while (stack.Count > 0)
            {
                var current = stack.Pop();
                if (!visited.Add(current.Node)) continue;
                var matched = current.AliasMatched || string.Equals(alias(current.Node), name, StringComparison.OrdinalIgnoreCase);
                var candidate = container(current.Node);
                if (candidate != null)
                {
                    var actualName = softwareName(candidate);
                    if (actualName != null && (matched || string.Equals(actualName, name, StringComparison.OrdinalIgnoreCase))
                        && matches.Add(candidate))
                    {
                        if (found != null)
                            throw new PortalException(PortalErrorCode.InvalidParams,
                                $"Ambiguous software name '{name}': multiple software containers match. Use a group-qualified device/software path.");
                        found = candidate;
                    }
                }
                Push(children(current.Node), matched);
            }
            return found;
        }

        private sealed class IdentityComparer : IEqualityComparer<object>
        {
            internal static readonly IdentityComparer Instance = new IdentityComparer();
            public new bool Equals(object? x, object? y) => ReferenceEquals(x, y);
            public int GetHashCode(object obj) => RuntimeHelpers.GetHashCode(obj);
        }
    }
}
