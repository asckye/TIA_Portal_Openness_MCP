using System;
using System.Windows;
using System.Windows.Media;

namespace TiaOpenness.Gui.Themes;

/// <summary>The system UI and code font chains (TYP-01).</summary>
public sealed class WorkbenchFonts : ResourceDictionary
{
    internal const string UiFallback = "Segoe UI Variable Text, Segoe UI, Microsoft YaHei UI";
    internal const string MonoFallback = "Cascadia Mono, Consolas, Microsoft YaHei UI";

    public WorkbenchFonts() => Apply(this, AppContext.BaseDirectory);

    internal static bool Apply(ResourceDictionary resources, string appDirectory)
    {
        // Keep Microsoft YaHei UI explicit for Chinese glyph fallback; no font installation is needed.
        resources["Ui.Font"] = new FontFamily(UiFallback);
        resources["Ui.FontMono"] = new FontFamily(MonoFallback);
        return false;
    }
}
