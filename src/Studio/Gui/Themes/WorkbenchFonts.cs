using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace TiaOpenness.Gui.Themes;

/// <summary>The two font tokens, loaded privately from Fonts beside the Workbench executable (TYP-01).</summary>
public sealed class WorkbenchFonts : ResourceDictionary
{
    internal const string Family = "Maple Mono CN";
    internal const string UiFallback = "Segoe UI Variable Text, Segoe UI, Microsoft YaHei UI";
    internal const string MonoFallback = "Cascadia Mono, Consolas, Microsoft YaHei UI";

    public WorkbenchFonts() => Apply(this, AppContext.BaseDirectory);

    internal static bool Apply(ResourceDictionary resources, string appDirectory)
    {
        // A folder base URI with a trailing separator lets WPF resolve both real weights.
        // Keep Microsoft YaHei UI explicit so Chinese does not fall back to Japanese glyph forms.
        var folder = new Uri(Path.Combine(appDirectory, "Fonts") + Path.DirectorySeparatorChar);
        bool bundled = Directory.Exists(folder.LocalPath)
            && Fonts.GetFontFamilies(folder).Any(f => f.FamilyNames.Values.Contains(Family, StringComparer.Ordinal));
        resources["Ui.Font"] = bundled ? new FontFamily(folder, $"./#{Family}, {UiFallback}") : new FontFamily(UiFallback);
        resources["Ui.FontMono"] = bundled ? new FontFamily(folder, $"./#{Family}, {MonoFallback}") : new FontFamily(MonoFallback);
        return bundled;
    }
}
