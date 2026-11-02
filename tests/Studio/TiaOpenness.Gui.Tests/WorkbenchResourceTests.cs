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
    [Fact]
    public void Row_and_control_sizes_follow_the_maintainer_scale()
    {
        wpf.Run(() =>
        {
            Assert.Equal(36d, Assert.IsType<double>(Application.Current.FindResource("Ui.RowHeight")));
            Assert.Equal(32d, Assert.IsType<double>(Application.Current.FindResource("Ui.ControlHeight")));
            Assert.Equal(44d, Assert.IsType<double>(Application.Current.FindResource("Ui.ControlHeight.Large")));
        });
    }

    [Theory]
    [InlineData(400, "Regular")]
    // The Chinese fallback resolves to installed real weights, including medium requests.
    [InlineData(500, "SemiBold")]
    [InlineData(600, "SemiBold")]
    public void Chinese_faces_resolve_to_the_installed_system_fallback(int weight, string face)
    {
        wpf.Run(() =>
        {
            var family = new FontFamily("Microsoft YaHei UI");
            var typeface = new Typeface(family, FontStyles.Normal, FontWeight.FromOpenTypeWeight(weight), FontStretches.Normal);
            Assert.True(typeface.TryGetGlyphTypeface(out var glyph));
            Assert.True(File.Exists(glyph.FontUri.LocalPath), face);
            Assert.Contains("Microsoft YaHei UI", glyph.FamilyNames.Values);
            Assert.Contains((int)'中', glyph.CharacterToGlyphMap.Keys);
            Assert.False(typeface.IsBoldSimulated);
            string chain = ((FontFamily)Application.Current.FindResource("Ui.Font")).Source;
            Assert.Equal("Segoe UI Variable Text, Segoe UI, Microsoft YaHei UI", chain);
            Assert.False(typeface.IsObliqueSimulated);
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
