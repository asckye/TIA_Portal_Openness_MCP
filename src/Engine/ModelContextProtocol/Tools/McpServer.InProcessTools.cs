using ModelContextProtocol.Server;
using ModelContextProtocol.Protocol;
using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using TiaMcp.Logic.V4;
using TiaMcp.Logic.V4.Inputs;

namespace TiaMcpServer.ModelContextProtocol
{
    public static partial class McpServer
    {
        internal static Dictionary<string, MethodInfo> AllToolMethods(bool includeUnavailable = false)
        {
            return AvailableToolMethods(((ToolCatalog)CatalogView).Methods, includeUnavailable);
        }

        private static Dictionary<string, MethodInfo> AvailableToolMethods(IEnumerable<KeyValuePair<string, MethodInfo>> methods, bool includeUnavailable)
            => methods.Where(kv => includeUnavailable || VersionToolProblem(kv.Key).Length == 0)
                .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);

        internal static string ToolDescription(MethodInfo m)
        {
            var d = m.GetCustomAttribute<DescriptionAttribute>();
            return (d == null ? "" : d.Description) + TiaOpenness.Shared.ToolUsageCatalog.Hint(m.GetCustomAttribute<McpServerToolAttribute>()?.Name ?? m.Name);
        }

        /// <summary>Renders one tool's signature the way the model needs to call it through CallTool.</summary>
        // Keep infrastructure parameters out of bridge signatures and argument binding.
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

