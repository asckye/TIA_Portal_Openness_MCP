using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;

namespace TiaMcpServer.Siemens
{
    // Traverses only the hardware/group tree. Never reads block groups or HMI contents.
    internal static class SoftwareContainerLookup
    {
        // PLC names are literal. Scan the complete hardware tree before certifying uniqueness;
        // a station or ancestor DeviceItem is an alias only when it owns one matching PLC.
        internal static T? FindPlc<T>(IEnumerable<object> roots,
            Func<object, IEnumerable<object>> children, Func<object, string?> alias,
            Func<object, T?> container, Func<T, string?> softwareName,
            Func<object, bool> isGroup, string name, Action<string> availablePaths, int maxNodes = 100000) where T : class
        {
            var token = (name ?? string.Empty).Trim();
            var stack = new Stack<(object Node, string[] Groups, string[] Hardware)>();
            var visited = new HashSet<object>(IdentityComparer.Instance);
            var candidates = new Dictionary<object, (T Value, HashSet<string> Aliases, string Path)>(IdentityComparer.Instance);
            int count = 0;
            void Push(IEnumerable<object> nodes, string[] groups, string[] hardware)
            {
                foreach (var node in nodes)
                {
                    if (++count > maxNodes)
                        throw new PortalException(PortalErrorCode.OpennessError,
                            "Software lookup node limit reached; uniqueness was not established.");
                    if (node != null) stack.Push((node, groups, hardware));
                }
            }
            Push(roots, Array.Empty<string>(), Array.Empty<string>());
            while (stack.Count > 0)
            {
                var current = stack.Pop();
                if (!visited.Add(current.Node)) continue;
                var nodeName = alias(current.Node);
                var groups = current.Groups;
                var hardware = current.Hardware;
                if (!string.IsNullOrEmpty(nodeName))
                {
                    if (isGroup(current.Node)) groups = groups.Concat(new[] { nodeName! }).ToArray();
                    else hardware = hardware.Concat(new[] { nodeName! }).ToArray();
                }
                var value = container(current.Node);
                if (value != null)
                {
                    var actual = softwareName(value);
                    if (!string.IsNullOrEmpty(actual))
                    {
                        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        void Add(IEnumerable<string> parts) => names.Add(string.Join("/", parts).Trim());
                        Add(new[] { actual! });
                        // Group-qualified CPU/software names and station aliases retain their
                        // structural meaning. A group by itself is never a PLC alias.
                        foreach (var item in hardware)
                        {
                            Add(new[] { item });
                            Add(groups.Concat(new[] { item }));
                            Add(new[] { item, actual! });
                            Add(groups.Concat(new[] { item, actual! }));
                        }
                        Add(groups.Concat(new[] { actual! }));
                        for (int i = 0; i < hardware.Length; i++)
                        {
                            var prefix = hardware.Take(i + 1).ToArray();
                            Add(prefix);
                            Add(groups.Concat(prefix));
                            Add(prefix.Concat(new[] { actual! }));
                            Add(groups.Concat(prefix).Concat(new[] { actual! }));
                            // Devices can contain a rack between the station and CPU.
                            foreach (var item in hardware.Skip(i + 1))
                            {
                                Add(prefix.Concat(new[] { item }));
                                Add(groups.Concat(prefix).Concat(new[] { item }));
                            }
                        }
                        var path = string.Join("/", groups.Concat(hardware).Concat(new[] { actual! }));
                        if (candidates.TryGetValue(value, out var existing)) existing.Aliases.UnionWith(names);
                        else candidates.Add(value, (value, names, path));
                    }
                }
                Push(children(current.Node), groups, hardware);
            }
            availablePaths(candidates.Count == 0 ? string.Empty : " Available PLC paths: "
                + string.Join(", ", candidates.Values.Select(c => c.Path).OrderBy(p => p, StringComparer.Ordinal)));
            // Preserve the existing omitted-name failure when there is no sole PLC.
            if (token.Length == 0) return candidates.Count == 1 ? candidates.Values.First().Value : null;
            var matches = candidates.Values.Where(c => c.Aliases.Contains(token)).ToArray();
            if (matches.Length > 1)
                throw new PortalException(PortalErrorCode.InvalidParams,
                    $"Ambiguous PLC software name '{name}': {string.Join(", ", matches.Select(c => c.Path).OrderBy(p => p, StringComparer.Ordinal))}. Use a group-qualified device/software path.");
            return matches.Length == 1 ? matches[0].Value : null;
        }

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
