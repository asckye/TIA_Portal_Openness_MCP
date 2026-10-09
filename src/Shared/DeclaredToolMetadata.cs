using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
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
    internal static class DeclaredToolMetadata
    {
        internal static McpServerTool CreateToolCore(MethodInfo method, McpServerToolCreateOptions? options, Func<RequestContext<CallToolRequestParams>, object> target)
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
                tool = McpServerTool.Create(method, target, options);
            }
            var protocol = tool.ProtocolTool;
            var schema = JsonNode.Parse(protocol.InputSchema.GetRawText())!.AsObject();
            var properties = schema["properties"]!.AsObject();
            var required = schema["required"] as JsonArray ?? new JsonArray();
            if (protocol.Name == "StageImportFiles") properties["files"] = TiaMcp.Logic.ModelContextProtocol.ImportStagingSchema.Files();
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
        private static string RenderSignature(string name, MethodInfo m)
        {
            var parts = new List<string>();
            foreach (var p in m.GetParameters())
            {
                string t = FriendlyTypeName(p.ParameterType);
                // Optional params are what a model most often gets wrong, so show the actual
                // default rather than a bare "?".
                if (!p.HasDefaultValue) { parts.Add(p.Name + ": " + t); continue; }
                string def;
                if (p.DefaultValue == null) def = "null";
                else if (p.DefaultValue is bool) def = ((bool)p.DefaultValue) ? "true" : "false";
                else if (p.DefaultValue is string) def = "\"" + p.DefaultValue + "\"";
                else def = Convert.ToString(p.DefaultValue, System.Globalization.CultureInfo.InvariantCulture) ?? "null";
                parts.Add(p.Name + "?: " + t + " = " + def);
            }
            return name + "(" + string.Join(", ", parts) + ")";
        }

        private static string FriendlyTypeName(Type t)
        {
            var u = Nullable.GetUnderlyingType(t) ?? t;
            if (u == typeof(string)) return "string";
            if (u == typeof(bool)) return "boolean";
            if (u == typeof(int) || u == typeof(long)) return "integer";
            if (u == typeof(double) || u == typeof(float) || u == typeof(decimal)) return "number";
            if (u.IsArray) return FriendlyTypeName(u.GetElementType()!) + "[]";
            return u.Name;
        }

        internal static List<PreflightLogic.ParameterSpec> SpecsOf(MethodInfo method)
        {
            var specs = new List<PreflightLogic.ParameterSpec>();
            foreach (var p in method.GetParameters())
            {
                if (McpServer.IsInfrastructureParameter(p.ParameterType)) continue;
                string? def = null;
                if (p.HasDefaultValue)
                    def = p.DefaultValue == null ? "null" : p.DefaultValue is bool b ? (b ? "true" : "false") : p.DefaultValue is string s ? "\"" + s + "\"" : Convert.ToString(p.DefaultValue, System.Globalization.CultureInfo.InvariantCulture);
                var d = p.GetCustomAttribute<DescriptionAttribute>();
                // A parameter without its own [Description] gets the roster-wide vocabulary text (flagged Synthesized).
                string? text = d?.Description;
                bool synthesized = false;
                if (string.IsNullOrEmpty(text)) { text = ParameterVocabulary.Describe(p.Name!); synthesized = text != null; }
                string tool = method.GetCustomAttribute<McpServerToolAttribute>()?.Name ?? method.Name;
                specs.Add(new PreflightLogic.ParameterSpec(p.Name!, FriendlyTypeName(p.ParameterType), !p.HasDefaultValue, def, text ?? "", synthesized,
                    ToolMetadata.Parameter(tool, p.Name!)?.AllowedValues));
            }
            return specs;
        }

        private static ToolMetadata.Classification? ClassificationOf(MethodInfo method)
            => method.GetCustomAttribute<ToolClassificationAttribute>()?.Value
               ?? ToolMetadata.Find(method.GetCustomAttribute<McpServerToolAttribute>()?.Name ?? method.Name);

        private static void PreserveTypedSchemas(JsonObject schema, JsonElement original, MethodInfo method)
        {
            foreach (var parameter in method.GetParameters())
                if (parameter.ParameterType != typeof(ToolArguments?) && TypedToolInput.For(parameter.ParameterType) != null)
                    schema["properties"]![parameter.Name!] = JsonNode.Parse(original.GetProperty("properties").GetProperty(parameter.Name!).GetRawText());
        }

        internal static McpServerTool Create(string name, MethodInfo method, Func<RequestContext<CallToolRequestParams>, object> target, out ToolDescriptor descriptor)
        {
            var attribute = method.GetCustomAttribute<System.ComponentModel.DescriptionAttribute>();
            var description = attribute?.Description ?? "";
            var decorated = method.GetCustomAttribute<TiaMcp.Logic.V4.BehaviorCandidateAttribute>() != null
                ? description + TiaOpenness.Shared.ToolUsageCatalog.Hint(name)
                : ToolExamples.Decorate(name, description) + TiaOpenness.Shared.ToolUsageCatalog.Hint(name);
            var tool = ReferenceEquals(decorated, description) || decorated == description
                ? CreateToolCore(method, null, target)
                : CreateToolCore(method, new McpServerToolCreateOptions { Name = name, Description = decorated }, target);
            // Enum / default / examples hints in the input schema (McpServer.CallDiscipline.cs).
            var hinted = McpServer.WithSchemaHints(tool, name, SpecsOf(method), (schema, original) => PreserveTypedSchemas(schema, original, method));
            var specs = SpecsOf(method);
            var parameters = method.GetParameters().Where(p => !McpServer.IsInfrastructureParameter(p.ParameterType)).Select((p, i) =>
                new ToolParameterDescriptor(p.Name!, specs[i].Kind, p.ParameterType.FullName!, specs[i].Required, specs[i].DefaultText,
                    p.DefaultValue?.ToString(), specs[i].Description, specs[i].Synthesized, specs[i].AllowedValues)).ToArray();
            var classification = ClassificationOf(method);
            var dryRun = method.GetParameters().FirstOrDefault(p => p.Name == "dryRun" && p.ParameterType == typeof(bool));
            descriptor = new ToolDescriptor(name, description, hinted.ProtocolTool, classification == null ? null :
                new ToolDescriptorClassification(classification.Layer, classification.Domain, classification.Operation, classification.BatchRead, classification.BatchWrite),
                RenderSignature(name, method), parameters, new ToolDryRunDescriptor(dryRun != null,
                    dryRun?.DefaultValue is not bool value || value), method.GetCustomAttribute<BehaviorCandidateAttribute>()?.Family,
                ToolExecution.Table.TryGetValue(name, out var execution) ? execution : "worker");
            return hinted;
        }

    }
    public static partial class McpServer
    {
        internal static bool IsInfrastructureParameter(Type type)
            => type.Name == "IMcpServer" || (type.IsGenericType && type.GetGenericTypeDefinition().Name.StartsWith("RequestContext", StringComparison.Ordinal))
               || (type.Namespace != null && type.Namespace.StartsWith("ModelContextProtocol", StringComparison.Ordinal));

    }
}
