using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Security;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.Logic.V4;
using TiaMcp.Logic.V4.Inputs;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcp.FoundationHost;

internal sealed class CanonicalParameterTool : McpServerTool
{
    private sealed record PreparedRequest(object Owner, string Tool);
    private static readonly ConditionalWeakTable<object, PreparedRequest> PreparedRequests = new();
    private readonly McpServerTool inner;
    private readonly Tool tool;
    private readonly string release;
    private readonly string[] required;
    private readonly Func<string?> defaultDirectory;
    private readonly IReadOnlyDictionary<string, string> presets;
    private readonly string[] presetNames;
    private readonly JsonElement implementationSchema;
    internal IReadOnlyList<string> ImplementationRequired => required;
    internal JsonElement ImplementationSchema => implementationSchema;

    internal CanonicalParameterTool(McpServerTool inner, string release, Func<string?>? defaultDirectory = null,
        IReadOnlyDictionary<string, string>? presets = null)
    {
        this.inner = inner;
        this.release = release;
        this.defaultDirectory = defaultDirectory ?? (() => EngineHostConfiguration.CurrentOrNull?.GetExportDirectory(prepare: true));
        this.presets = presets ?? EngineHostConfiguration.CurrentOrNull?.Presets ?? ParameterPresets.Empty;
        implementationSchema = CanonicalToolParameters.Schema(inner.ProtocolTool.Name, inner.ProtocolTool.InputSchema);
        presetNames = ParameterPresets.Names(inner.ProtocolTool.Name, implementationSchema, this.presets);
        required = inner.ProtocolTool.InputSchema.TryGetProperty("required", out var fields)
            ? fields.EnumerateArray().Select(field => field.GetString()!).ToArray() : Array.Empty<string>();
        tool = Describe(inner.ProtocolTool, this.presets);
    }

    internal static Tool Describe(Tool source, IReadOnlyDictionary<string, string>? presets = null)
    {
        var schema = CanonicalToolParameters.Schema(source.Name, source.InputSchema);
        presets ??= EngineHostConfiguration.CurrentOrNull?.Presets ?? ParameterPresets.Empty;
        var names = ParameterPresets.Names(source.Name, schema, presets);
        string? description = DescribeAlternatives(source.Name, source.Description, schema);
        if (names.Length != 0)
        {
            string suffix = ParameterPresets.Description(source.Name, names);
            if (description?.EndsWith(suffix, StringComparison.Ordinal) != true) description += " " + suffix;
        }
        return new Tool {
            Name = source.Name, Title = source.Title, Description = description,
            InputSchema = ParameterPresets.Schema(source.Name, schema, presets), OutputSchema = source.OutputSchema,
            Annotations = source.Annotations, Meta = source.Meta
        };
    }

    private static string DescribeAlternatives(string name, string? description, JsonElement schema)
    {
        string suffix = string.Join(" ", CanonicalToolParameters.Aliases(name).GroupBy(alias => alias.Legacy)
            .Where(group => schema.GetProperty("properties").TryGetProperty(group.Key, out _))
            .Select(group => string.Join(" and ", new[] { group.Key }.Concat(group.Select(alias => alias.Canonical)))
                + " are mutually exclusive parameter alternatives."));
        description ??= "";
        return suffix.Length == 0 || description.EndsWith(suffix, StringComparison.Ordinal) ? description : description + " " + suffix;
    }

