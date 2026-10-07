using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace TiaMcp.ReleaseTool;

internal static class ReleaseValidation
{
    internal static bool ChangelogVersion(string newest, string released, bool repositoryOnly, bool noteExists)
    {
        if (!Version.TryParse(newest, out var next) || !Version.TryParse(released, out var current)) return false;
        return newest == released || (repositoryOnly && noteExists && next > current);
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
        return path is "Version.props" or ".claude-plugin/plugin.json" or "docs/README.md" or
                   "docs/development/roadmap.md" or "docs/reference/tool-matrix.md" ||
               Regex.IsMatch(path, "^manifest/[^/]+\\.json$", RegexOptions.CultureInvariant);
    }

    internal static string? ReleaseDocumentationError(string root, string? version, bool requireNewest)
    {
        var props = System.Xml.Linq.XDocument.Load(Path.Combine(root, "Version.props"));
        var actual = (string?)props.Root?.Element("PropertyGroup")?.Element("TiaMcpRelease") ?? "";
        if (version is null) version = actual;
        if (actual != version) return "Early gates require Version.props to match -Version (run after the mechanical bump)";
        var changelog = File.ReadAllText(Path.Combine(root, "CHANGELOG.md"));
        var match = Regex.Match(changelog, "(?m)^## \\[(\\d+\\.\\d+\\.\\d+)\\]");
        if (!match.Success || !Version.TryParse(match.Groups[1].Value, out var newest) || !Version.TryParse(version, out var wanted) || newest < wanted || (requireNewest && match.Groups[1].Value != version))
            return "Newest CHANGELOG entry differs from the requested release";
        if (!File.Exists(Path.Combine(root, $"docs/releases/v{match.Groups[1].Value}.md"))) return "Matching release note missing";
        if (!File.ReadAllText(Path.Combine(root, "docs/README.md")).Contains($"[当前版本说明](releases/v{version}.md)", StringComparison.Ordinal)) return "docs/README.md current release link is stale";
        if (!Regex.IsMatch(File.ReadAllText(Path.Combine(root, "docs/development/roadmap.md")), "(?m)^# .*" + Regex.Escape(version))) return "Roadmap title is stale";
        return null;
    }

    internal static string? AssertStepOrder(IReadOnlyList<string> names)
    {
        const string expected = "00-preflight,01-multi-version,02-build-release,03-package-local,04-validate-bundle,05-prompt-registration,06-v4-contracts-capture,07-v4-contracts-compare,08-v4-responses-capture,09-v4-responses-compare,10-relocated-bundle";
        return string.Join(',', names) == expected ? null : "Reviewer chain order changed; preparation must precede full engines and snapshots must follow binary validation";
    }
}
