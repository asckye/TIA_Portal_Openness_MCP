using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Security;
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
    private readonly McpServerTool inner;
    private readonly Tool tool;
    private readonly string release;
    private readonly string[] required;
    private readonly Func<string?> defaultDirectory;
    internal IReadOnlyList<string> ImplementationRequired => required;

    internal CanonicalParameterTool(McpServerTool inner, string release, Func<string?>? defaultDirectory = null)
    {
        this.inner = inner;
        this.release = release;
        this.defaultDirectory = defaultDirectory ?? (() => EngineHostConfiguration.CurrentOrNull?.GetExportDirectory(prepare: true));
        required = inner.ProtocolTool.InputSchema.TryGetProperty("required", out var fields)
            ? fields.EnumerateArray().Select(field => field.GetString()!).ToArray() : Array.Empty<string>();
        tool = Describe(inner.ProtocolTool);
    }

    internal static Tool Describe(Tool source)
    {
        var schema = CanonicalToolParameters.Schema(source.Name, source.InputSchema);
        return new Tool {
            Name = source.Name, Title = source.Title, Description = DescribeAlternatives(source.Name, source.Description, schema),
            InputSchema = schema, OutputSchema = source.OutputSchema, Annotations = source.Annotations, Meta = source.Meta
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
        if (CanonicalToolParameters.Aliases(source.Name).Count == 0 && !CanonicalToolParameters.HasStringDevice(source.Name)) return source;
        var tool = Describe(source.Tool);
        var aliases = CanonicalToolParameters.Aliases(source.Name);
        var parameters = source.Parameters.Select(parameter => aliases.Any(alias => alias.Legacy == parameter.Name)
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
        return new ToolDescriptor(source.Name, DescribeAlternatives(source.Name, source.RawDescription, tool.InputSchema),
            tool, source.Classification, signature,
            parameters, source.DryRun, source.CandidateFamily, source.Execution) { ImplementationRequired = source.ImplementationRequired };
    }

    public override Tool ProtocolTool => tool;

    internal static ToolArguments ApplyDefaults(string name, ToolArguments arguments)
        => ApplyDefaults(name, arguments, out _, prepareDirectory: false);

    internal static ToolArguments ApplyDefaults(string name, ToolArguments arguments, out Error? error, bool prepareDirectory = true)
    {
        error = null;
        var context = EngineHostConfiguration.CurrentOrNull;
        if (CanonicalToolParameters.Aliases(name).Count == 0 || context?.Catalog?.Find(name) is not { } descriptor) return arguments;
        var input = JsonNode.Parse(arguments.Json.GetRawText())!.AsObject();
        error = PrepareDefault(name, input, descriptor.ImplementationRequired,
            () => context.GetExportDirectory(prepareDirectory), out var effective);
        return new ToolArguments(JsonSerializer.SerializeToElement(effective));
    }

    private static Error? PrepareDefault(string name, JsonObject input, IEnumerable<string> required,
        Func<string?> directory, out JsonObject effective)
    {
        effective = input;
        if (!CanonicalToolParameters.NeedsDefault(name, input, required)) return null;
        try
        {
            string? target = directory();
            if (target != null) { effective = CanonicalToolParameters.WithDefaults(name, input, required, target); return null; }
            string parameter = CanonicalToolParameters.Aliases(name)
                .First(alias => alias.Canonical.StartsWith("export", StringComparison.Ordinal)).Canonical;
            return new Error("No default export directory is available. Supply " + parameter + " explicitly.",
                new InvalidArgumentDetails("/" + parameter, Array.Empty<string>()));
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or SecurityException)
        {
            return new Error("Could not create the default export directory. Supply an explicit export target or repair the output root.",
                new IoFailedDetails("create-default-export-directory", EngineHostConfiguration.CurrentOrNull?.ExportDirectory));
        }
    }

    internal static CallToolResult Reject(string release, string name, Error error)
    {
        var meta = new Meta(DateTimeOffset.UtcNow, release, name, Meta.Correlate(null), Outcome.RejectedBeforeOperation,
            Execution.NotStarted, false, BehaviorPolicy.NotApplicable, Completeness.None, null, Array.Empty<Warning>());
        var body = JsonNode.Parse(V4Json.Serialize(Envelope.Create<object?>(null, error, meta)))!;
        return new CallToolResult { IsError = true, StructuredContent = body,
            Content = new[] { new TextContentBlock { Text = body.ToJsonString() } } };
    }

    internal static async ValueTask<CallToolResult> InvokeWithDefaults(McpServerTool inner, RequestContext<CallToolRequestParams> request,
        CancellationToken cancellationToken)
    {
        var original = request.Params;
        if (original == null || CanonicalToolParameters.Aliases(original.Name).Count == 0)
            return await inner.InvokeAsync(request, cancellationToken).ConfigureAwait(false);
        var supplied = new ToolArguments(JsonSerializer.SerializeToElement(original.Arguments ?? new Dictionary<string, JsonElement>()));
        var effective = ApplyDefaults(original.Name, supplied, out var error);
        if (error != null) return Reject(EngineHostConfiguration.ReleaseKey, original.Name, error);
        request.Params = new CallToolRequestParams { Name = original.Name, Meta = original.Meta, ProgressToken = original.ProgressToken,
            Arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(effective.Json.GetRawText()) };
        try { return await inner.InvokeAsync(request, cancellationToken).ConfigureAwait(false); }
        finally { request.Params = original; }
    }

    public override async ValueTask<CallToolResult> InvokeAsync(RequestContext<CallToolRequestParams> request,
        CancellationToken cancellationToken = default)
    {
        var input = JsonSerializer.SerializeToNode(request.Params?.Arguments ?? new Dictionary<string, JsonElement>())!.AsObject();
        var error = PrepareDefault(tool.Name, input, required, defaultDirectory, out var effective);
        error ??= CanonicalToolParameters.ValidateAliases(tool.Name, effective, required, requireOutput: true)
            ?? new InputSchema(tool.InputSchema).Validate(JsonSerializer.SerializeToElement(effective), "arguments");
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
        if (ReferenceEquals(body, decorated)) return result;
        return new CallToolResult { IsError = result.IsError, StructuredContent = decorated,
            Content = new[] { new TextContentBlock { Text = decorated!.ToJsonString() } } };
    }
}
