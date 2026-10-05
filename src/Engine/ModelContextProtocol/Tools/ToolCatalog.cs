using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using TiaMcp.Logic.V4.Inputs;
using TiaMcp.Logic.V4;

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
        /// <summary>Write and online-write tools by their typed classification (name inference only for unregistered names).</summary>
        internal static bool IsWrite(string name)
        {
            string operation = ToolTaxonomy.OperationOf(name, null).Operation;
            return operation == "WRITE" || operation == "ONLINE-WRITE";
        }

        internal ToolCatalog(IEnumerable<Type> types)
        {
            if (types == null) throw new ArgumentNullException(nameof(types));
            var methods = new Dictionary<string, MethodInfo>(StringComparer.OrdinalIgnoreCase);
            var allTypes = types.ToArray();
            var candidates = allTypes.SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance))
                .Where(m => m.GetCustomAttribute<BehaviorCandidateAttribute>() != null).ToArray();
            foreach (var type in allTypes)
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
                    methods.Add(name, BehaviorCapabilities.SelectMethod(name, method, candidates, McpServer.ReleaseKey));
                }
            }
            Methods = methods.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToList().AsReadOnly();
        }

        internal static McpServerTool CreateTool(MethodInfo method, McpServerToolCreateOptions? options = null)
        {
            // Exclude custom input contracts from SDK inference before it serializes
            // optional null defaults. Their null-rejecting converters are unchanged.
            var inputs = method.GetParameters().Select(p => (Parameter: p, Input: TypedToolInput.For(p.ParameterType)))
                .Where(p => p.Input != null && p.Parameter.ParameterType != typeof(ToolArguments?)).ToArray();
            if (inputs.Length > 0)
            {
                options = options == null ? new McpServerToolCreateOptions() : new McpServerToolCreateOptions
                {
                    Name = options.Name, Title = options.Title, Description = options.Description,
                    Destructive = options.Destructive, Idempotent = options.Idempotent, OpenWorld = options.OpenWorld,
                    ReadOnly = options.ReadOnly, UseStructuredContent = options.UseStructuredContent,
                    SerializerOptions = options.SerializerOptions, SchemaCreateOptions = options.SchemaCreateOptions, Services = options.Services,
                };
                var schemaOptions = options.SchemaCreateOptions ?? AIJsonSchemaCreateOptions.Default;
                options.SchemaCreateOptions = new AIJsonSchemaCreateOptions
                {
                    IncludeParameter = p => !inputs.Any(input => input.Parameter == p) && (schemaOptions.IncludeParameter?.Invoke(p) ?? true),
                    IncludeSchemaKeyword = schemaOptions.IncludeSchemaKeyword,
                    TransformOptions = schemaOptions.TransformOptions,
                    TransformSchemaNode = schemaOptions.TransformSchemaNode,
                };
            }
            McpServerTool tool;
            if (method.IsStatic) tool = options == null
                ? McpServerTool.Create(method)
                : McpServerTool.Create(method, options: options);
            else
            {
                Func<RequestContext<CallToolRequestParams>, object> target = request =>
                    request.Services?.GetService(method.DeclaringType!) ?? EngineServices.Get(method.DeclaringType!);
                tool = McpServerTool.Create(method, target, options);
            }
            var protocol = tool.ProtocolTool;
            var schema = JsonNode.Parse(protocol.InputSchema.GetRawText())!.AsObject();
            var properties = schema["properties"]!.AsObject();
            var required = schema["required"] as JsonArray ?? new JsonArray();
            foreach (var input in inputs)
            {
                string name = input.Parameter.Name!;
                var property = JsonNode.Parse(input.Input!.Schema.GetRawText())!;
                var description = input.Parameter.GetCustomAttribute<DescriptionAttribute>();
                if (description != null) property["description"] = description.Description;
                // Type-local recursive references become local to the tool root.
                RebaseReferences(property, "#/properties/" + name.Replace("~", "~0").Replace("/", "~1"));
                properties[name] = property;
                if (!input.Parameter.HasDefaultValue) required.Add(name);
            }
            if (required.Count > 0) schema["required"] = required;
            McpServer.RemoveV4NullDefaults(schema);
            var candidate = method.GetCustomAttribute<BehaviorCandidateAttribute>();
            if (candidate != null)
                schema = JsonNode.Parse(((IBehaviorCandidateContract)Activator.CreateInstance(candidate.Contract)!).InputSchema.GetRawText())!.AsObject();
            return new SchemaHintedTool(tool, new Tool
            {
                Name = protocol.Name, Title = protocol.Title, Description = protocol.Description,
                InputSchema = JsonSerializer.SerializeToElement(McpServer.InlineSchema(schema)),
                OutputSchema = protocol.OutputSchema, Annotations = protocol.Annotations, Meta = protocol.Meta,
            });
        }

        private static void RebaseReferences(JsonNode node, string root)
        {
            if (node is JsonObject obj)
            {
                if (obj["$ref"] is JsonValue reference)
                {
                    string pointer = reference.GetValue<string>();
                    if (!pointer.StartsWith("#/", StringComparison.Ordinal)) throw new ArgumentException("Only local input references are supported.");
                    obj["$ref"] = root + pointer.Substring(1);
                }
                foreach (var child in obj.ToArray()) if (child.Value != null) RebaseReferences(child.Value, root);
            }
            else if (node is JsonArray array)
                foreach (var child in array) if (child != null) RebaseReferences(child, root);
        }
    }
}
