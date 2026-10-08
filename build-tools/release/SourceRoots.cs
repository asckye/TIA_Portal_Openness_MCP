using System.Text.Json;

namespace TiaMcp.ReleaseTool;

internal sealed record SourceRoot(string Path, string[] Inventories, string[]? Checks, bool Optional);
internal sealed record RequiredSource(string Path, string[] Consumers, string? RepositoryLabel);

internal sealed record SourceRoots(int SchemaVersion, SourceRoot[] Roots, RequiredSource[] RequiredFiles)
{
    internal const string PolicyPath = "build-tools/release/source-roots.json";
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    internal static SourceRoots Load(string root)
    {
        var path = Path.Combine(root, PolicyPath);
        // Extracted bundles and synthetic source trees use the invoking tool's policy.
        if (!File.Exists(path))
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, PolicyPath))) directory = directory.Parent;
            path = Path.Combine(directory?.FullName ?? throw new ReleaseException("Source root policy missing."), PolicyPath);
        }
        var policy = JsonSerializer.Deserialize<SourceRoots>(File.ReadAllText(path), JsonOptions)
            ?? throw new ReleaseException("Source root policy is empty.");
        if (policy.SchemaVersion != 1 || policy.Roots.Length == 0 ||
            policy.Roots.Select(row => row.Path).Distinct(StringComparer.Ordinal).Count() != policy.Roots.Length ||
            policy.RequiredFiles.Select(row => row.Path).Distinct(StringComparer.Ordinal).Count() != policy.RequiredFiles.Length ||
            policy.Roots.Any(row => !PlainPath(row.Path) || row.Inventories.Except(new[] { "engine", "multi", "validation", "validationMulti" }).Any()) ||
            policy.RequiredFiles.Any(row => !PlainPath(row.Path) || row.Consumers.Except(new[] { "repository", "bundle", "package" }).Any() ||
                row.Consumers.Contains("repository") && string.IsNullOrEmpty(row.RepositoryLabel)))
            throw new ReleaseException("Invalid source root policy.");
        return policy;
    }

    private static bool PlainPath(string path) => path.Length != 0 && !path.Any(character => "\\:*?[]".Contains(character)) &&
        path.Split('/').All(part => part is not ("" or "." or ".."));

    internal IEnumerable<string> Inventory(string kind) => Roots.Where(row => row.Inventories.Contains(kind)).Select(row => row.Path);
    internal IEnumerable<string> RequiredPaths(string consumer) => RequiredFiles.Where(row => row.Consumers.Contains(consumer)).Select(row => row.Path);
    internal IEnumerable<ReleaseCheckRule> CheckRules() => Roots.Where(row => row.Checks is not null)
        .Select(row => new ReleaseCheckRule(row.Path + "/", row.Checks!));
}
