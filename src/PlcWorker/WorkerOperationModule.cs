using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace TiaMcp.PlcWorker
{
    // Modules register only reviewed application methods. Neither caller-supplied
    // type names nor Siemens object handles participate in dispatch.
    internal sealed class WorkerOperationModule
    {
        private static readonly HashSet<string> ScopedOperations = new HashSet<string>(StringComparer.Ordinal) {
            "ListTags", "ListUserConstants", "ListSystemConstants", "ExportBlock", "ExportType", "ExportTagTable",
            "ImportBlocks", "ImportTypes", "ImportTagTables"
        };
        internal string Family { get; }
        internal IReadOnlyDictionary<string, MethodInfo> Methods { get; }

        internal WorkerOperationModule(string family, Type facade, IEnumerable<string> names)
        {
            Family = family;
            var allowed = new HashSet<string>(names, StringComparer.Ordinal);
            var methods = facade.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(m => allowed.Contains(m.Name)).ToDictionary(m => m.Name, StringComparer.Ordinal);
            if (methods.Count != allowed.Count)
                throw new InvalidOperationException("Worker operation module does not match the compiled facade: " + family);
            // Keep legacy entry signatures and wire names; only these reviewed operations bind the extended scope arguments.
            foreach (var name in allowed.Where(ScopedOperations.Contains))
            {
                var scoped = facade.GetMethod(name + "Scoped", BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                if (scoped == null) continue;
                var original = methods[name];
                var parameters = original.GetParameters();
                var extended = scoped.GetParameters();
                if (original.ReturnType != scoped.ReturnType || extended.Length < parameters.Length
                    || !parameters.Select(p => (p.Name, p.ParameterType, p.DefaultValue))
                        .SequenceEqual(extended.Take(parameters.Length).Select(p => (p.Name, p.ParameterType, p.DefaultValue)))
                    || extended.Skip(parameters.Length).Any(p => !p.IsOptional))
                    throw new InvalidOperationException("Scoped worker operation changed its legacy contract: " + name);
                methods[name] = scoped;
            }
            Methods = methods;
        }

        internal static Dictionary<string, MethodInfo> Register(IEnumerable<WorkerOperationModule> modules)
        {
            var result = new Dictionary<string, MethodInfo>(StringComparer.Ordinal);
            foreach (var module in modules)
                foreach (var method in module.Methods)
                    result.Add(module.Family.Length == 0 ? method.Key : module.Family + "." + method.Key, method.Value);
            return result;
        }
    }
}
