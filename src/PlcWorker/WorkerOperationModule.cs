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
