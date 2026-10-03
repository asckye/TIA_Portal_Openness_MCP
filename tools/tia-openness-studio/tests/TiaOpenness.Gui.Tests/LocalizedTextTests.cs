using TiaOpenness.Gui.Localization;
using Xunit;

namespace TiaOpenness.Gui.Tests;

/// <summary>
/// The status line stores a key rather than a sentence, so that a language switch re-renders
/// whatever is currently on screen instead of leaving the last operation's message stranded in
/// the previous language.
/// </summary>
[Collection(WpfCollection.Name)]
public class LocalizedTextTests(WpfContext wpf)
{
    [Fact]
    public void A_key_is_resolved_at_read_time_not_at_construction()
    {
        wpf.Run(() =>
        {
            var previous = Loc.Current.Language;
            try
            {
                Loc.Current.Language = AppLanguage.English;
                var message = LocalizedText.Key("Status.ProjectSaved");
                Assert.Equal("Project saved.", message.Resolve());

                Loc.Current.Language = AppLanguage.Chinese;
                Assert.Equal("项目已保存。", message.Resolve());
            }
            finally
            {
                Loc.Current.Language = previous;
            }
        });
    }

    [Fact]
    public void Arguments_are_formatted_in()
    {
        wpf.RunWithLanguage(AppLanguage.English,
            () => Assert.Equal("Project Line open.", LocalizedText.Key("Status.ProjectOpen", "Line").Resolve()));
    }

    /// <summary>
    /// "Compiling…" is a frame key wrapped around an operation key. Both have to stay deferred,
    /// or half the sentence freezes in the language it was created in.
    /// </summary>
    [Fact]
    public void A_nested_entry_is_resolved_all_the_way_down()
    {
        wpf.Run(() =>
        {
            var previous = Loc.Current.Language;
            try
            {
                var working = LocalizedText.Working("Status.Compiling");

                Loc.Current.Language = AppLanguage.English;
                Assert.Equal("Compiling…", working.Resolve());

                Loc.Current.Language = AppLanguage.Chinese;
                Assert.Equal("正在编译…", working.Resolve());
            }
            finally
            {
                Loc.Current.Language = previous;
            }
        });
    }

    [Fact]
    public void A_nested_entry_carries_its_own_arguments()
    {
        wpf.RunWithLanguage(AppLanguage.English,
            () => Assert.Equal("Reading blocks of PLC_1…",
                LocalizedText.Working("Status.ReadingBlocks", "PLC_1").Resolve()));
    }

    /// <summary>An exception message from the bridge is already final and must pass through.</summary>
    [Fact]
    public void A_literal_is_returned_unchanged_in_either_language()
    {
        wpf.Run(() =>
        {
            var message = LocalizedText.Literal("Openness threw: E_ACCESSDENIED");

            wpf.RunWithLanguage(AppLanguage.Chinese,
                () => Assert.Equal("Openness threw: E_ACCESSDENIED", message.Resolve()));
        });
    }

    [Fact]
    public void The_empty_message_resolves_to_an_empty_string()
    {
        wpf.Run(() => Assert.Equal(string.Empty, LocalizedText.Empty.Resolve()));
    }

    /// <summary>Resolving must not consume the stored arguments; the status line is read repeatedly.</summary>
    [Fact]
    public void Resolving_twice_gives_the_same_answer()
    {
        wpf.RunWithLanguage(AppLanguage.English, () =>
        {
            var message = LocalizedText.Key("Status.VcDiffer", 12, 3);

            Assert.Equal(message.Resolve(), message.Resolve());
            Assert.Equal("12 mapped object(s), 3 differ.", message.Resolve());
        });
    }
}
