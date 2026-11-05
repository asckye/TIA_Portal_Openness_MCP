using System.Text.RegularExpressions;

namespace TiaMcp.ReleaseTool;

// Initializing a newly enabled rule is distinct from updating its only-decrease baseline.
// The base configuration and exact reviewed diagnostics prevent re-baselining an existing rule.
internal static class AnalyzerRuleInitialization
{
    internal static void Validate(ISet<string> rules, string before, string after, IEnumerable<AnalyzerWarning> baseline,
        IEnumerable<AnalyzerWarning> additions, IEnumerable<string>? initialized = null)
    {
        if (rules.Count == 0 || rules.Any(rule => !Regex.IsMatch(rule, @"\ACA\d{4}\z")))
            throw new ReleaseException("-InitializeRules requires comma-separated CA rule IDs", 64);
        if ((initialized ?? []).Any(rules.Contains))
            throw new ReleaseException("Rule was already initialized", 64);
        foreach (string rule in rules)
        {
            string pattern = @"(?m)^\s*dotnet_diagnostic\." + rule + @"\.severity\s*=\s*(?:warning|error)\s*$";
            if (Regex.IsMatch(before, pattern) || !Regex.IsMatch(after, pattern) && !ReleaseRules.Rules.Contains(rule))
                throw new ReleaseException("Rule is not newly enabled as a warning: " + rule, 64);
        }
        if (baseline.Any(entry => rules.Contains(entry.Rule)) || additions.Any(entry => !rules.Contains(entry.Rule)))
            throw new ReleaseException("Rule initialization cannot change existing rule baselines or add other rules", 64);
    }

    internal static void RequireProfiles(ISet<string> rules, AnalyzerWarningCollection current, IEnumerable<AnalyzerWarning> additions)
    {
        foreach (string rule in rules)
        {
            bool Matches(string profile) => ReleaseRules.Rules.Contains(rule) ? profile == "release" : profile is "merge" or "release";
            if (current.Profiles == null || !current.Profiles.Any(pair => pair.Value.Any(Matches) &&
                    (!ReleaseRules.Rules.Contains(rule) || pair.Key.StartsWith("src/", StringComparison.Ordinal))) ||
                additions.Where(entry => entry.Rule == rule).Any(entry =>
                    !current.Profiles.TryGetValue(entry.Path, out var profiles) || !profiles.Any(Matches) ||
                    !profiles.Any(profile => ReleaseRules.Emits(profile, entry.Path, rule))))
                throw new ReleaseException("Initialization requires diagnostics from the emitting profile: " + rule);
        }
    }

}
