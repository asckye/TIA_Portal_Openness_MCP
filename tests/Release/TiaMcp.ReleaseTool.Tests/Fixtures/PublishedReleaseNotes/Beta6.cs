using TiaMcp.ReleaseTool;
using Xunit;

namespace TiaMcp.ReleaseTool.Tests;

// Preserve the published note and the exact report URL it advertised.
public sealed partial class PrereleaseTests
{
    [Fact]
    public void DraftBetaNotesSendReportsToThePrereleaseFormWithTheItemNumber()
    {
        var notes = File.ReadAllText(Path.Combine(Root, PrereleaseNotes.NotesPath("4.0.0-beta.6")));
        var report = notes[notes.IndexOf("## Report problems", StringComparison.Ordinal)..notes.IndexOf(PrereleaseNotes.Begin, StringComparison.Ordinal)];
        Assert.Contains("https://github.com/asckye/TIA_Portal_Openness_MCP/issues/new?template=prerelease_report.md", report, StringComparison.Ordinal);
        Assert.Contains("item number", report, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(Root, ".github/ISSUE_TEMPLATE/prerelease_report.md")), "pre-release report form missing");
        Assert.Contains("Item number in the release notes' \"Not verified on a real TIA Portal\" list",
            File.ReadAllText(Path.Combine(Root, ".github/ISSUE_TEMPLATE/prerelease_report.md")), StringComparison.Ordinal);
    }
}
