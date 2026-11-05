using System.Globalization;
using System.Text;
using System.Text.Json;

namespace TiaMcp.ReleaseTool;

internal sealed record ScorecardRow(string Dimension, string Measure, string Release, string State, double? Raw,
    string Unit, int? Score, string Band, string Source, string Detail, string Change = "new");

internal sealed record ScorecardDimension(string Dimension, string Release, string State, int? Score);
internal sealed record ScorecardTypeIndex(string Path, string Type, double Estimate);

internal sealed class ScorecardReport
{
    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true, NewLine = "\n"
    };
    public int Format { get; set; } = 1;
    public string Commit { get; set; } = "";
    public DateTime Utc { get; set; } = DateTime.UtcNow;
    public double Seconds { get; set; }
    public string Bands { get; set; } = "proposed";
    public List<ScorecardRow> Rows { get; set; } = [];
    public List<string> Notes { get; set; } = [];
    public List<ScorecardTypeIndex> TypeIndices { get; set; } = [];
    public ScorecardDimension[] Dimensions => Rows.Where(row => row.Dimension != "Performance trend")
        .GroupBy(row => (row.Dimension, row.Release)).Select(group =>
        {
            var scores = group.Where(row => row.Score != null).Select(row => row.Score!.Value).ToArray();
            string state = scores.Length == 0 ? group.Any(row => row.State == "not measured") ? "not measured" : group.First().State
                : group.Any(row => row.State != "measured") ? "partial" : "measured";
            return new ScorecardDimension(group.Key.Dimension, group.Key.Release, state, scores.Length == 0 ? null : scores.Min());
        }).ToArray();

    internal void Add(string dimension, string measure, string release, double raw, string source,
        string detail = "", string unit = "count")
    {
        var band = ScorecardBands.Find(measure);
        Rows.Add(new(dimension, measure, release, "measured", raw, unit, band?.Grade(raw),
            band == null ? "unscored" : band.PassRequired ? "proposed; pass=5, failure=0"
                : $"proposed; {(band.HigherIsBetter ? ">=" : "<=")} {string.Join(", ", band.Edges)}",
            source, detail));
    }

    internal void Missing(string dimension, string measure, string release, string source, string reason,
        string state = "not measured") => Rows.Add(new(dimension, measure, release, state, null, "", null, "", source, reason));

    internal void Capture(string dimension, string release, string source, Action collect, string measure = "source")
    {
        try { collect(); }
        catch (Exception ex) { Missing(dimension, measure, release, source, ex.Message); }
    }

    internal void Compare(ScorecardReport? previous)
    {
        for (int index = 0; index < Rows.Count; index++)
        {
            var row = Rows[index];
            var old = previous?.Rows.FirstOrDefault(item =>
                (item.Dimension, item.Measure, item.Release) == (row.Dimension, row.Measure, row.Release));
            if (old == null) continue;
            string change = row.Raw is { } raw && old.Raw is { } before ? Signed(raw - before) : old.State + " → " + row.State;
            if (row.Score is { } score && old.Score is { } priorScore) change += "; score " + Signed(score - priorScore);
            if (row.Dimension == "Performance trend" && row.Raw is { } current && old.Raw is { } prior)
                change += current < prior ? "; faster" : current > prior ? "; slower" : "; unchanged";
            Rows[index] = row with { Change = change };
        }
    }

    internal static string Number(double value) => value.ToString("0.####", CultureInfo.InvariantCulture);
    private static string Signed(double value) => (value > 0 ? "+" : "") + Number(value);
    private static string Cell(string value) => value.Replace("|", "\\|", StringComparison.Ordinal)
        .Replace("\r", "", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);

    internal string Render()
    {
        var text = new StringBuilder("# Quality scorecard\n\n");
        text.AppendLine($"Commit: {Commit}. UTC: {Utc:O}. Elapsed: {Number(Seconds)} seconds. Bands: **proposed**.");
        text.AppendLine("\nInformational only; no total or project score. Each release stands on its own. " +
            "A dimension uses its lowest measured score; partial means a component is unavailable.\n");
        text.AppendLine("| Dimension | Release | State | Score (proposed) |\n|---|---|---|---:|");
        foreach (var row in Dimensions)
            text.AppendLine($"| {row.Dimension} | {row.Release} | {row.State} | {row.Score?.ToString() ?? "—"} |");
        text.AppendLine("\n| Dimension / measure | Release | Raw | State | Band | Score | Change | Source / detail |" +
            "\n|---|---|---:|---|---|---:|---|---|");
        foreach (var row in Rows)
        {
            string raw = row.Raw is { } value ? Number(value) + " " + row.Unit : "—";
            text.AppendLine($"| {row.Dimension} / {row.Measure} | {row.Release} | {raw} | {row.State} | " +
                $"{Cell(row.Band)} | {row.Score?.ToString() ?? "—"} | {Cell(row.Change)} | {Cell(row.Source + "; " + row.Detail)} |");
        }
        text.AppendLine("\nOfficial API coverage counts documented M:/P: member names, merging overloads, intersected with " +
            "compiled direct calls. Dead code counts; reflective calls, enums/types, boilerplate (including GetAttribute, " +
            "GetService, Invoke) and whether calls work are not measured. 100% is not the goal.\n");
        if (TypeIndices.Count != 0)
        {
            text.AppendLine($"\nLowest type maintainability estimates (all {TypeIndices.Count} declarations in scorecard.json):\n");
            text.AppendLine("| File | Type | Index estimate |\n|---|---|---:|");
            foreach (var row in TypeIndices.OrderBy(row => row.Estimate).ThenBy(row => row.Path, StringComparer.Ordinal).Take(20))
                text.AppendLine($"| {Cell(row.Path)} | {Cell(row.Type)} | {Number(row.Estimate)} |");
        }
        foreach (string note in Notes) text.AppendLine("- " + note);
        return text.ToString();
    }
}
