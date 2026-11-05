namespace TiaMcp.ReleaseTool;

internal sealed record ScorecardBand(string Measure, bool HigherIsBetter, double[] Edges, bool PassRequired = false)
{
    internal int Score(double value) => HigherIsBetter
        ? Edges.Count(edge => value >= edge)
        : 5 - Edges.Count(edge => value > edge);

    internal int Grade(double value) => PassRequired && value != 0 ? 0 : Score(value);
}

// Proposed targets, independent of the observations. Shares are fractions, not percentages.
internal static class ScorecardBands
{
    internal static readonly ScorecardBand[] All =
    [
        new("long-methods", false, [0, .01, .05, .10, .20]),
        new("complex-methods", false, [0, .01, .05, .10, .20]),
        new("deep-methods", false, [0, .01, .05, .10, .20]),
        new("many-parameters", false, [0, .01, .05, .10, .20]),
        new("large-files", false, [0, .01, .05, .10, .20]),
        new("naming-diagnostics", false, [0, 1, 5, 10, 25]),
        new("reviewed-sample", true, [1, 2, 3, 4, 5]),
        new("missing-public-docs", false, [0, .05, .10, .25, .50]),
        new("todo-comments", false, [0, 1, 5, 10, 25]),
        new("commented-code", false, [0, 1, 5, 10, 25]),
        new("duplicate-blocks", false, [0, 1, 5, 10, 25]),
        new("project-cycles", false, [0, 1, 2, 3, 5]),
        new("maintainability-index", true, [20, 40, 60, 80, 90]),
        new("suite-coverage", true, [.50, .75, .90, .95, 1]),
        new("failed-tests", false, [0, 1, 5, 10, 25]),
        new("skipped-share", false, [0, .001, .005, .01, .05]),
        new("minimum-margin", true, [0, 1, 5, 10, 20]),
        new("narrow-seconds", false, [600, 900, 1200, 1800, 3600]),
        new("wide-seconds", false, [600, 900, 1200, 1800, 3600]),
        new("first-pass-rate", true, [.50, .70, .80, .90, .95]),
        new("delivery-pass-share", true, [.50, .70, .80, .90, .95]),
        new("delivery-defects", false, [0, 1, 5, 10, 25]),
        new("median-handovers", false, [1, 2, 3, 4, 5]),
        new("maximum-handovers", false, [1, 2, 3, 5, 8]),
        new("security-warnings", false, [0, 1, 5, 10, 25]),
        new("high-critical", false, [0, 1, 2, 3, 5]),
        new("scan-age-days", false, [1, 7, 14, 30, 90]),
        new("repository-errors", false, [0, 1, 5, 10, 25]),
        new("dead-references", false, [0, 1, 2, 3, 5], PassRequired: true),
        new("outdated-docs", false, [0, .05, .10, .25, .50]),
        new("dry-run-share", true, [.50, .70, .80, .90, 1]),
        new("project-identity-share", true, [.50, .70, .80, .90, 1]),
        new("approval-checks", true, [.50, .70, .80, .90, 1]),
        new("official-api-share", true, [.10, .20, .30, .40, .50]),
        new("tools-run-share", true, [.50, .70, .80, .90, 1]),
        new("regressions", false, [0, 1, 5, 10, 25]),
        new("typed-error-share", true, [.50, .70, .80, .90, 1]),
        new("native-message-share", true, [.50, .70, .80, .90, 1]),
        new("outcome-unknown-share", false, [0, .01, .05, .10, .20]),
        new("server-log-share", false, [0, .01, .05, .10, .20])
    ];

    internal static ScorecardBand? Find(string measure) => All.SingleOrDefault(band => band.Measure == measure);
}
