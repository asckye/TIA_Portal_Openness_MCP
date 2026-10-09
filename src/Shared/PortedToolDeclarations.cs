using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using ModelContextProtocol.Server;
using TiaMcp.Adapters.Contracts;

namespace TiaMcpServer.ModelContextProtocol
{
    internal static class PortedToolDeclarations
    {
        private static readonly IReadOnlyDictionary<string, Type> Types = new Dictionary<string, Type>(StringComparer.Ordinal) {
            ["F19"] = typeof(AddressesTools)
        };

        internal static IEnumerable<MethodInfo> Methods(string release)
        {
            foreach (var family in PortedFamilies.All.Where(f => f.Available(release)))
            {
                var methods = Types[family.Name].GetMethods().Where(m => m.GetCustomAttribute<McpServerToolAttribute>() != null).ToArray();
                var names = methods.Select(m => m.GetCustomAttribute<McpServerToolAttribute>()!.Name!).ToArray();
                if (names.Length != family.Tools.Length || names.Distinct(StringComparer.Ordinal).Count() != names.Length
                    || names.Except(family.Tools, StringComparer.Ordinal).Any())
                    throw new InvalidOperationException("Ported declaration roster mismatch: " + family.Name);
                foreach (var method in methods) yield return method;
            }
        }
    }
}