    internal static ToolDescriptor Describe(ToolDescriptor source)
    {
        var presets = EngineHostConfiguration.CurrentOrNull?.Presets ?? ParameterPresets.Empty;
        if (CanonicalToolParameters.Aliases(source.Name).Count == 0 && !CanonicalToolParameters.HasStringDevice(source.Name)
            && ParameterPresets.Names(source.Name, source.ImplementationSchema, presets).Length == 0) return source;
        var tool = Describe(source.Tool);
        var aliases = CanonicalToolParameters.Aliases(source.Name);
        var parameters = source.Parameters.Where(parameter => tool.InputSchema.GetProperty("properties").TryGetProperty(parameter.Name, out _))
            .Select(parameter => aliases.Any(alias => alias.Legacy == parameter.Name)
            ? new ToolParameterDescriptor(parameter.Name, parameter.FriendlyType, parameter.ClrType, false, parameter.DefaultJson,
                parameter.DefaultText, tool.InputSchema.GetProperty("properties").GetProperty(parameter.Name).GetProperty("description").GetString()!,
                false, parameter.AllowedValues)
            : parameter.Name == "devicePath" && CanonicalToolParameters.HasStringDevice(source.Name)
            ? new ToolParameterDescriptor(parameter.Name, "string[] | string", parameter.ClrType, parameter.Required, null, null,
                tool.InputSchema.GetProperty("properties").GetProperty("devicePath").GetProperty("description").GetString()!,
                false, parameter.AllowedValues) : parameter).ToList();
        foreach (var alias in CanonicalToolParameters.Aliases(source.Name))
        {
            var legacy = source.Parameters.FirstOrDefault(parameter => parameter.Name == alias.Legacy);
            if (legacy == null || parameters.Any(parameter => parameter.Name == alias.Canonical)) continue;
            parameters.Add(new ToolParameterDescriptor(alias.Canonical, legacy.FriendlyType, legacy.ClrType, false, null, null,
                tool.InputSchema.GetProperty("properties").GetProperty(alias.Canonical).GetProperty("description").GetString()!,
                false, legacy.AllowedValues));
        }
        string signature = source.Name + "(" + string.Join(", ", parameters.Select(parameter =>
            parameter.Name + (parameter.Required ? ": " : "?: ") + parameter.FriendlyType)) + ")";
        string description = DescribeAlternatives(source.Name, source.RawDescription, tool.InputSchema);
        var names = ParameterPresets.Names(source.Name, source.ImplementationSchema, presets);
        string suffix = ParameterPresets.Description(source.Name, names);
        if (names.Length != 0 && !description.EndsWith(suffix, StringComparison.Ordinal)) description += " " + suffix;
        return new ToolDescriptor(source.Name, description,
            tool, source.Classification, signature,
            parameters, source.DryRun, source.CandidateFamily, source.Execution) {
                ImplementationRequired = source.ImplementationRequired, ImplementationSchema = source.ImplementationSchema };
    }

    public override Tool ProtocolTool => tool;

    internal static ToolArguments ApplyDefaults(string name, ToolArguments arguments)
        => ApplyDefaults(name, arguments, out _, prepareDirectory: false);

