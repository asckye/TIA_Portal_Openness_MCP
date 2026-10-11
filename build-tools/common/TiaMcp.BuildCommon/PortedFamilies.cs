using System.Text.RegularExpressions;

namespace TiaMcp.BuildCommon;

// Read the same reviewed table compiled by the host and workers; fail on unknown row syntax.
public static class PortedFamilies
{
    public sealed record Family(string Prefix, string[] Releases, string[] Tools, Dictionary<string, string[]> ToolReleases);
    public static readonly IReadOnlyList<string> ReleaseKeys = Array.AsReadOnly(new string[] {"14sp1", "15.1", "16", "17", "18", "19", "20", "21"});
    public static Dictionary<string, Family> Load(string root) => Parse(Repository.ReadSource(Path.Combine(root, "src/Adapters.Contracts/PortedFamilies.cs")));
    public static HashSet<string> FoundationCandidates(string root) => ParseFoundationCandidates(Repository.ReadSource(Path.Combine(root, "src/Adapters.Contracts/PortedFamilies.cs")));
    public static HashSet<string> ParseFoundationCandidates(string source)
    {
        var declarations = Regex.Matches(source, @"\bFoundationCandidates\s*=\s*(?:\{(?<body>[^}]+)\}|Array\.AsReadOnly\(new string\[\]\s*\{(?<body>[^}]+)\}\s*\))\s*;");
        Require(declarations.Count == 1 && Regex.Matches(source, @"\bFoundationCandidates\s*=").Count == 1, "Missing or unparsed reviewed Foundation candidate declarations");
        var body = declarations[0].Groups["body"].Value; var names = Strings(body);
        Require(names.Length > 0 && names.Distinct(StringComparer.Ordinal).Count() == names.Length
            && Regex.IsMatch(body, "^\\s*\"[A-Za-z0-9]+\"(?:\\s*,\\s*\"[A-Za-z0-9]+\")*\\s*,?\\s*$"), "Invalid Foundation candidate declarations");
        var tools = Parse(source).Values.SelectMany(f => f.Tools).ToHashSet(StringComparer.Ordinal);
        Require(names.All(tools.Contains), "Unknown Foundation candidate declaration");
        return names.ToHashSet(StringComparer.Ordinal);
    }
    private static string[] Strings(string text) => Regex.Matches(text, "\"([^\"\\n]+)\"").Select(m => m.Groups[1].Value).ToArray();
    private static void Require(bool value, string message) { if (!value) throw new ArgumentException(message); }
    public static Dictionary<string, Family> Parse(string source)
    {
        var rows = Regex.Matches(source, "new Family\\(\"([^\"\\n]+)\",\\s*\"([^\"\\n]+)\",\\s*new\\[\\]\\s*\\{([^}]+)\\},\\s*new\\[\\]\\s*\\{([^}]+)\\}\\s*(?=[,)])");
        Require(rows.Count > 0 && rows.Count == Regex.Matches(source, @"\bnew\s+Family\s*\(").Count, "Unparsed ported-family availability row");
        var result = new Dictionary<string, Family>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match row in rows)
        {
            var releases = Strings(row.Groups[3].Value); var tools = Strings(row.Groups[4].Value);
            Require(releases.Length > 0 && releases.Distinct().Count() == releases.Length && releases.All(ReleaseKeys.Contains), "Invalid ported-family releases");
            Require(tools.Length > 0 && tools.All(seen.Add), "Duplicate ported tool");
            Require(result.TryAdd(row.Groups[1].Value, new(row.Groups[2].Value, releases, tools, new(StringComparer.Ordinal))), "Duplicate ported family");
        }
        var overrides = Regex.Match(source, @"\bToolReleases\s*=\s*new Dictionary<string, string\[\]>\([^)]*\)\s*\{(.*?)\n\s*\};", RegexOptions.Singleline);
        Require(overrides.Success || !Regex.IsMatch(source, @"\bToolReleases\s*="), "Unparsed per-tool availability overrides");
        if (overrides.Success)
        {
            var entries = Regex.Matches(overrides.Groups[1].Value, "\\[\"([^\"\\n]+)\"\\]\\s*=\\s*new\\[\\]\\s*\\{([^}]+)\\}");
            Require(entries.Count == overrides.Groups[1].Value.Count(c => c == '='), "Unparsed per-tool availability override");
            foreach (Match row in entries)
            {
                var tool = row.Groups[1].Value; var releases = Strings(row.Groups[2].Value);
                var owners = result.Values.Where(f => f.Tools.Contains(tool)).ToArray();
                Require(owners.Length == 1, "Unknown per-tool availability override: " + tool);
                var owner = owners[0];
                Require(releases.Length > 0 && releases.Distinct().Count() == releases.Length && releases.All(owner.Releases.Contains), "Invalid per-tool releases: " + tool);
                Require(owner.ToolReleases.TryAdd(tool, releases), "Duplicate per-tool availability override");
            }
        }
        return result;
    }
    // Ported tools the reviewed table withholds on a release: no supported action there, so the release does not list them.
    public static HashSet<string> Withheld(Dictionary<string, Family> families, string release) => families.Values
        .SelectMany(f => f.Tools.Where(t => !f.ToolReleases.GetValueOrDefault(t, f.Releases).Contains(release))).ToHashSet(StringComparer.Ordinal);
    public static HashSet<string> Additions(Dictionary<string, Family> families, string release) => families.Values
        .Where(f => f.Releases.Contains(release)).SelectMany(f => f.Tools.Where(t => f.ToolReleases.GetValueOrDefault(t, f.Releases).Contains(release))).ToHashSet(StringComparer.Ordinal);
    public static HashSet<string> Withheld(Dictionary<string, Family> families, string release) => families.Values
        .SelectMany(f => f.Tools).Except(Additions(families, release), StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
}
