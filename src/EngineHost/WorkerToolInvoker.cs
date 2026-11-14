using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.Logic.V4;
using TiaMcp.Logic.V4.Inputs;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcp.FoundationHost
{
    internal sealed class WorkerToolInvoker : IToolInvoker
    {
        private readonly IToolCatalogView catalog;
        private readonly IEngineWorker worker;
        private readonly IReadOnlyDictionary<string, MethodInfo> local;
        private readonly IReadOnlyDictionary<string, McpServerTool> shared;
        private readonly System.Collections.Concurrent.ConcurrentDictionary<Type, object> targets = new();
        internal void SetLocalTarget(Type type, object value) => targets[type] = value;
        internal object LocalTarget(Type type) => targets.GetOrAdd(type, target => Activator.CreateInstance(target)!);
        internal WorkerToolInvoker(IToolCatalogView catalog, IEngineWorker worker, ImportStagingHostLifetime stagingLifetime,
            IReadOnlyList<McpServerTool>? sharedTools = null)
        {
            this.catalog = catalog; this.worker = worker;
            shared = (sharedTools ?? Array.Empty<McpServerTool>()).ToDictionary(t => t.ProtocolTool.Name, StringComparer.Ordinal);
            targets[typeof(ImportStagingTools)] = new ImportStagingTools(stagingLifetime);
            local = new[] { typeof(McpServer), typeof(XmlBuilderTools), typeof(OfflineSuiteTools), typeof(TemplateTools),
                typeof(ExportTools), typeof(ImportStagingTools), typeof(ImportOrderTools), typeof(ToolUsageTools),
                typeof(EcosystemTools), typeof(V21EcosystemTools), typeof(EngineeringDiagnosticsTools), typeof(OfflineAnalysisTools),
                typeof(HostMetaTools), typeof(PlcOfflineTools), typeof(HmiOfflineTools), typeof(PlcDocumentationTools), typeof(QualityAuditTools),
                    typeof(GitWorkflowTools),
                typeof(RuntimeTools), typeof(RuntimeChannelTools), typeof(PlcSimAdvancedTools) }
                .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance))
                .Where(m => m.GetCustomAttribute<McpServerToolAttribute>() != null)
                .ToDictionary(m => m.GetCustomAttribute<McpServerToolAttribute>()!.Name ?? m.Name, AsyncImplementation, StringComparer.Ordinal);
            foreach (var tool in catalog.All.Values.Where(t => t.Execution == "host"))
                if (!local.ContainsKey(tool.Name)) throw new InvalidOperationException("Missing host tool implementation: " + tool.Name);
        }

        private static MethodInfo AsyncImplementation(MethodInfo method)
        {
            var dispatch = method.GetCustomAttribute<AsyncDispatchAttribute>();
            if (dispatch == null) return method;
            var parameters = method.GetParameters();
            var implementation = method.DeclaringType!.GetMethod(dispatch.MethodName,
                BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance, null,
                parameters.Select(p => p.ParameterType).ToArray(), null);
            if (implementation == null || implementation.IsStatic != method.IsStatic
                || implementation.ReturnType != typeof(Task<CallToolResult>)
                || implementation.GetCustomAttribute<McpServerToolAttribute>() != null
                || !implementation.GetParameters().Select(p => (p.Name, p.HasDefaultValue, p.DefaultValue))
                    .SequenceEqual(parameters.Select(p => (p.Name, p.HasDefaultValue, p.DefaultValue))))
                throw new InvalidOperationException("Invalid asynchronous host tool implementation: " + method.Name);
            return implementation;
        }

        public Error? Bind(string name, ToolArguments arguments, out IBoundToolCall? call)
        {
            call = null;
            if (string.IsNullOrWhiteSpace(name)) return McpServer.InvalidInput("name");
            var tool = catalog.Find(name, true);
            if (tool == null || name != tool.Name) return new Error("Tool is not registered in this release.", new ToolNotFoundDetails(name));
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in arguments.Json.EnumerateObject())
                if (!seen.Add(property.Name)) return McpServer.InvalidInput("", "object with unique fields");
            string problem = ReleaseCatalogData.Releases[McpServer.ReleaseKey].WrapPortedTools && tool.Execution == "foundation"
                && (!TiaMcp.Adapters.Contracts.PortedFamilies.Contains(name)
                || TiaMcp.Adapters.Contracts.PortedFamilies.Available(McpServer.ReleaseKey, name)) ? "" : McpServer
                    .VersionCallProblem(name, key => arguments.Json.TryGetProperty(key, out var value) ? value.ToString()
                : tool.Parameters.FirstOrDefault(p => p.Name == key)?.DefaultText);
            if (problem.Length == 0 && !catalog.All.ContainsKey(name)) problem = "Tool is not available in this release.";
            if (problem.Length != 0) return new Error(problem, new UnsupportedCapabilityDetails(McpServer.ReleaseKey, name, null));
            var error = ValidateArguments(tool, arguments.Json, tool.Tool.InputSchema);
            if (error == null) call = new BoundCall(this, tool, arguments);
            return error;
        }

        private static Type? ParameterType(string name) => typeof(TypedToolInput).Assembly.GetType(name) ?? Type.GetType(name);
        public Error? ValidateArguments(ToolDescriptor tool, JsonElement arguments, JsonElement schema, bool typedFamiliesOnly = false)
        {
            var navigation = TiaMcpServer.Siemens.Services.UnifiedPortService.ValidateNavigation(tool.Name, schema, arguments);
            if (navigation != null) return navigation;
            Error? Families(ToolDescriptor target, JsonElement input)
            {
                foreach (var parameter in target.Parameters)
                {
                    var type = ParameterType(parameter.ClrType);
                    if (type == null || type == typeof(ToolArguments) || !input.TryGetProperty(parameter.Name, out var value)) continue;
                    var error = TypedToolInput.For(type)?.Validate(value, parameter.Name);
                    if (error != null) return new InputSchema(target.Tool.InputSchema).Restrict(error, input);
                }
                return null;
            }
            Error? Target(JsonElement input)
            {
                if (input.ValueKind != JsonValueKind.Object || !input.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String
                    || !input.TryGetProperty("arguments", out var args) || args.ValueKind != JsonValueKind.Object) return null;
                var target = catalog.Find(name.GetString()!, true);
                return target == null ? null : Families(target, args);
            }
            Error? family = null;
            if (tool.Name is "RunReadOnlyToolBatch" or "PreviewToolBatch" && arguments.TryGetProperty("operations", out var calls)
                && calls.ValueKind == JsonValueKind.Array)
            {
                int index = 0;
                foreach (var input in calls.EnumerateArray())
                {
                    family = Target(input);
                    if (family != null)
                    {
                        family = InvalidArgumentDetails.Prefix(family,
                            "/operations/" + index.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/arguments");
                        break;
                    }
                    index++;
                }
            }
            family ??= Families(tool, arguments);
            if (family != null || typedFamiliesOnly) return family;
            bool canonical = CanonicalToolParameters.Aliases(tool.Name).Count != 0 || CanonicalToolParameters.HasStringDevice(tool.Name);
            var shape = (canonical ? CanonicalToolParameters.ValidateAliases(tool.Name,
                JsonNode.Parse(arguments.GetRawText())!.AsObject(), tool.ImplementationRequired) : null)
                ?? new InputSchema(schema).Validate(arguments, "arguments");
            if (shape != null) return shape;
            if (canonical)
                arguments = JsonSerializer.SerializeToElement(CanonicalToolParameters.LegacyArguments(tool.Name,
                    JsonNode.Parse(arguments.GetRawText())!.AsObject()));
            foreach (var parameter in tool.Parameters)
            {
                var type = ParameterType(parameter.ClrType);
                if (type == null || !arguments.TryGetProperty(parameter.Name, out var value)) continue;
                try {
                    JsonSerializer
                        .Deserialize(value.GetRawText(), type, new JsonSerializerOptions(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = false });
                }
                catch (InputRejectionException rejection) { return rejection.ToError(parameter.Name); }
                catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException or OverflowException or NotSupportedException)
                { return McpServer.InvalidInput(parameter.Name); }
            }
            return null;
        }

        public ToolInvocationResult Invoke(string name, ToolArguments arguments, bool preview)
            => InvokeAsync(name, arguments, preview).GetAwaiter().GetResult();

        public Task<ToolInvocationResult> InvokeAsync(string name, ToolArguments arguments, bool preview)
        {
            var error = Bind(name, arguments, out var call);
            return error == null ? call!.InvokeAsync(preview) : Task.FromResult(new ToolInvocationResult(McpServer.V4Reject(name, error), false));
        }

        public McpServerTool CreateTool(ToolDescriptor tool) => new InfrastructureInputTool(new CatalogTool(this, tool), tool);

        private async Task<ToolInvocationResult> DispatchAsync(ToolDescriptor tool, ToolArguments arguments, bool preview)
        {
            if (tool.Execution == "foundation"
                || CanonicalToolParameters.Aliases(tool.Name).Count == 0 && !CanonicalToolParameters.HasStringDevice(tool.Name))
                return RestrictArguments(tool, arguments, await DispatchCoreAsync(tool, arguments, preview).ConfigureAwait(false));
            var input = JsonNode.Parse(arguments.Json.GetRawText())!.AsObject();
            var prepared = CanonicalParameterTool.ApplyDefaults(tool.Name, arguments, out var error);
            if (error != null) return new ToolInvocationResult(CanonicalParameterTool.Reject(McpServer.ReleaseKey, tool.Name, error), false);
            var effective = JsonNode.Parse(prepared.Json.GetRawText())!.AsObject();
            var adapted = new ToolArguments(JsonSerializer.SerializeToElement(CanonicalToolParameters.LegacyArguments(tool.Name, effective)));
            var result = await DispatchCoreAsync(tool, adapted, preview).ConfigureAwait(false);
            return RestrictArguments(tool, arguments,
                new ToolInvocationResult(CanonicalParameterTool.Decorate(tool.Name, input, result.Result), result.NativeCallIssued));
        }

        private static ToolInvocationResult RestrictArguments(ToolDescriptor tool, ToolArguments arguments, ToolInvocationResult result)
        {
            // Orchestration has already validated each child against its own catalog schema.
            if (tool.Name is "CallTool" or "PreviewToolCall" or "RunReadOnlyToolBatch" or "PreviewToolBatch") return result;
            if (result.Result.StructuredContent is not JsonObject body || (string?)body["error"]?["code"] != "INVALID_ARGUMENT") return result;
            var error = V4Json.Deserialize<Error>(body["error"]!.ToJsonString());
            body["error"] = JsonNode.Parse(V4Json.Serialize(new InputSchema(tool.Tool.InputSchema)
                .Restrict(error, arguments.Json, body["data"] as JsonObject)));
            return new ToolInvocationResult(new CallToolResult { IsError = result.Result.IsError, StructuredContent = body,
                Content = new[] { new TextContentBlock { Text = body.ToJsonString() } } }, result.NativeCallIssued);
        }

        private async Task<ToolInvocationResult> DispatchCoreAsync(ToolDescriptor tool, ToolArguments arguments, bool preview)
        {
            if (tool.Execution == "foundation")
            {
                var request = new RequestContext<CallToolRequestParams>(McpServer.CurrentCallServer!) {
                    Params = new CallToolRequestParams {
                        Name = tool.Name, Arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(arguments.Json.GetRawText()) }
                };
                return new ToolInvocationResult(await shared[tool.Name].InvokeAsync(request, McpServer.WorkerDispatchCancellation).ConfigureAwait(false),
                        false);
            }
            if (tool.Execution == "worker")
            {
                using var digestScope = WriteDigest.Enter();
                McpServer.PrepareWriteDigest(tool.Name, JsonNode.Parse(arguments.Json.GetRawText())!.AsObject());
                var token = McpServer.WorkerDispatchCancellation;
                using var progress = (worker as IEngineWorkerProgress)?.UseProgress(preview ? null : McpServer.WorkerProgressRelay());
                using var lane = await TiaMcpServer.Dispatch.ToolDispatchLanes.Acquire(tool.Name, token).ConfigureAwait(false);
                TiaMcpServer.Dispatch.ToolDispatchLanes.Activate(lane);
                string id = TiaOpenness.Shared.AuditInvocation.CurrentRequestId ?? InvocationJournal.CorrelationId;
                string phase = "THREW";
                InvocationJournal.Write(id, "worker:" + tool.Name, "BEFORE");
                try
                {
                    var unsettledBeforeSave = tool.Name is "SaveProject" or "SaveProjectCopy" ? worker.Unsettled.ToJson() : null;
                    var reply = await worker.Invoke(id, tool.Name, JsonNode.Parse(arguments.Json.GetRawText())!.AsObject(), preview, token)
                        .ConfigureAwait(false);
                    if (reply.NativeCallIssued) InvocationJournal.NativeCallStarted();
                    phase = "RETURNED";
                    return new ToolInvocationResult(McpServer.ObserveSessionOutcome(tool.Name, reply.Result,
                        HostBehavior.ApprovalWrite(tool.Name, JsonNode.Parse(arguments.Json.GetRawText())!.AsObject(),
                            tool.Classification?.Operation is "WRITE" or "ONLINE-WRITE", tool.DryRun.Present, tool.DryRun.Default),
                        arguments.Json.GetRawText(), preview, unsettledBeforeSave), reply.NativeCallIssued);
                }
                finally { InvocationJournal.Write(id, "worker:" + tool.Name, phase); }
            }
            var method = local[tool.Name];
            var values = method.GetParameters().Select(p => arguments.Json.TryGetProperty(p.Name!, out var value)
                ? JsonSerializer.Deserialize(value.GetRawText(), p.ParameterType, new JsonSerializerOptions(JsonSerializerDefaults.Web))
                : p.DefaultValue).ToArray();
            using var scope = preview ? McpServer.BeginReadOnlyApprovalPreview() : null;
            try
            {
                object? result = method.Invoke(method.IsStatic ? null : LocalTarget(method.DeclaringType!), values);
                if (result is Task task) { await task.ConfigureAwait(false); result = task.GetType().GetProperty("Result")?.GetValue(task); }
                return new ToolInvocationResult(McpServer.ToolResult(result), false);
            }
            catch (Exception error) { return new ToolInvocationResult(McpServer.TargetFailure(tool.Name, error, false), false); }
        }

        private sealed class BoundCall(WorkerToolInvoker owner, ToolDescriptor tool, ToolArguments arguments) : IBoundToolCall
        {
            public ToolInvocationResult Invoke(bool preview) => InvokeAsync(preview).GetAwaiter().GetResult();
            public Task<ToolInvocationResult> InvokeAsync(bool preview) => owner.DispatchAsync(tool, arguments, preview);
        }
        private sealed class CatalogTool(WorkerToolInvoker owner, ToolDescriptor tool) : McpServerTool
        {
            public override Tool ProtocolTool => tool.Tool;
            public override async ValueTask<CallToolResult> InvokeAsync(RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken =
                    default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return (await owner.DispatchAsync(tool, new ToolArguments(JsonSerializer.SerializeToElement(
                    request.Params?.Arguments ?? new Dictionary<string, JsonElement>())), McpServer.IsReadOnlyApprovalPreview).ConfigureAwait(false)).Result;
            }
        }
    }
}
