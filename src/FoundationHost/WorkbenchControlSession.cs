using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Logic.V4;
using TiaOpenness.Shared;

namespace TiaMcp.FoundationHost;

// A worker belongs to one MCP session. The actor key additionally guards nested
// transport calls and test/embedded consumers which reuse a registry instance.
internal sealed class WorkbenchControlSession
{
    private static readonly ConditionalWeakTable<IFoundationWorker, WorkbenchControlSession> Sessions = new();
    private readonly IFoundationWorker worker;
    private readonly ConcurrentDictionary<string, Registry> registries = new(StringComparer.Ordinal);
    private sealed record Source(string Project, string Software, string Block, string Hash);
    private sealed record Render(WorkbenchRenderArtifact Artifact, string? Project, string? Software, string? Block);
    private sealed class Registry
    {
        internal readonly ConcurrentDictionary<string, Source> Sources = new(StringComparer.OrdinalIgnoreCase);
        internal readonly ConcurrentDictionary<string, Render> Renders = new(StringComparer.Ordinal);
    }
    private WorkbenchControlSession(IFoundationWorker worker) => this.worker = worker;
    internal static WorkbenchControlSession For(IFoundationWorker worker) => Sessions.GetValue(worker, key => new(key));
    internal string SessionKey => (ActorScope.McpSession ?? throw new InvalidOperationException("Missing MCP session scope."))[..16];
    internal string? Project
    {
        get
        {
            var identity = JsonNode.Parse((worker as IFoundationSessionWorker)?.ApprovalIdentity ?? "{}");
            var nested = identity?["identity"] ?? identity?["Identity"];
            return (string?)(identity?["ProjectFile"] ?? identity?["projectFile"] ?? identity?["ProjectPath"] ?? identity?["projectPath"]
                ?? nested?["ProjectFile"] ?? nested?["projectFile"] ?? nested?["ProjectPath"] ?? nested?["projectPath"]);
        }
    }
    private Registry Current => registries.GetOrAdd(SessionKey, _ => new());
    internal void Observe(string tool, IReadOnlyDictionary<string, JsonElement> args, JsonNode? result)
    {
        if (result?["ok"]?.GetValue<bool>() != true || (string?)result["meta"]?["execution"] != "completed") return;
        string? Text(string key) => args.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        var data = result["data"];
        try
        {
            if (tool is "ExportPlcBlock" or "ExportPlcBlocks" && Project is string project && Text("softwarePath") is string software)
            {
                void SourceFile(string? path, string? block)
                {
                    if (path == null || block == null || !LocalFile(path)) return;
                    if (Current.Sources.Count >= 1024) Current.Sources.Clear();
                    Current.Sources[Path.GetFullPath(path)] = new(project, software, block, Hash(path));
                }
                if (tool == "ExportPlcBlock") SourceFile((string?)(data?["outputPath"] ?? data?["outputFile"] ?? data?["exportPath"]) ?? Text("exportPath"), Text("blockPath"));
                else if (data?["items"] is JsonArray items)
                    foreach (var item in items)
                    {
                        var row = item?["result"]?["data"] ?? item;
                        SourceFile((string?)(row?["outputFile"] ?? row?["outputPath"]), (string?)(row?["objectPath"] ?? row?["path"]));
                    }
            }
            if (tool is not ("RenderPlcBlock" or "RenderPlcProgramAtlas")) return;
            string id = (string)result["meta"]!["requestId"]!;
            string path = (string)data!["outputPath"]!;
            string hash = (string)data["sha256"]!;
            if (!LocalHtml(path) || Hash(path) != hash) return;
            var sources = data["sources"]!.AsArray().Select(source => (string)source!["path"]!).ToArray();
            var provenance = sources.Select(source => Current.Sources.TryGetValue(Path.GetFullPath(source), out var entry)
                && LocalFile(source) && Hash(source) == entry.Hash ? entry : null).ToArray();
            bool associated = provenance.Length > 0 && provenance.All(source => source != null
                && source.Project == provenance[0]!.Project && source.Software == provenance[0]!.Software);
            // Bound growth; losing an old locator produces a typed NOT_FOUND.
            if (Current.Renders.Count >= 256) Current.Renders.Clear();
            Current.Renders[id] = new(new() { RequestId = id, Path = Path.GetFullPath(path), Sha256 = hash,
                Kind = tool == "RenderPlcBlock" ? WorkbenchRenderKind.Ladder : WorkbenchRenderKind.Atlas },
                associated ? provenance[0]!.Project : null, associated ? provenance[0]!.Software : null,
                associated && provenance.Length == 1 ? provenance[0]!.Block : null);
        }
        catch (Exception) /* swallow(logging-failure): optional UI artifact registration cannot change a completed export/render result */
        { TiaMcpServer.ModelContextProtocol.InvocationJournal.Write((string?)result["meta"]?["requestId"] ?? "", tool, "WORKBENCH_ARTIFACT_UNAVAILABLE"); }
    }

    internal (WorkbenchRenderArtifact? Artifact, Error? Error) Resolve(string id, WorkbenchRenderKind kind,
        string? software = null, string? block = null)
    {
        if (!Current.Renders.TryGetValue(id, out var render)) return (null, new("Render artifact is not registered in this MCP session.", new NotFoundDetails("render-artifact")));
        if (render.Artifact.Kind != kind || render.Project == null || !string.Equals(render.Project, Project, StringComparison.OrdinalIgnoreCase)
            || kind == WorkbenchRenderKind.Ladder && (render.Software != software || render.Block != block))
            return (null, new("Render artifact does not match the bound project and exact target.", new IdentityMismatchDetails("render-artifact", id, null)));
        try
        {
            if (!File.Exists(render.Artifact.Path)) return (null, new("Render artifact no longer exists.", new NotFoundDetails("render-artifact")));
            if (!LocalHtml(render.Artifact.Path) || Hash(render.Artifact.Path) != render.Artifact.Sha256)
                return (null, new("Render artifact changed after registration.", new IdentityMismatchDetails("render-artifact", render.Artifact.Sha256, null)));
            return (render.Artifact, null);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { return (null, new("Render artifact cannot be read.", new NotFoundDetails("render-artifact"))); }
    }
    private static string Hash(string path)
    { using var file = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant(); }
    private static bool LocalHtml(string path)
        => path.EndsWith(".html", StringComparison.OrdinalIgnoreCase) && LocalFile(path);
    private static bool LocalFile(string path)
    {
        if (!Path.IsPathFullyQualified(path) || path.StartsWith("\\\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal)
            || path[2..].Contains(':')) return false;
        if (new DriveInfo(Path.GetPathRoot(path)!).DriveType == DriveType.Network) return false;
        for (string? part = Path.GetFullPath(path); part != null; part = Path.GetDirectoryName(part))
            if ((File.GetAttributes(part) & FileAttributes.ReparsePoint) != 0) return false;
        return true;
    }
}
