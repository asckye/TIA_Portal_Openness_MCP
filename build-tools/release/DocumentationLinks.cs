using System.Text.RegularExpressions;

namespace TiaMcp.ReleaseTool;

internal static class DocumentationLinks
{
    internal static string? AnchorError(string source, string target)
    {
        target = Uri.UnescapeDataString(target.Trim('<', '>'));
        if (Regex.IsMatch(target, @"\A[\w+.-]+:") || !target.Contains('#')) return null;
        var parts = target.Split('#', 2);
        if (parts[1].Length == 0) return null;
        var path = parts[0].Length == 0 ? source : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, parts[0]));
        if (!File.Exists(path) || !path.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) return null;
        var text = File.ReadAllText(path);
        var anchors = Regex.Matches(text, "<(?:a|[hH][1-6])\\s+[^>]*?(?:id|name)=[\"']([^\"']+)[\"']")
            .Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
        var seen = new Dictionary<string, int>();
        var fenced = false;
        foreach (var line in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            if (Regex.IsMatch(line, @"^\s*(```|~~~)")) { fenced = !fenced; continue; }
            if (fenced) continue;
            var heading = Regex.Match(line, @"^ {0,3}#{1,6}\s+(.+?)(?:\s+#+)?$");
            if (!heading.Success) continue;
            var title = Regex.Replace(heading.Groups[1].Value, @"!?\[([^\]]+)\]\([^)]*\)", "$1");
            title = Regex.Replace(title, "<[^>]+>", "");
            var slug = Regex.Replace(title.ToLowerInvariant(), @"[^\p{L}\p{N}\p{M}\s_-]", "");
            slug = Regex.Replace(slug, @"\s", "-");
            var duplicate = seen.GetValueOrDefault(slug);
            seen[slug] = duplicate + 1;
            anchors.Add(duplicate == 0 ? slug : slug + "-" + duplicate);
        }
        return anchors.Contains(parts[1]) ? null : "missing anchor: " + target;
    }
}
