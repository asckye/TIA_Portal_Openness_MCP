using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace TiaMcpServer.ModelContextProtocol
{
    internal sealed class ToolCatalog
    {
        private static readonly Lazy<ToolCatalog> engine = new Lazy<ToolCatalog>(() =>
            new ToolCatalog(LoadableTypes(typeof(ToolCatalog).Assembly)
                .Where(type => type.GetCustomAttribute<McpServerToolTypeAttribute>() != null)));

        // A type whose optional dependency is absent must not take every tool down: tool types only
        // reference the engine and its shipped libraries, so they are among the types that do load.
        private static IEnumerable<Type> LoadableTypes(Assembly assembly)
        {
            try { return assembly.GetTypes(); }
            catch (ReflectionTypeLoadException ex)
            {
                foreach (var error in ex.LoaderExceptions.Where(error => error != null))
                    Console.Error.WriteLine("ToolCatalog: skipped a type that could not load: " + error!.Message);
                return ex.Types.Where(type => type != null)!;
            }
        }

        internal static ToolCatalog Engine => engine.Value;
        internal IReadOnlyList<KeyValuePair<string, MethodInfo>> Methods { get; }

        internal ToolCatalog(IEnumerable<Type> types)
        {
            if (types == null) throw new ArgumentNullException(nameof(types));
            var methods = new Dictionary<string, MethodInfo>(StringComparer.OrdinalIgnoreCase);
            foreach (var type in types)
            {
                foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance))
                {
                    var attribute = method.GetCustomAttribute<McpServerToolAttribute>();
                    if (attribute == null) continue;
                    var name = attribute.Name ?? method.Name;
                    if (methods.TryGetValue(name, out var previous))
                        throw new InvalidOperationException("Duplicate MCP tool name '" + name + "': "
                            + previous.DeclaringType!.FullName + "." + previous.Name + " and "
                            + method.DeclaringType!.FullName + "." + method.Name + ". Tool names must be unique (OrdinalIgnoreCase).");
                    methods.Add(name, method);
                }
            }
            Methods = methods.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToList().AsReadOnly();
        }

        internal static McpServerTool CreateTool(MethodInfo method, McpServerToolCreateOptions? options = null)
        {
            if (method.IsStatic) return options == null
                ? McpServerTool.Create(method)
                : McpServerTool.Create(method, options: options);
            Func<RequestContext<CallToolRequestParams>, object> target = request =>
                request.Services?.GetService(method.DeclaringType!) ?? EngineServices.Get(method.DeclaringType!);
            return McpServerTool.Create(method, target, options);
        }
    }
}
