using System;
using System.ComponentModel;
using System.Globalization;
using TiaOpenness.Gui.Localization;
using Xunit;

namespace TiaOpenness.Gui.Tests;

/// <summary>
/// The catalogue lookup itself: what a caller gets for a key that exists, for one that does
/// not, and what the rest of the UI is told when the language changes.
/// </summary>
[Collection(WpfCollection.Name)]
public class LocTests(WpfContext wpf)
{
    [Fact]
    public void Looks_up_the_active_language()
    {
        wpf.RunWithLanguage(AppLanguage.English, () => Assert.Equal("Blocks", Loc.Current["Tab.Blocks"]));
        wpf.RunWithLanguage(AppLanguage.Chinese, () => Assert.Equal("程序块", Loc.Current["Tab.Blocks"]));
    }

    /// <summary>
    /// A bracketed key on screen is a bug report. Returning empty would leave a blank button
    /// that nobody notices until a customer finds it.
    /// </summary>
    [Fact]
    public void An_unknown_key_shows_itself_rather_than_disappearing()
    {
        Assert.Equal("[Nope.Missing]", Loc.Current["Nope.Missing"]);
    }

    [Fact]
    public void Formats_arguments_into_an_entry()
    {
        wpf.RunWithLanguage(AppLanguage.English,
            () => Assert.Equal("2 workspace(s).", Loc.Current.T("Status.VcWorkspaces", 2)));
    }

    /// <summary>Chinese reorders this one, which is the whole reason placeholders are indexed.</summary>
    [Fact]
    public void Honours_a_reordered_placeholder()
    {
        wpf.RunWithLanguage(AppLanguage.English,
            () => Assert.Equal("7 block(s) in PLC_1.", Loc.Current.T("Status.BlocksIn", 7, "PLC_1")));

        wpf.RunWithLanguage(AppLanguage.Chinese,
            () => Assert.Equal("PLC_1 中共 7 个程序块。", Loc.Current.T("Status.BlocksIn", 7, "PLC_1")));
    }

    [Fact]
    public void Surplus_arguments_are_ignored_rather_than_throwing()
    {
        wpf.RunWithLanguage(AppLanguage.English,
            () => Assert.Equal("Not connected.", Loc.Current.T("Status.NotConnected", "unused")));
    }

    /// <summary>
    /// Too few arguments is a programming error, but it must not take down the operation that
    /// was being reported - the caller is usually mid-way through talking to TIA Portal.
    /// </summary>
    [Fact]
    public void A_missing_argument_falls_back_to_the_raw_entry()
    {
        wpf.RunWithLanguage(AppLanguage.English, () =>
        {
            var text = Loc.Current.T("Status.VcWorkspaces");
            Assert.Equal("{0} workspace(s).", text);
        });
    }

    /// <summary>
    /// WPF re-evaluates an indexer binding when it sees PropertyChanged("Item[]"). Without this
    /// notification every label in the window would keep its old language until it was rebuilt.
    /// </summary>
    [Fact]
    public void Changing_language_announces_that_every_lookup_may_have_changed()
    {
        wpf.Run(() =>
        {
            var previous = Loc.Current.Language;
            var announced = new System.Collections.Generic.List<string?>();

            void Handler(object? sender, PropertyChangedEventArgs e) => announced.Add(e.PropertyName);

            ((INotifyPropertyChanged)Loc.Current).PropertyChanged += Handler;
            try
            {
                Loc.Current.Language = AppLanguage.Chinese;
                Assert.Contains("Item[]", announced);
                Assert.Contains(nameof(Loc.Language), announced);
            }
            finally
            {
                ((INotifyPropertyChanged)Loc.Current).PropertyChanged -= Handler;
                Loc.Current.Language = previous;
            }
        });
    }

    [Fact]
    public void Setting_the_same_language_twice_announces_nothing()
    {
        wpf.Run(() =>
        {
            var raised = 0;
            void Handler(object? sender, PropertyChangedEventArgs e) => raised++;

            Loc.Current.Language = AppLanguage.English;
            ((INotifyPropertyChanged)Loc.Current).PropertyChanged += Handler;
            try
            {
                Loc.Current.Language = AppLanguage.English;
                Assert.Equal(0, raised);
            }
            finally
            {
                ((INotifyPropertyChanged)Loc.Current).PropertyChanged -= Handler;
            }
        });
    }

    /// <summary>The IsEnglish / IsChinese pair is what the segmented picker binds to.</summary>
    [Fact]
    public void The_picker_flags_track_the_language()
    {
        wpf.RunWithLanguage(AppLanguage.Chinese, () =>
        {
            Assert.True(Loc.Current.IsChinese);
            Assert.False(Loc.Current.IsEnglish);

            Loc.Current.IsEnglish = true;
            Assert.Equal(AppLanguage.English, Loc.Current.Language);
        });
    }

    /// <summary>
    /// Deselecting a segment must not leave the picker showing neither language. The setter
    /// ignores false; the UI relies on RadioButton grouping to do the switching.
    /// </summary>
    [Fact]
    public void Clearing_a_picker_flag_does_nothing()
    {
        wpf.RunWithLanguage(AppLanguage.English, () =>
        {
            Loc.Current.IsEnglish = false;
            Assert.Equal(AppLanguage.English, Loc.Current.Language);
        });
    }

    [Fact]
    public void The_launch_language_follows_the_operating_system()
    {
        var expected = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName
            .Equals("zh", StringComparison.OrdinalIgnoreCase)
            ? AppLanguage.Chinese
            : AppLanguage.English;

        Assert.Equal(expected, Loc.FromSystem());
    }
}
