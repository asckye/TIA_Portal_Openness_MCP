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
    bool UseDeclaredDiscovery, string DeclarationDisclosure, IReadOnlyList<string> PreferredOrder,
    IReadOnlyList<string> LiteNames, IReadOnlyList<string> ManifestSupplementaryReleases, StringComparer NameComparer,
    string DefaultExampleProfile, IReadOnlyDictionary<string, string> ExampleProfiles,
    IReadOnlyDictionary<string, string> ManifestReturnTypes)
{
    internal string ProfileName { get; init; } = "plc-foundation";
    internal bool RegisterDiscoveryHandlers { get; init; }
    internal bool CatalogCallDiscipline { get; init; }
    internal bool HasManifestCatalog { get; init; }
    internal bool SharedSession { get; init; }
    internal bool MergeWorkerReadiness { get; init; }
    internal bool RevealSessionEndedDiagnostic { get; init; }
    internal bool WrapPortedTools { get; init; }
    internal string FoundationInvocationTag { get; init; } = "foundation";
    internal string HostInvocationTag { get; init; } = "engine";
    internal bool DisclosePortedBehavior { get; init; } = true;
    internal bool ExtendedWorkerStatus { get; init; }
    internal bool StartSupervisorOnRead { get; init; }
    internal bool StatusOutcomeIsFault { get; init; } = true;
    internal bool NativeObservations { get; init; }
    internal IReadOnlyList<string> ObservationToolNames { get; init; } = Array.Empty<string>();
    internal IReadOnlyList<string> ObservationSet { get; init; } = new[] {
        "environment", "group", "group.fix", "assemblies", "tool-names", "reflection", "reflection-deny",
        "session.GetState", "session.ListPortalProcessProjects", "session.GetProjectTree", "session.ConnectPortal" };
    internal bool FoundationReadinessBoundary { get; init; } = true;
    private const string DiscoveryInstructions = ToolUsageCatalog.Instructions +
        " FindTools searches the complete release catalog, including tools omitted by the lite profile; CallTool invokes a discovered tool. " +
            "InitializeEnvironment reports the current connection and environment.";

    private static readonly Lazy<MethodInfo[]> s_attributedMethods = new(() => typeof(EngineHostPipeline).Assembly
        .GetTypes().SelectMany(type => type.GetMethods())
        .Where(method => method.GetCustomAttribute<McpServerToolAttribute>() != null).ToArray());

    internal static IReadOnlyDictionary<string, ReleaseCatalogData> Releases { get; } =
        new Dictionary<string, ReleaseCatalogData>(StringComparer.Ordinal)
        {
            ["14sp1"] = Foundation("14sp1"),
            ["15.1"] = Foundation("15.1"),
            ["16"] = Foundation("16"),
            ["17"] = Foundation("17"),
            ["18"] = Foundation("18"),
            ["19"] = Foundation("19"),
            ["20"] = Declared("20"),
            ["21"] = Declared("21")
        };

    private static ReleaseCatalogData Presentation(string release) => new(DiscoveryInstructions, true, true, true, "",
        ToolUsageCatalog.ProfileEntries(release).Select(row => (string)row!["currentName"]!)
            .Append("GetCallResult").Distinct(StringComparer.Ordinal).OrderBy(name => name, StringComparer.Ordinal).ToArray(),
        ToolUsageCatalog.ProfileEntries(release).Where(row => row!["profiles"]!.AsArray().Any(profile => (string?)profile == "lite"))
            .Select(row => (string)row!["currentName"]!).Append("GetCallResult").Distinct(StringComparer.Ordinal).ToArray(),
        new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" }, StringComparer.Ordinal,
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

    // Invocation/disclosure policy is deliberately unchanged until P8-04n2.
    private static ReleaseCatalogData Foundation(string release) => Presentation(release) with
        { DeclarationDisclosure = " Native behaviorPolicy=current; V4 native acceptance is pending." };

    private static ReleaseCatalogData Declared(string release) => Presentation(release) with
        { SharedSession = true, MergeWorkerReadiness = true, RevealSessionEndedDiagnostic = true, WrapPortedTools = true, DisclosePortedBehavior = false,
            ExtendedWorkerStatus = true, StartSupervisorOnRead = true, StatusOutcomeIsFault = false, NativeObservations = true, FoundationReadinessBoundary =
                false,
            ObservationToolNames = ToolUsageCatalog.ProfileEntries(release, engineSource: true).Select(row => (string)row!["currentName"]!)
                .OrderBy(name => name, StringComparer.Ordinal)
                .Concat(TiaMcp.Adapters.Contracts.PortedFamilies.All.Where(f => f.Available(release)).SelectMany(f => f.Tools))
                .Distinct(StringComparer.Ordinal).ToArray(),
            ObservationSet = new[] { "environment", "group", "group.fix", "assemblies", "tool-names", "reflection", "reflection-deny",
                "session.GetState", "session.ListPortalProcessProjects", "session.GetProjectTree", "session.ConnectPortal", "session.ValidateAutomationContext"
                } };

    internal string ExampleProfile(string name) => ExampleProfiles.GetValueOrDefault(name, DefaultExampleProfile);

    internal IEnumerable<MethodInfo> Declarations(string release)
    {
        var ported = PortedToolDeclarations.Methods(release, RetainUnavailable).ToArray();
        var names = ported.Select(Name).ToHashSet(StringComparer.Ordinal);
        var discovery = PreferredOrder.Count == 0 ? Enumerable.Empty<MethodInfo>() : s_attributedMethods.Value
            .Where(method => !names.Contains(Name(method)) && PreferredOrder.Contains(Name(method), StringComparer.Ordinal));
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
