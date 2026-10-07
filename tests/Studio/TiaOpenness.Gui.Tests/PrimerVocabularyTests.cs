using System;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace TiaOpenness.Gui.Tests;

public sealed class PrimerVocabularyTests
{
    [Fact]
    public void Pages_use_the_shared_vocabulary_and_never_define_visual_chrome()
    {
        var pages = SourceScan.Markup.Where(f => f.Name.Contains(".Views.", StringComparison.Ordinal)
            || f.Name.Contains(".Configuration.", StringComparison.Ordinal) || f.Name.EndsWith(".MainWindow.xaml", StringComparison.Ordinal)).ToArray();
        Assert.True(pages.Length >= 14);
        foreach (var page in pages)
        {
            Assert.DoesNotMatch(@"#[\da-fA-F]{3,8}\b|(?:DropShadow|Blur)Effect|(?:Linear|Radial)GradientBrush", page.Text);
            var elements = XDocument.Parse(page.Text).Descendants().ToArray();
            Assert.DoesNotContain(elements, e => e.Name.LocalName is "Style" or "ControlTemplate");
            foreach (var element in elements.Where(e => e.Name.LocalName is "Button" or "ToggleButton" or "RadioButton" or "CheckBox" or "Border"))
            {
                Assert.StartsWith("{StaticResource Primer.", (string?)element.Attribute("Style") ?? "");
                foreach (string chrome in new[] { "CornerRadius", "Background", "BorderBrush", "BorderThickness", "Effect" })
                    Assert.Null(element.Attribute(chrome));
            }
            Assert.DoesNotContain(elements, e => e.Attributes().Any(a => a.Name.LocalName == "FontWeight"
                && a.Value is "Bold" or "ExtraBold" or "Black"));
        }
    }

    [Fact]
    public void Only_Primer_and_the_active_palette_define_the_vocabulary()
    {
        var app = SourceScan.Markup.Single(f => f.Name.EndsWith(".App.xaml", StringComparison.Ordinal)).Text;
        Assert.Contains("Themes/Primer.xaml", app);
        Assert.DoesNotMatch(@"Themes/(?:Glass|Controls\.|FeaturePages|Shell|Typography|Window)", app);
        var vocabulary = SourceScan.Markup.Single(f => f.Name.EndsWith(".Themes.Primer.xaml", StringComparison.Ordinal)).Text;
        foreach (string key in new[] { "Container", "HeaderStrip", "FooterStrip", "SectionLabel", "ListRow", "TableItem", "TableButton", "ClientRow", "Secondary", "Primary", "Run", "OutlinedAccent", "SegmentGroup", "Segment", "Chip", "CountBadge", "WarningBadge", "Input", "InputLabel", "MonoInput", "CodeBlock", "Note", "Checkbox", "Drawer", "Toast", "Dialog" })
            Assert.Contains("x:Key=\"Primer." + key + "\"", vocabulary);
        Assert.DoesNotMatch(@"(?:DropShadow|Blur)Effect|(?:Linear|Radial)GradientBrush|Manrope", vocabulary);
        Assert.Contains("Duration=\"0:0:0.12\"", vocabulary);
    }
}