    internal static ToolArguments ApplyDefaults(string name, ToolArguments arguments, out Error? error, bool prepareDirectory = true,
        bool applyExportDefaults = true)
    {
        error = null;
        var context = EngineHostConfiguration.CurrentOrNull;
        if (context?.Catalog?.Find(name) is not { } descriptor) return arguments;
        var names = PresetNames(descriptor);
        if (names.Length == 0 && CanonicalToolParameters.Aliases(name).Count == 0 && !CanonicalToolParameters.HasCanonicalOutput(name)) return arguments;
        if (names.Length != 0)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in arguments.Json.EnumerateObject())
                if (!seen.Add(property.Name)) { error = McpServer.InvalidInput("", "object with unique fields"); return arguments; }
        }
        var input = JsonNode.Parse(arguments.Json.GetRawText())!.AsObject();
        bool prepared = ReferenceEquals(arguments.PresetOwner, context) && arguments.PresetTool == name;
        if (!prepared)
        {
            error = ParameterPresets.Refusal(name, input, names, context.Presets);
            if (error != null) return arguments;
            foreach (string parameter in names.Where(parameter => ParameterPresets.PlcTarget(name, parameter, input)))
                input[parameter] = ParameterPresets.Value(context.Presets, parameter);
        }
        var effective = input;
        if (applyExportDefaults)
            error = PrepareDefault(name, input, descriptor.ImplementationRequired,
                () => context.GetExportDirectory(prepareDirectory), out effective);
        return new ToolArguments(JsonSerializer.SerializeToElement(effective), context, name);
    }

    internal static string[] PresetNames(ToolDescriptor descriptor)
        => ParameterPresets.Names(descriptor.Name, descriptor.ImplementationSchema,
            EngineHostConfiguration.CurrentOrNull?.Presets ?? ParameterPresets.Empty);

    internal static ToolArguments PublicArguments(ToolDescriptor descriptor, ToolArguments arguments)
    {
        var context = EngineHostConfiguration.CurrentOrNull;
        if (context == null || !ReferenceEquals(arguments.PresetOwner, context) || arguments.PresetTool != descriptor.Name) return arguments;
        var names = PresetNames(descriptor);
        if (names.Length == 0) return arguments;
        var input = JsonNode.Parse(arguments.Json.GetRawText())!.AsObject();
        foreach (string name in names)
            if (!descriptor.Tool.InputSchema.GetProperty("properties").TryGetProperty(name, out _)) input.Remove(name);
        return new ToolArguments(JsonSerializer.SerializeToElement(input));
    }

    internal static ToolArguments RequestArguments(RequestContext<CallToolRequestParams> request)
    {
        var arguments = request.Params?.Arguments;
        var json = JsonSerializer.SerializeToElement(arguments ?? new Dictionary<string, JsonElement>());
        return arguments != null && PreparedRequests.TryGetValue(arguments, out var prepared)
            && ReferenceEquals(prepared.Owner, EngineHostConfiguration.CurrentOrNull) && prepared.Tool == request.Params!.Name
                ? new ToolArguments(json, prepared.Owner, prepared.Tool) : new ToolArguments(json);
    }

    internal static void PreserveRequestArguments(CallToolRequestParams? source, CallToolRequestParams target)
    {
        if (source?.Arguments != null && target.Arguments != null && source.Name == target.Name
            && PreparedRequests.TryGetValue(source.Arguments, out var prepared)
            && ReferenceEquals(prepared.Owner, EngineHostConfiguration.CurrentOrNull) && prepared.Tool == target.Name)
            PreparedRequests.Add(target.Arguments, prepared);
    }

    internal static JsonElement PublicRequestArguments(ToolDescriptor descriptor, RequestContext<CallToolRequestParams> request, JsonElement json)
        => PresetNames(descriptor).Length != 0 && request.Params?.Arguments is { } arguments && PreparedRequests.TryGetValue(arguments, out _)
            ? PublicArguments(descriptor, RequestArguments(request)).Json : json;

    private static Error? PrepareDefault(string name, JsonObject input, IEnumerable<string> required,
        Func<string?> directory, out JsonObject effective)
    {
        effective = input;
        if (!CanonicalToolParameters.NeedsDefault(name, input, required)) return null;
        try
        {
            string? target = directory();
            if (target != null) { effective = CanonicalToolParameters.WithDefaults(name, input, required, target); return null; }
            string parameter = CanonicalToolParameters.HasCanonicalOutput(name) ? "exportFile" : CanonicalToolParameters.Aliases(name)
                .First(alias => alias.Canonical.StartsWith("export", StringComparison.Ordinal)).Canonical;
            return new Error("No default export directory is available. Supply " + parameter + " explicitly.",
                new InvalidArgumentDetails("/" + parameter, Array.Empty<string>()));
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or SecurityException)
        {
            return new Error("Could not create the default export directory. Supply an explicit export target or repair the output root.",
                new IoFailedDetails("create-default-export-directory", EngineHostConfiguration.CurrentOrNull?.ExportDirectory));
        }
        catch (ArgumentException failure) when (CanonicalToolParameters.HasCanonicalOutput(name))
        {
            return new Error(failure.Message, new InvalidArgumentDetails("/" + (failure.ParamName ?? "inputPath"), Array.Empty<string>()));
        }
    }

    internal static CallToolResult Reject(string release, string name, Error error)
    {
        var meta = new Meta(DateTimeOffset.UtcNow, release, name, Meta.Correlate(null), Outcome.RejectedBeforeOperation,
            Execution.NotStarted, BehaviorPolicy.NotApplicable, Completeness.None, null, Array.Empty<Warning>());
        var body = JsonNode.Parse(V4Json.Serialize(Envelope.Create<object?>(null, error, meta)))!;
        return new CallToolResult { IsError = true, StructuredContent = body,
            Content = new[] { new TextContentBlock { Text = body.ToJsonString() } } };
    }

    internal static async ValueTask<CallToolResult> InvokeWithDefaults(McpServerTool inner, RequestContext<CallToolRequestParams> request,
        CancellationToken cancellationToken)
    {
        var original = request.Params;
        if (original == null || inner is CanonicalParameterTool)
            return await inner.InvokeAsync(request, cancellationToken).ConfigureAwait(false);
        var context = EngineHostConfiguration.CurrentOrNull;
        var supplied = RequestArguments(request);
        var input = JsonNode.Parse(supplied.Json.GetRawText())!.AsObject();
        var reflectionError = ParameterPresets.UnpinnedReflectionTarget(original.Name, input, context?.Presets ?? ParameterPresets.Empty);
        if (reflectionError != null) return Reject(EngineHostConfiguration.ReleaseKey, original.Name, reflectionError);
        var projectError = PresetProjectGuard.Check(original.Name, input);
        if (projectError != null) return Reject(EngineHostConfiguration.ReleaseKey, original.Name, projectError);
        if (context?.Catalog?.Find(original.Name) is not { } descriptor
            || CanonicalToolParameters.Aliases(original.Name).Count == 0 && !CanonicalToolParameters.HasCanonicalOutput(original.Name)
                && PresetNames(descriptor).Length == 0)
            return await inner.InvokeAsync(request, cancellationToken).ConfigureAwait(false);
        var effective = ApplyDefaults(original.Name, supplied, out var error);
        if (error != null) return Reject(EngineHostConfiguration.ReleaseKey, original.Name, error);
        var effectiveArguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(effective.Json.GetRawText())!;
        PreparedRequests.Add(effectiveArguments, new PreparedRequest(context, original.Name));
        request.Params = new CallToolRequestParams { Name = original.Name, Meta = original.Meta, ProgressToken = original.ProgressToken,
            Arguments = effectiveArguments };
        try { return await inner.InvokeAsync(request, cancellationToken).ConfigureAwait(false); }
        finally { request.Params = original; }
    }

    public override async ValueTask<CallToolResult> InvokeAsync(RequestContext<CallToolRequestParams> request,
        CancellationToken cancellationToken = default)
    {
        var input = JsonSerializer.SerializeToNode(request.Params?.Arguments ?? new Dictionary<string, JsonElement>())!.AsObject();
        var error = ParameterPresets.Refusal(tool.Name, input, presetNames, presets);
        if (error != null) return Reject(release, tool.Name, error);
        foreach (string parameter in presetNames.Where(parameter => ParameterPresets.PlcTarget(tool.Name, parameter, input)))
            input[parameter] = ParameterPresets.Value(presets, parameter);
        error = PresetProjectGuard.Check(tool.Name, input);
        if (error != null) return Reject(release, tool.Name, error);
        error = PrepareDefault(tool.Name, input, required, defaultDirectory, out var effective);
        error ??= CanonicalToolParameters.ValidateAliases(tool.Name, effective, required, requireOutput: true)
            ?? new InputSchema(implementationSchema).Validate(JsonSerializer.SerializeToElement(effective), "arguments");
        if (error != null) return Decorate(tool.Name, input, Reject(release, tool.Name, error));
        var original = request.Params;
        request.Params = new CallToolRequestParams { Name = tool.Name, Meta = original?.Meta, ProgressToken = original?.ProgressToken,
            Arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                CanonicalToolParameters.LegacyArguments(tool.Name, effective).ToJsonString()) };
        try { return Decorate(tool.Name, input, await inner.InvokeAsync(request, cancellationToken).ConfigureAwait(false)); }
        finally { request.Params = original; }
    }

    internal static CallToolResult Decorate(string name, JsonObject input, CallToolResult result)
    {
        var body = result.StructuredContent ?? JsonNode.Parse((result.Content.FirstOrDefault() as TextContentBlock)?.Text ?? "null");
        var decorated = CanonicalToolParameters.Warn(name, input, body);
        if (decorated?["error"]?["details"] is JsonObject details)
            foreach (var alias in CanonicalToolParameters.Aliases(name)
                .Where(alias => !input.ContainsKey(alias.Legacy) && input.ContainsKey(alias.Canonical)))
            {
                string legacy = "/" + alias.Legacy, canonical = "/" + alias.Canonical;
                bool parameter = (string?)details["parameter"] == legacy;
                bool parameters = details["parameters"] is JsonArray fields && fields.Any(field => (string?)field == legacy);
                if (!parameter && !parameters) continue;
                if (ReferenceEquals(body, decorated))
                {
                    decorated = body!.DeepClone();
                    details = decorated["error"]!["details"]!.AsObject();
                }
                if (parameter) details["parameter"] = canonical;
                if (details["parameters"] is JsonArray pointers)
                    for (int index = 0; index < pointers.Count; index++)
                        if ((string?)pointers[index] == legacy) pointers[index] = canonical;
            }
        if (ReferenceEquals(body, decorated)) return result;
        return new CallToolResult { IsError = result.IsError, StructuredContent = decorated,
            Content = new[] { new TextContentBlock { Text = decorated!.ToJsonString() } } };
    }
}
