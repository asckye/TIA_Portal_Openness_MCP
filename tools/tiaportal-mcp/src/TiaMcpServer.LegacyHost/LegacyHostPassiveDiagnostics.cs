using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;
using TiaMcp.Versioning;
using TiaMcp.PlcWorker;

namespace TiaMcp.LegacyHost;

// Pure managed contract inspection. Deliberately no worker, OS, settings, file or native API dependency.
internal static class LegacyHostPassiveDiagnostics
{
    internal const string Contract = "legacy-host-passive-diagnostics-v1";
    internal const int MaxTools = 256;
    internal static JsonObject Inspect(string releaseKey, bool nativeSessionConfigured, IReadOnlyList<McpServerTool> tools)
    {
        var release = TiaVersionCatalog.Get(releaseKey);
        if (tools.Count > MaxTools) throw new InvalidOperationException();
        var names = tools.Select(t => t.ProtocolTool.Name).ToArray();
        bool unique = names.Distinct(StringComparer.Ordinal).Count() == names.Length;
        bool schemas = tools.All(t => ObjectSchema(t.ProtocolTool.InputSchema));
        bool mappings = FoundationTools.Definitions.Where(d => FoundationTools.Available(d, releaseKey)).All(d => names.Contains(d.Name, StringComparer.Ordinal) && WorkerOperations.Names.Contains(d.Operation));
        var roster = new JsonArray();
        foreach (var tool in tools.OrderBy(t => t.ProtocolTool.Name, StringComparer.Ordinal))
        {
            var name = tool.ProtocolTool.Name;
            if (name.Length > 128) throw new InvalidOperationException();
            var definition = FoundationTools.Definitions.SingleOrDefault(d => d.Name == name);
            roster.Add(new JsonObject { ["name"] = name, ["execution"] = definition == null ? "host-only" : "worker-protocol", ["wiredOperation"] = definition?.Operation, ["objectSchemaContractValid"] = ObjectSchema(tool.ProtocolTool.InputSchema) });
        }
        var probes = new JsonObject();
        foreach (var key in new[] { "installedTia", "installedPublicApi", "sdkCompatibility", "groupMembership", "permission", "connectReadiness", "workerAvailability", "connectionState", "portalProcesses", "projectState", "automationContext", "nativeAcceptance" }) probes[key] = "not-probed";
        return new JsonObject {
            ["contract"] = Contract, ["scope"] = "managed host registration and structural schema checks only",
            ["upstreamResponseCompatible"] = false, ["nativeCertified"] = false,
            ["selectedRelease"] = new JsonObject { ["key"] = release.Key, ["displayName"] = release.DisplayName, ["catalogState"] = release.SupportState, ["catalogMeaning"] = "Build-target metadata only; not installed or accepted capability." },
            ["host"] = new JsonObject { ["profile"] = "plc-foundation", ["productionAccepted"] = false, ["nativeSessionConfigured"] = nativeSessionConfigured, ["nativeCallsDisabledByDefault"] = false, ["nativeCallsDisabledByConfiguration"] = !nativeSessionConfigured, ["nativeGateMeaning"] = "Configuration only; this diagnostic never invokes the worker even when enabled." },
            ["checks"] = new JsonObject { ["uniqueRegisteredNames"] = unique, ["objectSchemaContracts"] = schemas, ["foundationOperationsInSourceAllowlist"] = mappings, ["passed"] = unique && schemas && mappings, ["meaning"] = "Structural checks only; not complete JSON Schema validation, tool execution, PLC semantics or native readiness." },
            ["registeredToolCount"] = tools.Count, ["registeredTools"] = roster, ["probes"] = probes,
            ["sideEffects"] = new JsonObject { ["workerInvoked"] = false, ["tiaLaunchedOrAttached"] = false, ["groupInspectedOrRepaired"] = false, ["persistentSettingsChanged"] = false },
            ["recommendedNextTool"] = "RunCapabilitySelfTest"
        };
    }
    // Deliberately bounded structural contract, not a JSON Schema evaluator.
    private static bool ObjectSchema(JsonElement schema)
    {
        if (schema.ValueKind != JsonValueKind.Object || !schema.TryGetProperty("type", out var type) || type.GetString() != "object" || !schema.TryGetProperty("properties", out var props) || props.ValueKind != JsonValueKind.Object || !schema.TryGetProperty("additionalProperties", out var extra) || extra.ValueKind != JsonValueKind.False) return false;
        if (!schema.TryGetProperty("required", out var required)) return true;
        return required.ValueKind == JsonValueKind.Array && required.EnumerateArray().All(x => x.ValueKind == JsonValueKind.String && props.TryGetProperty(x.GetString()!, out _));
    }
}

internal static class LegacyHostToolRegistry
{
    internal static IReadOnlyList<McpServerTool> Create(IFoundationWorker worker, string releaseKey, bool nativeSessionConfigured)
    {
        TiaVersionCatalog.Get(releaseKey);
        var tools = FoundationTools.Create(worker, releaseKey).Concat(OfflineXmlTools.Create()).Concat(OfflineCompositionTools.Create()).Concat(OfflineBlockCompositionTools.Create()).Concat(OfflineSymbolManifestTools.Create()).Concat(OfflineLadderTools.Create()).ToList();
        tools.Add(new ImportOrderTool());
        tools.AddRange(LegacyHostPassiveDiagnosticTools.Create(releaseKey, nativeSessionConfigured, () => tools));
        return tools.AsReadOnly();
    }
}
