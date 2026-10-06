using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using TiaOpenness.Gui.Views;
using Xunit;

namespace TiaOpenness.Gui.Tests;

[Collection(WpfCollection.Name)]
public sealed class WorkbenchResourceTests(WpfContext wpf)
{
    [Theory]
    [InlineData(400, "Regular")]
    // Only Regular and Bold ship (2026-10-06, package size); medium text uses the nearest embedded face.
    [InlineData(500, "Regular")]
    [InlineData(700, "Bold")]
    public void Chinese_faces_resolve_to_the_embedded_font(int weight, string face)
    {
        wpf.Run(() =>
        {
            var family = new FontFamily(new Uri("pack://application:,,,/TiaOpenness;component/"), "./Fonts/#Noto Sans SC");
            var typeface = new Typeface(family, FontStyles.Normal, FontWeight.FromOpenTypeWeight(weight), FontStretches.Normal);
            Assert.True(typeface.TryGetGlyphTypeface(out var glyph));
            Assert.EndsWith("notosanssc-" + face.ToLowerInvariant() + ".otf", glyph.FontUri.ToString().ToLowerInvariant());
            Assert.Contains((int)'中', glyph.CharacterToGlyphMap.Keys);
            Assert.False(typeface.IsBoldSimulated);
            string chain = ((FontFamily)Application.Current.FindResource("Ui.Font")).Source;
            Assert.Contains("#Manrope, /TiaOpenness;component/Fonts/#Noto Sans SC, Microsoft YaHei UI", chain);
            Assert.True(File.Exists(Path.Combine(AppContext.BaseDirectory, "Fonts", "NotoSansSC-OFL.txt")));
        });
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("{}", "")]
    [InlineData("{broken", "")]
    [InlineData("{\"packageName\":42}", "")]
    [InlineData("{\"packageName\":\"fixture-package\",\"package\":\"wrong-field\"}", "fixture-package")]
    public void Package_label_uses_only_the_bundle_package_name(string? json, string expected)
    {
        string root = Path.Combine(AppContext.BaseDirectory, "package-label", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "manifest"));
        try
        {
            if (json != null) File.WriteAllText(Path.Combine(root, "manifest", "package-manifest.json"), json);
            Assert.Equal(expected, SettingsView.ReadPackageName(root));
        }
        finally { Directory.Delete(root, true); }
    }
}
