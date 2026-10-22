using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace TiaMcp.ReleaseTool;

internal static class ReleaseValidation
{
    internal static string[] ChildToolArguments(string command, params string[] arguments) =>
        [typeof(ReleaseCommands).Assembly.Location, command, .. arguments];

    internal static string[] PackageToolArguments() => ["--release-tool", typeof(ReleaseCommands).Assembly.Location];

    internal static bool ChangelogVersion(string newest, string released, bool repositoryOnly, bool noteExists)
    {
        if (!IsReleaseText(newest) || !IsReleaseText(released)) return false;
        return newest == released || (repositoryOnly && noteExists && IsNewer(newest, released));
    }

    /// <summary>A release text: X.Y.Z (or the System.Version forms the checks accepted before), X.Y.Z-beta.N or X.Y.Z-rc.N.</summary>
    internal static bool IsReleaseText(string? text) => Version.TryParse(text, out _) || ReleaseVersions.IsValid(text);

    /// <summary>Semantic Versioning precedence for release labels; numeric System.Version comparison for the other forms.</summary>
    internal static bool IsNewer(string newest, string released)
    {
        if (ReleaseVersions.Compare(newest, released) is { } semantic) return semantic > 0;
        return Version.TryParse(newest, out var next) && Version.TryParse(released, out var current) && next > current;
    }

    /// <summary>The package of a release is exactly TIA_MCP_Delivery_v&lt;full version&gt;_&lt;yyyyMMdd&gt; (the archive adds ".zip").</summary>
    internal static string? PackageNameError(string packageName, string version)
    {
        var match = Regex.Match(packageName, "^TIA_MCP_Delivery_v" + Regex.Escape(version) + "_(?<date>\\d{8})$", RegexOptions.CultureInvariant);
        if (match.Success && DateTime.TryParseExact(match.Groups["date"].Value, "yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out _))
            return null;
        return $"Version mismatch: manifest packageName '{packageName}' is not TIA_MCP_Delivery_v{version}_<yyyyMMdd>";
    }

    internal static string? OfflineNuGetError(string path)
    {
        var document = System.Xml.Linq.XDocument.Load(path);
        var sources = document.Root?.Element("packageSources");
        if (sources?.Element("clear") is null) return "NuGetConfig must clear inherited package feeds";
        foreach (var source in sources.Elements("add"))
        {
            var value = (string?)source.Attribute("value") ?? "";
            if (value.Length == 0) continue;
            var uriLike = Regex.IsMatch(value, "^[a-z][a-z0-9+.-]*:", RegexOptions.IgnoreCase) &&
                          !Regex.IsMatch(value, "^[a-z]:[\\\\/]", RegexOptions.IgnoreCase);
            if (uriLike || value.StartsWith("\\\\", StringComparison.Ordinal) || value.StartsWith("//", StringComparison.Ordinal))
                return $"Network NuGet feed is forbidden: {value}";
        }
        return null;
    }

    internal static string? HostApprovalSettings(bool productDefaults) =>
        productDefaults ? null : "enabled=false\ntimeoutSeconds=120\n";

    internal static string SourceHash(string path)
    {
        var extension = Path.GetExtension(path);
        byte[] bytes;
        if (extension.Equals(".ttf", StringComparison.OrdinalIgnoreCase) || extension.Equals(".otf", StringComparison.OrdinalIgnoreCase))
            bytes = File.ReadAllBytes(path);
        else
            bytes = Encoding.UTF8.GetBytes(File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal));
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    internal static string GetBundleDirectory(string zipPath)
    {
        if (!Path.GetExtension(zipPath).Equals(".zip", StringComparison.OrdinalIgnoreCase))
            throw new ReleaseException("Package result must name a ZIP");
        return Path.Combine(Path.GetDirectoryName(Path.GetFullPath(zipPath))!, Path.GetFileNameWithoutExtension(zipPath));
    }

    internal static bool IsReleaseManagedChange(string line)
    {
        if (line.Length < 4 || !line.StartsWith(" M", StringComparison.Ordinal)) return false;
        var path = line[3..].Trim().Trim('"').Replace('\\', '/');
        return path is "Version.props" or ".claude-plugin/plugin.json" or ".claude-plugin/marketplace.json" or "docs/README.md" or
                   "docs/development/roadmap.md" or "docs/reference/tool-matrix.md" ||
               Regex.IsMatch(path, "^manifest/[^/]+\\.json$", RegexOptions.CultureInvariant);
    }

    internal static string? ReleaseDocumentationError(string root, string? version, bool requireNewest)
    {
        var actual = ReleaseVersions.ReadLabel(root);
        if (version is null) version = actual;
        if (actual != version) return "Early gates require Version.props to match -Version (run after the mechanical bump)";
        var changelog = File.ReadAllText(Path.Combine(root, "CHANGELOG.md"));
        var match = ReleaseVersions.NewestChangelogEntry(changelog);
        if (!match.Success || !IsReleaseText(match.Groups["v"].Value) || !IsReleaseText(version) || IsNewer(version, match.Groups["v"].Value) || (requireNewest && match.Groups["v"].Value != version))
            return "Newest CHANGELOG entry differs from the requested release";
        if (!File.Exists(Path.Combine(root, $"docs/releases/v{match.Groups["v"].Value}.md"))) return "Matching release note missing";
        if (!File.ReadAllText(Path.Combine(root, "docs/README.md")).Contains($"[当前版本说明](releases/v{version}.md)", StringComparison.Ordinal)) return "docs/README.md current release link is stale";
        if (!Regex.IsMatch(File.ReadAllText(Path.Combine(root, "docs/development/roadmap.md")), "(?m)^# .*" + Regex.Escape(version))) return "Roadmap title is stale";
        return null;
    }

    internal static string? AssertStepOrder(IReadOnlyList<string> names)
    {
        const string expected = "00-preflight,01-multi-version,02-build-release,03-package-local,04-validate-bundle,05-prompt-registration,06-v4-contracts-capture,07-v4-contracts-compare,08-v4-responses-capture,09-v4-responses-compare,10-relocated-bundle";
        return string.Join(',', names) == expected ? null : "Reviewer chain order changed; preparation must precede the release-wide build and snapshots must follow binary validation";
    }
}
