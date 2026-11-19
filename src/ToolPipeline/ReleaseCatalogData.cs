using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using ModelContextProtocol.Server;
using TiaMcpServer.ModelContextProtocol;
using TiaOpenness.Shared;

namespace TiaMcp.FoundationHost;

// Presentation is selected here; FoundationCatalog never branches on a release key.
internal sealed record ReleaseCatalogData(string Instructions, bool HasLite, bool RetainUnavailable,
    bool UseDeclaredDiscovery, IReadOnlyList<string> ManifestSupplementaryReleases, StringComparer NameComparer,
    string DefaultExampleProfile, IReadOnlyDictionary<string, string> ExampleProfiles,
    IReadOnlyDictionary<string, string> ManifestReturnTypes)
{
    internal string ProfileName { get; init; } = "plc-foundation";
    internal bool RegisterDiscoveryHandlers { get; init; }
    internal bool CatalogCallDiscipline { get; init; }
    internal bool HasManifestCatalog { get; init; }
    internal bool SharedSession { get; init; } = true;
    internal bool ExtendedWorkerStatus { get; init; } = true;
    internal bool StartSupervisorOnRead { get; init; }
    internal bool NativeObservations { get; init; } = true;
    internal IReadOnlyList<string> ObservationSet { get; init; } = new[] {
        "environment", "group", "group.fix", "assemblies", "tool-names", "reflection", "reflection-deny",
        "session.GetState", "session.ListPortalProcessProjects", "session.GetProjectTree", "session.ConnectPortal",
        "session.ValidateAutomationContext" };

    // Direct V20/V21 ports publish declaration errors, check caller files at the host and page at the client text limit.
    // Earlier direct ports retain Foundation argument errors, worker-side file access and the worker channel size limit.
    private static readonly string[] DeclaredPortReleases = TiaMcp.Adapters.Contracts.ReleaseRange.Since("20");
    internal static bool DeclaredPortInputAndPaging(string release) => DeclaredPortReleases.Contains(release, StringComparer.Ordinal);

    private const string DiscoveryInstructions = ToolUsageCatalog.Instructions +
        " FindTools searches the complete release catalog, including tools omitted by the lite profile; CallTool invokes a discovered tool. " +
            "InitializeEnvironment reports the current connection and environment.";

    private static readonly Lazy<MethodInfo[]> s_attributedMethods = new(() => typeof(EngineHostPipeline).Assembly
        .GetTypes().SelectMany(type => type.GetMethods())
        .Where(method => method.GetCustomAttribute<McpServerToolAttribute>() != null).ToArray());

    internal sealed record Roster(IReadOnlyList<string> PreferredOrder, IReadOnlyList<string> LiteNames);

    internal static IReadOnlyDictionary<string, Roster> Rosters { get; } =
        TiaMcp.Adapters.Contracts.ReleaseRange.All.ToDictionary(key => key, key =>
        {
            var entries = ToolUsageCatalog.ProfileEntries(key);
            var names = entries.Select(row => (string)row!["currentName"]!);
            var lite = entries.Where(row => row!["profiles"]!.AsArray().Any(profile => (string?)profile == "lite"))
                .Select(row => (string)row!["currentName"]!);
            return new Roster(
                names.Append("GetCallResult").Append("GetApprovalStatus").Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
                lite.Append("GetCallResult").Append("GetApprovalStatus").Distinct(StringComparer.Ordinal).ToArray());
        }, StringComparer.Ordinal);

    private static readonly ReleaseCatalogData Presentation = new(DiscoveryInstructions, true, true, true,
        TiaMcp.Adapters.Contracts.ReleaseRange.All, StringComparer.Ordinal,
        "plc-foundation", new Dictionary<string, string>(StringComparer.Ordinal),
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ManagePlcGitRepository"] = "CallToolResult",
            ["RunPlcCompanionTool"] = "CallToolResult",
            ["DecodePlcSimaticMl"] = "CallToolResult",
            ["CheckProductUpdate"] = "CallToolResult",
            ["GenerateAcceptanceReport"] = "CallToolResult",
            ["GetEnvironmentDiagnostics"] = "CallToolResult"
        }) { RegisterDiscoveryHandlers = true, CatalogCallDiscipline = true, HasManifestCatalog = true };

    internal static IReadOnlyDictionary<string, ReleaseCatalogData> Releases { get; } =
        Rosters.Keys.ToDictionary(key => key, _ => Presentation, StringComparer.Ordinal);

    internal string ExampleProfile(string name) => ExampleProfiles.GetValueOrDefault(name, DefaultExampleProfile);

    internal IEnumerable<MethodInfo> Declarations(string release)
    {
        var ported = PortedToolDeclarations.Methods(release, RetainUnavailable).ToArray();
        var names = ported.Select(Name).ToHashSet(StringComparer.Ordinal);
        var order = Rosters[release].PreferredOrder;
        var discovery = order.Count == 0 ? Enumerable.Empty<MethodInfo>() : s_attributedMethods.Value
            .Where(method => !names.Contains(Name(method)) && order.Contains(Name(method), StringComparer.Ordinal));
        return ported.Concat(discovery).Where(method => UseDeclaredDiscovery || ToolExecution.Table[Name(method)] == "host");
    }

    internal static string Name(MethodInfo method) => method.GetCustomAttribute<McpServerToolAttribute>()!.Name ?? method.Name;
}

