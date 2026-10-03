using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using TiaOpenness.Gui.Themes;
using Xunit;

namespace TiaOpenness.Gui.Tests;

/// <summary>
/// The appearance switch works by replacing one entry of Application.Resources.MergedDictionaries
/// in place. That makes two things worth holding: the palettes have to be interchangeable - a
/// token present in one and missing from the other leaves controls unpainted in that appearance
/// only - and the palette has to stay at the slot ThemeManager writes to.
/// </summary>
[Collection(WpfCollection.Name)]
public class ThemeTests(WpfContext wpf)
{
    // Taken from ThemeManager rather than repeated, so renaming the assembly cannot leave the
    // test asserting against a URI the app no longer uses.
    private static readonly string PalettePrefix = ThemeManager.PackPrefix;

    private static ResourceDictionary Palette(string file)
        => new() { Source = new Uri(PalettePrefix + file, UriKind.Absolute) };

    private static IReadOnlyList<string> KeysOf(ResourceDictionary dictionary)
        => dictionary.Keys.Cast<object>().Select(k => k.ToString()!).OrderBy(k => k, StringComparer.Ordinal).ToList();

    [Fact]
    public void Both_palettes_define_exactly_the_same_tokens()
    {
        wpf.Run(() =>
        {
            var light = KeysOf(Palette("Palette.Light.xaml"));
            var dark = KeysOf(Palette("Palette.Dark.xaml"));

            Assert.Equal(light, dark);
        });
    }

    [Fact]
    public void The_palettes_are_not_empty_and_actually_differ()
    {
        wpf.Run(() =>
        {
            var light = Palette("Palette.Light.xaml");
            var dark = Palette("Palette.Dark.xaml");

            Assert.NotEmpty(light.Keys);

            var lightWindow = Assert.IsType<SolidColorBrush>(light["Ui.WindowBackground"]);
            var darkWindow = Assert.IsType<SolidColorBrush>(dark["Ui.WindowBackground"]);

            Assert.NotEqual(lightWindow.Color, darkWindow.Color);
        });
    }

    /// <summary>
    /// ThemeManager swaps MergedDictionaries[0] by index. If a dictionary is ever inserted above
    /// the palette in App.xaml, the switch silently replaces the wrong file - so the shape of
    /// that list is asserted rather than left to a comment.
    /// </summary>
    [Fact]
    public void The_palette_occupies_the_first_merged_dictionary_slot()
    {
        wpf.Run(() =>
        {
            var first = Application.Current.Resources.MergedDictionaries[0];

            Assert.Contains("Palette.", first.Source.OriginalString, StringComparison.Ordinal);
            Assert.True(first.Contains("Ui.WindowBackground"));
        });
    }

    /// <summary>
    /// Two dictionaries claiming the same key is the failure mode this theme is most exposed to,
    /// because merged dictionaries resolve last-wins and say nothing about it. A style named the
    /// same as a brush does not break the build, or the XAML load, or even the window's first
    /// layout pass - it breaks when a Border finally asks for its Background and is handed a
    /// Style, which lands as an unhandled exception during rendering.
    /// </summary>
    [Fact]
    public void No_key_is_claimed_by_two_different_dictionaries()
    {
        wpf.Run(() =>
        {
            var seen = new Dictionary<string, string>(StringComparer.Ordinal);
            var clashes = new SortedSet<string>(StringComparer.Ordinal);

            foreach (var dictionary in Application.Current.Resources.MergedDictionaries)
            {
                var origin = dictionary.Source?.OriginalString ?? "(inline)";

                foreach (var key in KeysOf(dictionary))
                {
                    if (seen.TryGetValue(key, out var first)) clashes.Add(key + ": " + first + " and " + origin);
                    else seen[key] = origin;
                }
            }

            Assert.Empty(clashes);
        });
    }

    [Fact]
    public void Switching_appearance_repaints_the_live_resource_lookup()
    {
        wpf.Run(() =>
        {
            var previous = ThemeManager.Current.Theme;
            try
            {
                ThemeManager.Current.Theme = AppTheme.Light;
                var light = ((SolidColorBrush)Application.Current.FindResource("Ui.WindowBackground")).Color;

                ThemeManager.Current.Theme = AppTheme.Dark;
                var dark = ((SolidColorBrush)Application.Current.FindResource("Ui.WindowBackground")).Color;

                Assert.NotEqual(light, dark);
                Assert.True(ThemeManager.Current.EffectivelyDark);
            }
            finally
            {
                ThemeManager.Current.Theme = previous;
            }
        });
    }

    [Fact]
    public void An_explicit_appearance_overrides_what_the_system_is_set_to()
    {
        wpf.Run(() =>
        {
            var previous = ThemeManager.Current.Theme;
            try
            {
                ThemeManager.Current.Theme = AppTheme.Light;
                Assert.False(ThemeManager.Current.EffectivelyDark);
                Assert.True(ThemeManager.Current.IsLight);

                ThemeManager.Current.Theme = AppTheme.Dark;
                Assert.True(ThemeManager.Current.EffectivelyDark);
                Assert.True(ThemeManager.Current.IsDark);
            }
            finally
            {
                ThemeManager.Current.Theme = previous;
            }
        });
    }

    /// <summary>As with the language picker, clearing a segment must not clear the choice.</summary>
    [Fact]
    public void Clearing_an_appearance_flag_does_nothing()
    {
        wpf.Run(() =>
        {
            var previous = ThemeManager.Current.Theme;
            try
            {
                ThemeManager.Current.Theme = AppTheme.Dark;
                ThemeManager.Current.IsDark = false;

                Assert.Equal(AppTheme.Dark, ThemeManager.Current.Theme);
            }
            finally
            {
                ThemeManager.Current.Theme = previous;
            }
        });
    }
}
