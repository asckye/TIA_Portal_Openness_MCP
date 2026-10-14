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
    private const string DiscoveryInstructions = ToolUsageCatalog.Instructions +
        " FindTools searches the complete release catalog, including tools omitted by the lite profile; CallTool invokes a discovered tool. InitializeEnvironment reports the current connection and environment.";

    private static readonly Lazy<MethodInfo[]> s_attributedMethods = new(() => typeof(EngineHostPipeline).Assembly
        .GetTypes().SelectMany(type => type.GetMethods())
        .Where(method => method.GetCustomAttribute<McpServerToolAttribute>() != null).ToArray());

    internal static IReadOnlyDictionary<string, ReleaseCatalogData> Releases { get; } =
        new Dictionary<string, ReleaseCatalogData>(StringComparer.Ordinal)
        {
            ["14sp1"] = Foundation(),
            ["15.1"] = Foundation(),
            ["16"] = Foundation(),
            ["17"] = Foundation(),
            ["18"] = Foundation(),
            ["19"] = Foundation(),
            ["20"] = Declared("20", Array.Empty<string>()),
            ["21"] = Declared("21", new[] { "14sp1", "15.1", "16", "17", "18", "19", "20" })
        };

    private static ReleaseCatalogData Foundation() => new(ToolUsageCatalog.Instructions +
        " This release uses the PLC foundation catalog. InitializeEnvironment reports environment/session state; ListPortalProcessProjects returns current process/project identities. Only listed contracts are available.",
        false, false, false, " Native behaviorPolicy=current; V4 native acceptance is pending.",
        Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), StringComparer.OrdinalIgnoreCase,
        "plc-foundation", new Dictionary<string, string>(StringComparer.Ordinal),
        new Dictionary<string, string>(StringComparer.Ordinal));

    private static ReleaseCatalogData Declared(string release, IReadOnlyList<string> supplementaryReleases) => new(DiscoveryInstructions, true, true, true, "",
        ToolUsageCatalog.ProfileEntries(release, engineSource: true).Select(row => (string)row!["currentName"]!)
            .Concat(new[] { "RetrieveProjectArchive", "SaveProjectCopy", "ManageMultiuserSession", "BuildProjectScaffold", "ConnectIsolatedPortal" })
            .Distinct(StringComparer.Ordinal).OrderBy(name => name, StringComparer.Ordinal).ToArray(),
        ToolUsageCatalog.ProfileEntries(release).Where(row => row!["profiles"]!.AsArray().Any(profile => (string?)profile == "lite"))
            .Select(row => (string)row!["currentName"]!).ToArray(), supplementaryReleases, StringComparer.Ordinal,
        "full-engine", ToolUsageCatalog.ProfileEntries(release).ToDictionary(row => (string)row!["currentName"]!,
            row => row!["profiles"]!.AsArray().Any(profile => (string?)profile == "plc-foundation") ? "plc-foundation" : "full-engine",
            StringComparer.Ordinal), new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ManagePlcGitRepository"] = "CallToolResult",
            ["RunPlcCompanionTool"] = "CallToolResult",
            ["DecodePlcSimaticMl"] = "CallToolResult",
            ["CheckProductUpdate"] = "CallToolResult",
            ["GenerateAcceptanceReport"] = "CallToolResult",
            ["GetEnvironmentDiagnostics"] = "CallToolResult"
        });

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
