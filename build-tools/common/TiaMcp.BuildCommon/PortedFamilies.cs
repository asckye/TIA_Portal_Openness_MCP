using System.Text.RegularExpressions;

namespace TiaMcp.BuildCommon;

// Read the same reviewed table compiled by the host and workers; fail on unknown row syntax.
public static class PortedFamilies
{
    public sealed record Family(string Prefix, string[] Releases, string[] Tools, Dictionary<string, string[]> ToolReleases)
    {
        public Dictionary<string, string> UnavailableReasons { get; } = new(StringComparer.Ordinal);
    }
    private const string ReleaseListPattern = """(?:new\[\]\s*\{[^}]+\}|ReleaseRange\.(?:All\b|Since\("[^"\n]+"\)|Between\("[^"\n]+",\s*"[^"\n]+"\)))""";
    public static Dictionary<string, Family> Load(string root, IReadOnlyList<string> releaseKeys) => Parse(Repository.ReadSource(Path.Combine(root, "src/Adapters.Contracts/PortedFamilies.cs")), releaseKeys);
    public static HashSet<string> FoundationCandidates(string root, IReadOnlyList<string> releaseKeys) => ParseFoundationCandidates(Repository.ReadSource(Path.Combine(root, "src/Adapters.Contracts/PortedFamilies.cs")), releaseKeys);
    public static HashSet<string> ParseFoundationCandidates(string source, IReadOnlyList<string> releaseKeys)
    {
        var declarations = Regex.Matches(source, @"\bFoundationCandidates\s*=\s*(?:\{(?<body>[^}]+)\}|Array\.AsReadOnly\(new string\[\]\s*\{(?<body>[^}]+)\}\s*\))\s*;");
        Require(declarations.Count == 1 && Regex.Matches(source, @"\bFoundationCandidates\s*=").Count == 1, "Missing or unparsed reviewed Foundation candidate declarations");
        var body = declarations[0].Groups["body"].Value; var names = Strings(body);
        Require(names.Length > 0 && names.Distinct(StringComparer.Ordinal).Count() == names.Length
            && Regex.IsMatch(body, "^\\s*\"[A-Za-z0-9]+\"(?:\\s*,\\s*\"[A-Za-z0-9]+\")*\\s*,?\\s*$"), "Invalid Foundation candidate declarations");
        var tools = Parse(source, releaseKeys).Values.SelectMany(f => f.Tools).ToHashSet(StringComparer.Ordinal);
        Require(names.All(tools.Contains), "Unknown Foundation candidate declaration");
        return names.ToHashSet(StringComparer.Ordinal);
    }
    private static string[] Strings(string text) => Regex.Matches(text, "\"([^\"\\n]+)\"").Select(m => m.Groups[1].Value).ToArray();
    private static void Require(bool value, string message) { if (!value) throw new ArgumentException(message); }
    private static string[] ExpandReleases(string expression, IReadOnlyList<string> releaseKeys)
    {
        if (expression.StartsWith("new[]", StringComparison.Ordinal)) return Strings(expression);
        if (expression == "ReleaseRange.All") return releaseKeys.ToArray();
        var endpoints = Strings(expression);
        var keys = releaseKeys.ToArray();
        var first = Array.IndexOf(keys, endpoints[0]);
        Require(first >= 0, "Unknown release key: " + endpoints[0]);
        var last = endpoints.Length == 1 ? keys.Length - 1 : Array.IndexOf(keys, endpoints[1]);
        Require(last >= 0, "Unknown release key: " + endpoints[^1]);
        Require(first <= last, "Reversed release range: " + endpoints[0] + " to " + endpoints[^1]);
        return keys.Skip(first).Take(last - first + 1).ToArray();
    }
    public static Dictionary<string, Family> Parse(string source, IReadOnlyList<string> releaseKeys)
    {
        var rows = Regex.Matches(source, "new Family\\(\"([^\"\\n]+)\",\\s*\"([^\"\\n]+)\",\\s*(" + ReleaseListPattern + "),\\s*new\\[\\]\\s*\\{([^}]+)\\}\\s*(?=[,)])");
        Require(rows.Count > 0 && rows.Count == Regex.Matches(source, @"\bnew\s+Family\s*\(").Count, "Unparsed ported-family availability row");
        var result = new Dictionary<string, Family>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match row in rows)
        {
            var releases = ExpandReleases(row.Groups[3].Value, releaseKeys); var tools = Strings(row.Groups[4].Value);
            Require(releases.Length > 0 && releases.Distinct().Count() == releases.Length && releases.All(releaseKeys.Contains), "Invalid ported-family releases");
            Require(tools.Length > 0 && tools.All(seen.Add), "Duplicate ported tool");
            Require(result.TryAdd(row.Groups[1].Value, new(row.Groups[2].Value, releases, tools, new(StringComparer.Ordinal))), "Duplicate ported family");
        }
        var overrides = Regex.Match(source, @"\bToolReleases\s*=\s*new Dictionary<string, string\[\]>\([^)]*\)\s*\{(.*?)\n\s*\};", RegexOptions.Singleline);
        Require(overrides.Success || !Regex.IsMatch(source, @"\bToolReleases\s*="), "Unparsed per-tool availability overrides");
        if (overrides.Success)
        {
            var entries = Regex.Matches(overrides.Groups[1].Value, "\\[\"([^\"\\n]+)\"\\]\\s*=\\s*(" + ReleaseListPattern + ")");
            Require(entries.Count == overrides.Groups[1].Value.Count(c => c == '='), "Unparsed per-tool availability override");
            foreach (Match row in entries)
            {
                var tool = row.Groups[1].Value; var releases = ExpandReleases(row.Groups[2].Value, releaseKeys);
                var owners = result.Values.Where(f => f.Tools.Contains(tool)).ToArray();
                Require(owners.Length == 1, "Unknown per-tool availability override: " + tool);
                var owner = owners[0];
                Require(releases.Length > 0 && releases.Distinct().Count() == releases.Length && releases.All(owner.Releases.Contains), "Invalid per-tool releases: " + tool);
                Require(owner.ToolReleases.TryAdd(tool, releases), "Duplicate per-tool availability override");
            }
        }
        var reasons = Regex.Match(source, @"\bUnavailableReasons\s*=\s*new Dictionary<string, string>\([^)]*\)\s*\{(.*?)\n\s*\};", RegexOptions.Singleline);
        Require(reasons.Success || !Regex.IsMatch(source, @"\bUnavailableReasons\s*="), "Unparsed availability reasons");
        if (reasons.Success)
        {
            var entries = Regex.Matches(reasons.Groups[1].Value, "\\[\"([^\"/]+)/([^\"]+)\"\\]\\s*=\\s*\"([^\"\\n]+)\"");
            Require(entries.Count == reasons.Groups[1].Value.Count(c => c == '='), "Unparsed availability reason");
            foreach (Match row in entries)
            {
                var family = row.Groups[1].Value; var release = row.Groups[2].Value;
                Require(result.ContainsKey(family) && releaseKeys.Contains(release), "Unknown availability reason owner");
                Require(result[family].UnavailableReasons.TryAdd(release, row.Groups[3].Value), "Duplicate availability reason");
            }
        }
        return result;
    }
    // Ported tools the reviewed table withholds on a release: no supported action there, so the release does not list them.
    public static HashSet<string> Withheld(Dictionary<string, Family> families, string release) => families.Values
        .SelectMany(f => f.Tools.Where(t => !f.ToolReleases.GetValueOrDefault(t, f.Releases).Contains(release))).ToHashSet(StringComparer.Ordinal);
    public static HashSet<string> Additions(Dictionary<string, Family> families, string release) => families.Values
        .Where(f => f.Releases.Contains(release)).SelectMany(f => f.Tools.Where(t => f.ToolReleases.GetValueOrDefault(t, f.Releases).Contains(release))).ToHashSet(StringComparer.Ordinal);
    public static string WithheldReason(Dictionary<string, Family> families, string release, string tool)
    {
        Require(Withheld(families, release).Contains(tool), "Availability reason requested for a registered tool");
        return families.Values.Single(family => family.Tools.Contains(tool)).UnavailableReasons.GetValueOrDefault(release,
            "no supported action on this release");
    }
}