internal sealed record WorkerSupervisorState(System.Text.Json.Nodes.JsonObject Legacy, bool Faulted, bool Started,
    long Generation, int Active, int Queued, double TimeoutSeconds, System.Text.Json.Nodes.JsonNode? TimeoutPolicy,
    System.Text.Json.Nodes.JsonNode? Fault, System.Text.Json.Nodes.JsonNode? Readiness,
    System.Text.Json.Nodes.JsonNode? Binding, System.Text.Json.Nodes.JsonArray PreviousGenerations);

internal static class WorkerSupervisorReplies
{
    internal static System.Text.Json.Nodes.JsonObject Fault(Exception error, string operation, bool dispatched,
        long? bindingEpoch, long? channelRequestId, int? processId)
        => new() { ["cause"] = error.GetType().Name, ["message"] = error.Message,
            ["operation"] = operation, ["dispatched"] = dispatched, ["outcome"] = dispatched ? "unknown" : "not-started",
            ["observedUtc"] = DateTimeOffset.UtcNow.ToString("O"), ["bindingEpoch"] = bindingEpoch,
            ["channelRequestId"] = channelRequestId, ["processId"] = processId };

    internal static System.Text.Json.Nodes.JsonObject Snapshot(string release, WorkerSupervisorState state)
    {
        if (!ReleaseCatalogData.Releases[release].ExtendedWorkerStatus)
        {
            var legacy = (System.Text.Json.Nodes.JsonObject)state.Legacy.DeepClone();
            legacy["readiness"] = state.Readiness?.DeepClone();
            return legacy;
        }
        return new System.Text.Json.Nodes.JsonObject { ["enabled"] = true,
            ["state"] = state.Faulted ? "Faulted" : state.Active > 0 ? "Busy" : state.Started ? "Ready" : "NotStarted",
            ["generation"] = state.Generation, ["active"] = state.Active, ["queued"] = state.Queued,
            ["timeoutSeconds"] = state.TimeoutSeconds, ["timeoutPolicy"] = state.TimeoutPolicy?.DeepClone(),
            ["fault"] = state.Fault?.DeepClone(), ["readiness"] = state.Readiness?.DeepClone(),
            ["binding"] = state.Binding?.DeepClone(), ["previousGenerations"] = state.PreviousGenerations.DeepClone() };
    }
    internal static System.Text.Json.Nodes.JsonObject Restart(bool confirmed, bool success,
        System.Text.Json.Nodes.JsonObject worker)
    {
        var result = ResponseMeta.Unstamped(success);
        if (!confirmed) result["dryRun"] = true;
        else if (success) result["restarted"] = true;
        result["worker"] = worker;
        return result;
    }
}
