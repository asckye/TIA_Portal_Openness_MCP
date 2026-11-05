namespace TiaMcp.ReleaseTool;

internal static class ReleaseRules
{
    internal static readonly HashSet<string> Rules =
    [
        "CA2100", "CA2305", "CA2311", "CA2312", "CA2321", "CA2322", "CA2327", "CA2328",
        "CA2329", "CA2330", "CA3001", "CA3002", "CA3003", "CA3004", "CA3005", "CA3006",
        "CA3007", "CA3008", "CA3009", "CA3010", "CA3011", "CA3012", "CA5380", "CA5381",
        "CA5382", "CA5383", "CA5384", "CA5385", "CA5386", "CA5387", "CA5388", "CA5389",
        "CA5390", "CA5399", "CA5400", "CA5401", "CA5402", "CA5403"
    ];

    internal static bool Expected(AnalyzerWarningCollection current, AnalyzerWarning entry) => current.Profiles == null ||
        (current.Profiles.TryGetValue(entry.Path, out var profiles) ? profiles : current.Profiles.Values.SelectMany(value => value))
            .Any(profile => Emits(profile, entry.Path, entry.Rule));

    internal static bool Emits(string profile, string path, string rule) =>
        !Rules.Contains(rule) || profile == "release" && path.StartsWith("src/", StringComparison.Ordinal);
}
