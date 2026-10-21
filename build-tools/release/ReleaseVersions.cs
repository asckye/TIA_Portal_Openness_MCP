using System.Text.RegularExpressions;
using System.Xml.Linq;
using TiaMcp.Versioning;

namespace TiaMcp.ReleaseTool;

// One version source: Version.props holds TiaMcpRelease (numeric X.Y.Z) and TiaMcpPrerelease ("", "beta.N" or "rc.N").
// Release records, the tag, the package name and the notes carry the label; assembly and file versions carry the core.
internal static class ReleaseVersions
{
    /// <summary>A CHANGELOG version: X.Y.Z, X.Y.Z-beta.N or X.Y.Z-rc.N. "## [Unreleased]" never matches.</summary>
    internal const string VersionPattern = @"\d+\.\d+\.\d+(?:-(?:beta|rc)\.(?:0|[1-9]\d*))?";

    internal static ProductReleaseVersion Require(string? text, string message)
    {
        if (!ProductReleaseVersion.TryParse(text, out var version) || version is null) throw new ReleaseException(message);
        return version;
    }

    internal static bool IsValid(string? text) => ProductReleaseVersion.TryParse(text, out _);

    /// <summary>The label from Version.props, or "" when TiaMcpRelease is missing; a missing TiaMcpPrerelease means a plain release.</summary>
    internal static string ReadLabel(string root) => ReadLabel(XDocument.Load(Path.Combine(root, "Version.props")));

    internal static string ReadLabel(XDocument props)
    {
        var group = props.Root?.Element("PropertyGroup");
        var core = ((string?)group?.Element("TiaMcpRelease") ?? "").Trim();
        if (core.Length == 0) return "";
        return ProductReleaseVersion.Compose(core, ((string?)group?.Element("TiaMcpPrerelease") ?? "").Trim());
    }

    /// <summary>The validated Version.props release; a fork or feature suffix other than beta.N or rc.N is refused.</summary>
    internal static ProductReleaseVersion ReadVersionProps(string root, string message = "Public release version must be X.Y.Z, X.Y.Z-beta.N or X.Y.Z-rc.N without fork or feature suffixes") =>
        Require(ReadLabel(root), message);

    /// <summary>The file version of a release label: the numeric core plus ".0". A text that is not a release keeps the former suffix rule.</summary>
    internal static string FileVersionOf(string release) =>
        ProductReleaseVersion.TryParse(release, out var version) && version is not null ? version.FileVersion : release + ".0";

    /// <summary>The newest "## [version]" heading of a CHANGELOG (group "v"), or an unsuccessful match. The heading spacing rule of each
    /// caller is kept: one space, or any whitespace when <paramref name="anySpace"/> is set.</summary>
    internal static Match NewestChangelogEntry(string changelog, bool anySpace = false) =>
        Regex.Match(changelog, "(?m)^##" + (anySpace ? "\\s*" : " ") + "\\[(?<v>" + VersionPattern + ")\\]");

    /// <summary>Semantic Versioning precedence of two release labels; null when either is not a release.</summary>
    internal static int? Compare(string? left, string? right)
    {
        if (!ProductReleaseVersion.TryParse(left, out var a) || !ProductReleaseVersion.TryParse(right, out var b) || a is null || b is null) return null;
        return a.CompareTo(b);
    }
}
