using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace TiaMcp.ReleaseTool;

internal static class ReleaseValidation
{
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
}
