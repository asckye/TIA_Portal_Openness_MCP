using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace TiaOpenness.Gui.Tests;

// Segoe Fluent Icons ships with Windows 11 only. On Windows 10 the caption buttons, combo box chevrons and
// selection ticks rendered as empty boxes (3.3.0 candidate testing), so every use must fall back to
// Segoe MDL2 Assets, which carries the same code points for the glyphs the workbench uses.
public sealed class IconFontTests
{
    [Fact]
    public void Every_fluent_icon_font_use_falls_back_to_the_windows_10_icon_font()
    {
        var offenders = SourceScan.Markup.Concat(SourceScan.Code)
            .SelectMany(file => Regex.Matches(file.Text, "Segoe Fluent Icons[^\"<]*")
                .Select(match => (file.Name, match.Value)))
            .Where(use => !use.Value.Contains("Segoe MDL2 Assets"))
            .Select(use => use.Name + ": " + use.Value)
            .ToList();
        Assert.True(offenders.Count == 0, string.Join("\n", offenders));
    }

    [Fact]
    public void The_scan_sees_the_icon_font_uses()
    {
        Assert.Contains(SourceScan.Markup, file => file.Text.Contains("Segoe Fluent Icons, Segoe MDL2 Assets"));
    }
}
