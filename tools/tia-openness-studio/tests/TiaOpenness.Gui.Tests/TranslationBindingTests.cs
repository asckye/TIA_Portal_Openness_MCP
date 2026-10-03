using System.Windows.Controls;
using System.Windows.Data;
using TiaOpenness.Gui.Localization;
using Xunit;

namespace TiaOpenness.Gui.Tests;

/// <summary>
/// {l:Tr Some.Key} compiles to a binding whose path is an indexer holding a dotted key -
/// "[Toolbar.Doctor]". Whether WPF's property-path parser reads that as one string argument
/// rather than as a nested property walk is not something the compiler checks, and getting it
/// wrong produces blank labels at runtime rather than a build error. So it is checked here,
/// end to end, on a real element.
/// </summary>
[Collection(WpfCollection.Name)]
public class TranslationBindingTests(WpfContext wpf)
{
    private static TextBlock Bound(string key)
    {
        var block = new TextBlock();
        BindingOperations.SetBinding(block, TextBlock.TextProperty, TrExtension.CreateBinding(key));
        return block;
    }

    [Fact]
    public void A_dotted_key_resolves_through_the_indexer()
    {
        wpf.RunWithLanguage(AppLanguage.English, () => Assert.Equal("Doctor", Bound("Toolbar.Doctor").Text));
    }

    [Fact]
    public void A_bound_label_follows_a_language_change_without_being_rebuilt()
    {
        wpf.Run(() =>
        {
            var previous = Loc.Current.Language;
            try
            {
                Loc.Current.Language = AppLanguage.English;
                var label = Bound("Tab.VersionControl");
                Assert.Equal("Version control", label.Text);

                Loc.Current.Language = AppLanguage.Chinese;
                Assert.Equal("版本控制", label.Text);
            }
            finally
            {
                Loc.Current.Language = previous;
            }
        });
    }

    [Fact]
    public void An_unknown_key_shows_the_bracketed_key_on_screen()
    {
        wpf.Run(() => Assert.Equal("[Not.A.Key]", Bound("Not.A.Key").Text));
    }

    /// <summary>
    /// XAML would write this as StringFormat="{}{0}:" - the leading braces are the XAML parser's
    /// own escape and are stripped before the value reaches the extension, so the format that
    /// actually arrives is the plain one used here.
    /// </summary>
    [Fact]
    public void The_string_format_wrapper_is_applied()
    {
        wpf.RunWithLanguage(AppLanguage.English, () =>
        {
            var block = new TextBlock();
            BindingOperations.SetBinding(block, TextBlock.TextProperty,
                TrExtension.CreateBinding("Project.Label", "{0}:"));

            Assert.Equal("Project:", block.Text);
        });
    }
}
