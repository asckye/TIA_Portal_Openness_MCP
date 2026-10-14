using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcp.FoundationHost;

internal sealed class FoundationCatalog : IToolCatalogView
{
    public IReadOnlyDictionary<string, ToolDescriptor> All { get; }
    public IReadOnlyDictionary<string, ToolDescriptor> IncludingUnavailable { get; }
    public IReadOnlyList<ToolDescriptor> Lite { get; }
    public JsonArray BehaviorCapabilities { get; }
    internal string Instructions { get; }
    public ToolDescriptor? Find(string name, bool includeUnavailable = false)
        => (includeUnavailable ? IncludingUnavailable : All).GetValueOrDefault(name);

    internal FoundationCatalog(string release, IReadOnlyList<McpServerTool> shared, ISet<string>? essentials = null)
    {
        var data = ReleaseCatalogData.Releases[release];
        essentials ??= new HashSet<string>(data.LiteNames, StringComparer.Ordinal);
        Instructions = data.Instructions;
        BehaviorCapabilities = TiaMcp.Logic.V4.BehaviorCapabilities.Table(typeof(EngineHostPipeline).Assembly, release);
        var declared = data.Declarations(release).ToDictionary(ReleaseCatalogData.Name, method =>
        {
            DeclaredToolMetadata.Create(ReleaseCatalogData.Name(method), method,
                _ => throw new InvalidOperationException("Metadata only."), out var descriptor);
            descriptor.Tool.Description += data.DeclarationDisclosure;
            return descriptor;
        }, StringComparer.Ordinal);
        All = declared.Where(pair => !data.RetainUnavailable || !TiaMcp.Adapters.Contracts.PortedFamilies.Contains(pair.Key)
                || TiaMcp.Adapters.Contracts.PortedFamilies.Available(release, pair.Key))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        IncludingUnavailable = declared;
        Lite = Array.Empty<ToolDescriptor>();
        var merged = new SharedToolCatalog(this, shared, essentials ?? new HashSet<string>(StringComparer.Ordinal));
        var all = merged.All.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        // Old-release host declarations retain their existing presentation after the shared overlay.
        foreach (var pair in declared.Where(_ => !data.UseDeclaredDiscovery))
            all[pair.Key] = pair.Value;
        var order = data.PreferredOrder.Concat(shared.Select(tool => tool.ProtocolTool.Name)).Concat(all.Keys)
            .Distinct(StringComparer.Ordinal).Where(all.ContainsKey);
        All = order.ToDictionary(name => name, name => all[name], data.NameComparer);
        IncludingUnavailable = data.RetainUnavailable
            ? data.PreferredOrder.Concat(declared.Keys).OrderBy(name => name, StringComparer.Ordinal)
                .Concat(shared.Select(tool => tool.ProtocolTool.Name)).Distinct(StringComparer.Ordinal)
                .Where(merged.IncludingUnavailable.ContainsKey)
                .ToDictionary(name => name, name => merged.IncludingUnavailable[name], data.NameComparer)
            : All;
        Lite = data.HasLite ? All.Values.Where(tool => essentials?.Contains(tool.Name) == true
            || SharedToolPolicy.Bridge.Contains(tool.Name, StringComparer.Ordinal)).ToArray() : Array.Empty<ToolDescriptor>();
    }
}