        internal static Error? BindV4Call(string name, ToolArguments arguments, out MethodInfo? method, out object?[]? call)
        {
            method = null; call = null;
            if (string.IsNullOrWhiteSpace(name)) return InvalidInput("name");
            if (!AllToolMethods(includeUnavailable: true).TryGetValue(name, out method)
                || name != (method.GetCustomAttribute<McpServerToolAttribute>()?.Name ?? method.Name))
                return new Error("Tool is not registered in this release.", new ToolNotFoundDetails(name));
            var argumentNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in arguments.Json.EnumerateObject())
                if (!argumentNames.Add(property.Name)) return InvalidInput("arguments");
            name = method.GetCustomAttribute<McpServerToolAttribute>()?.Name ?? method.Name;
            var resolved = method;
            string version = VersionCallProblem(name, key => arguments.Json.TryGetProperty(key, out var v) ? v.ToString()
                : resolved.GetParameters().FirstOrDefault(p => p.Name == key)?.DefaultValue?.ToString());
            if (version.Length != 0)
                return new Error(version, new UnsupportedCapabilityDetails(ReleaseKey, name, null));
            var schema = ToolInputSchema(name, method);
            var error = ValidateReflectedArguments(method, arguments.Json, schema);
            if (error != null) return error;
            try { CallerInputFiles.ValidateNativeFile(name, JsonNode.Parse(arguments.Json.GetRawText())!.AsObject(), checkOutput: false); }
            catch (TiaMcp.Adapters.Contracts.AdapterPreconditionException refusal)
            { return new Error(refusal.Message, new InvalidArgumentDetails(refusal.ParamName ?? "arguments", Array.Empty<string>())); }
            var parameters = method.GetParameters();
            call = new object?[parameters.Length];
            for (int i = 0; i < parameters.Length; i++)
            {
                var p = parameters[i];
                if (IsInfrastructureParameter(p.ParameterType)) continue;
                if (!arguments.Json.TryGetProperty(p.Name!, out var value))
                {
                    if (!p.HasDefaultValue) return InvalidInput(p.Name!);
                    call[i] = p.DefaultValue; continue;
                }
                try { call[i] = JsonSerializer.Deserialize(value.GetRawText(), p.ParameterType, V4BindingJson); }
                catch (Exception ex) when (ex is JsonException || ex is ArgumentException || ex is InvalidOperationException || ex is OverflowException)
                { return InvalidInput(p.Name!); }
            }
            return null;
        }

        /// <summary>The parameter specs of a tool method (infrastructure parameters skipped), the vocabulary PreflightLogic / SchemaHintsLogic / ToolExamples share.</summary>
        internal static List<PreflightLogic.ParameterSpec> SpecsOf(MethodInfo method)
        {
            var specs = new List<PreflightLogic.ParameterSpec>();
            foreach (var p in method.GetParameters())
            {
                if (IsInfrastructureParameter(p.ParameterType)) continue;
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

        /// <summary>Write and online-write tools by their typed classification; descriptions never decide.</summary>
        internal static bool IsWriteTool(MethodInfo method) => IsWrite(ClassificationOf(method));
        internal static object? InvokeToolMethod(MethodInfo method, object?[] call)
        {
            string id = Guid.NewGuid().ToString("N");
            RecordBridgeEvent(id, method.Name, "BEFORE");
            IDisposable? observation = null;
            StartCallProjection(id, method, call, ref observation);
            bool issued = false;
            string toolName = method.GetCustomAttribute<McpServerToolAttribute>()?.Name ?? method.Name;
            var input = new JsonObject();
            var specs = method.GetParameters();
            for (int i = 0; i < specs.Length; i++)
                if (specs[i].ParameterType == typeof(string)) input[specs[i].Name!] = (string?)call[i];
            using var export = CallerInputFiles.ObserveExport(toolName, input);
            try
            {
                var parameters = method.GetParameters();
                var problem = VersionCallProblem(toolName, key =>
                {
                    int index = Array.FindIndex(parameters, p => string.Equals(p.Name, key, StringComparison.OrdinalIgnoreCase));
                    return index < 0 ? null : call[index]?.ToString();
                });
                if (problem.Length != 0) throw new NotSupportedException(problem);
                CallerInputFiles.ValidateNativeFile(toolName, input);
                ValidateRuntimeBinding(method);
                object? target = method.IsStatic ? null : EngineServices.Get(method.DeclaringType!);
                issued = true;
                object? result = method.Invoke(target, call);
                if (result is Task task)
                {
                    bool waited = false;
                    WaitToolTask(task, ref waited);
                    if (!waited) task.GetAwaiter().GetResult();
                    var resultProperty = task.GetType().GetProperty("Result");
                    result = resultProperty != null && resultProperty.PropertyType.Name != "VoidTaskResult" ? resultProperty.GetValue(task) : null;
                }
                if (result is CallToolResult returned) result = ExportFailureResult(toolName, returned, export);
                RecordBridgeEvent(id, method.Name, "RETURNED");
                EndCallProjection(observation, result);
                return result;
            }
            catch (Exception ex)
            {
                RecordBridgeEvent(id, method.Name, "THREW");
                CallerInputFiles.RecordExportFailure(ex);
                var result = ExportFailureResult(toolName, TargetFailure(toolName, ex, issued), export);
                EndCallProjection(observation, result);
                return result;
            }
            finally { observation?.Dispose(); }
        }

        internal static CallToolResult ExportFailureResult(string tool, CallToolResult result, CallerInputFiles.ExportObservation? export)
        {
            if (export?.NativeMessage == null || !InvocationJournal.NativeCallIssued || result.StructuredContent is not JsonObject original
                || (string?)original["meta"]?["outcome"] is not ("unknown" or "failed")) return result;
            if ((bool?)original["error"]?["details"]?["evidence"]?["nativeResultReturned"] == true) return result;
            var data = original["data"]?.DeepClone() as JsonObject ?? new JsonObject();
            data["nativeMessage"] = export.NativeMessage;
            if (!export.NoFileWritten)
            {
                data["mayHaveChanged"] = true;
                return V4Result(tool, data, new Error("The export failed and its target may have changed. " + export.NativeMessage,
                    new OutcomeUnknownDetails("export-file", new Dictionary<string, JsonElement>())), Outcome.Unknown, Execution.Unknown, Completeness.Unknown, current: true);
            }
            data["mayHaveChanged"] = false; data["mayHaveWrittenFiles"] = false;
            if (data["evidence"] is JsonObject evidence) { evidence["mayHaveChanged"] = false; evidence["mayHaveWrittenFiles"] = false; }
            return V4Result(tool, data, new Error("The native export failed without writing its target. " + export.NativeMessage,
                new NativeOperationFailedDetails(null, export.NativeMessage, new Dictionary<string, JsonElement>())), Outcome.Failed, Execution.Completed, Completeness.Complete, current: true);
        }

        static partial void RecordBridgeEvent(string id, string name, string phase);
        static partial void StartCallProjection(string id, MethodInfo method, object?[] arguments, ref IDisposable? observation);
        static partial void EndCallProjection(IDisposable? observation, object? result);
        static partial void RecordCallRejection(string name, ToolArguments arguments, CallToolResult result);
        static partial void ValidateRuntimeBinding(MethodInfo method);
        static partial void WaitToolTask(Task task, ref bool waited);

        private static readonly ConcurrentDictionary<(string Name, MethodInfo Method), JsonElement> InputSchemas =
            new ConcurrentDictionary<(string, MethodInfo), JsonElement>();
        internal static JsonElement ToolInputSchema(string name, MethodInfo method)
            => InputSchemas.GetOrAdd((name, method), key => CreateInputSchema(key.Name, key.Method));

        private static JsonElement CreateInputSchema(string name, MethodInfo method)
        {
            var raw = ToolCatalog.CreateTool(method).ProtocolTool.InputSchema;
            var schema = (JsonObject)JsonNode.Parse(raw.GetRawText())!;
            var candidate = method.GetCustomAttribute<BehaviorCandidateAttribute>();
            SchemaHintsLogic.Augment(schema, SpecsOf(method), candidate?.Family == "P6-FALLBACK" ? BehaviorCapabilities.CandidateExample(ReleaseKey, name) : null);
            PreserveTypedSchemas(schema, raw, method);
            InfrastructureSchema(name, schema);
            schema["additionalProperties"] = false;
            return JsonSerializer.SerializeToElement(InlineSchema(schema));
        }

        private static void PreserveTypedSchemas(JsonObject schema, JsonElement original, MethodInfo method)
        {
            foreach (var parameter in method.GetParameters())
                if (parameter.ParameterType != typeof(ToolArguments?) && TypedToolInput.For(parameter.ParameterType) != null)
                    schema["properties"]![parameter.Name!] = JsonNode.Parse(original.GetProperty("properties").GetProperty(parameter.Name!).GetRawText());
        }

        private static Error? ValidateReflectedArguments(MethodInfo method, JsonElement arguments, JsonElement schema)
        {
            // Check family budgets before binding so converter exceptions cannot erase
            // LIMIT_EXCEEDED or expose supplied values in SDK diagnostics.
            var family = ValidateNestedTypedArguments(method, arguments) ?? ValidateReflectedTypedFamilies(method, arguments);
            if (family != null) return family;
            var shapeError = new InputSchema(schema).Validate(arguments, "arguments");
            if (shapeError != null) return shapeError;
            foreach (var parameter in method.GetParameters())
            {
                if (IsInfrastructureParameter(parameter.ParameterType) || !arguments.TryGetProperty(parameter.Name!, out var value)) continue;
                try { JsonSerializer.Deserialize(value.GetRawText(), parameter.ParameterType, V4BindingJson); }
                catch (InputRejection rejection) { return rejection.ToError(parameter.Name!); }
                catch (Exception ex) when (ex is JsonException || ex is ArgumentException || ex is InvalidOperationException || ex is OverflowException || ex is NotSupportedException)
                { return InvalidInput(parameter.Name!); }
            }
            return null;
        }

        internal static Error? ValidateReflectedTypedFamilies(MethodInfo method, JsonElement arguments)
        {
            foreach (var parameter in method.GetParameters())
            {
                var contract = TypedToolInput.For(parameter.ParameterType);
                if (contract == null || parameter.ParameterType == typeof(ToolArguments?) || !arguments.TryGetProperty(parameter.Name!, out var value)) continue;
                var error = contract.Validate(value, parameter.Name!);
                if (error != null) return error;
            }
            return null;
        }

        private static Error? ValidateNestedTypedArguments(MethodInfo method, JsonElement arguments)
        {
            Error? Target(JsonElement call)
            {
                if (call.ValueKind != JsonValueKind.Object || !call.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String
                    || !call.TryGetProperty("arguments", out var input) || input.ValueKind != JsonValueKind.Object) return null;
                if (!AllToolMethods(true).TryGetValue(name.GetString()!, out var target)) return null;
                return ValidateReflectedTypedFamilies(target, input);
            }
            string tool = method.GetCustomAttribute<McpServerToolAttribute>()?.Name ?? method.Name;
            if (tool == "CallTool" || tool == "PreviewToolCall") return Target(arguments);
            if ((tool == "RunReadOnlyToolBatch" || tool == "PreviewToolBatch") && arguments.TryGetProperty("operations", out var operations)
                && operations.ValueKind == JsonValueKind.Array)
                foreach (var operation in operations.EnumerateArray())
                {
                    var error = Target(operation);
                    if (error != null) return error;
                }
            return null;
        }

        internal static bool IsBatchOrchestration(MethodInfo method) => method.Name.Contains("Batch")
            || method.Name == "CallTool" || method.GetCustomAttribute<McpServerToolAttribute>()?.Name == "RunToolTransaction"
            || method.GetCustomAttribute<McpServerToolAttribute>()?.Name == "GetPlcCrossReferences";

        static partial void InitializeToolCatalog(ref IToolCatalogView? catalog, ref IToolInvoker? invoker)
        { catalog = ToolCatalog.Engine; invoker = new InProcessToolInvoker(ToolCatalog.Engine); }

        internal static void ConfigureToolBridge(ToolCatalog catalog, Func<bool> isLiteProfile, ISet<string> liteToolNames)
            => ConfigureToolBridge(catalog, new InProcessToolInvoker(catalog), isLiteProfile, liteToolNames);

        internal static Error? ValidateV4Arguments(MethodInfo method, JsonElement arguments, JsonElement schema)
            => ValidateReflectedArguments(method, arguments, schema);

        // The protocol description carries the worked example from ToolExamples (one table, validated at build
        // time), so the model sees a correct call next to every listed tool without duplicating examples in attributes.
        internal static McpServerTool WithSchemaHints(McpServerTool tool, string name, MethodInfo method)
        {
            var hinted = WithSchemaHints(tool, name, SpecsOf(method), (schema, original) => PreserveTypedSchemas(schema, original, method));
            CreateInProcessTool(name, method, out var descriptor);
            return new InfrastructureInputTool(hinted, descriptor, (arguments, schema) => ValidateReflectedArguments(method, arguments, schema));
        }

        internal static McpServerTool CreateTool(string name, MethodInfo method)
            => CreateInProcessTool(name, method, out _);

        internal static McpServerTool CreateInProcessTool(string name, MethodInfo method, out ToolDescriptor descriptor)
        {
            var tool = DeclaredToolMetadata.Create(name, method, request =>
                request.Services?.GetService(method.DeclaringType!) ?? EngineServices.Get(method.DeclaringType!), out descriptor);
            return new InfrastructureInputTool(tool, descriptor, (arguments, schema) => ValidateReflectedArguments(method, arguments, schema));
        }

    }
}
