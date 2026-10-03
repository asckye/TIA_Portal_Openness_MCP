using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;

namespace TiaOpenness.Gui.Localization;

/// <summary>The languages the UI ships with.</summary>
public enum AppLanguage
{
    English,
    Chinese,
}

/// <summary>
/// The string catalogue behind every visible label.
///
/// Bindings go through the indexer rather than through static resources so that switching
/// language re-evaluates them in place: WPF re-reads an indexer binding when the source
/// raises <c>PropertyChanged("Item[]")</c>, which is what <see cref="Language"/> does. A
/// <c>StaticResource</c> would need the window to be rebuilt instead.
/// </summary>
public sealed class Loc : INotifyPropertyChanged
{
    private IReadOnlyDictionary<string, string> _table = Strings.English;
    private AppLanguage _language = AppLanguage.English;

    private Loc() { }

    public static Loc Current { get; } = new();

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raised after the table has been swapped, for listeners that cache text.</summary>
    public event EventHandler? LanguageChanged;

    public AppLanguage Language
    {
        get => _language;
        set
        {
            if (_language == value) return;

            _language = value;
            _table = value == AppLanguage.Chinese ? Strings.Chinese : Strings.English;

            // "Item[]" is the WPF convention for "every indexer result may have changed".
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Language)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsEnglish)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChinese)));
            LanguageChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public bool IsEnglish
    {
        get => _language == AppLanguage.English;
        set { if (value) Language = AppLanguage.English; }
    }

    public bool IsChinese
    {
        get => _language == AppLanguage.Chinese;
        set { if (value) Language = AppLanguage.Chinese; }
    }

    /// <summary>
    /// The lookup used by every binding. An unknown key is returned bracketed rather than
    /// blank, so a typo shows up on screen instead of leaving an empty control.
    /// </summary>
    public string this[string key]
        => key is not null && _table.TryGetValue(key, out var text) ? text : "[" + key + "]";

    /// <summary>Formats a catalogue entry. Used from code where a binding cannot reach.</summary>
    public string T(string key, params object?[] args)
    {
        var format = this[key];
        if (args is null || args.Length == 0) return format;

        try
        {
            return string.Format(CultureInfo.CurrentCulture, format, args);
        }
        catch (FormatException)
        {
            // A malformed catalogue entry must not take down the operation being reported.
            return format;
        }
    }

    /// <summary>Picks the launch language from the OS, so a Chinese Windows opens in Chinese.</summary>
    public static AppLanguage FromSystem()
        => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("zh", StringComparison.OrdinalIgnoreCase)
            ? AppLanguage.Chinese
            : AppLanguage.English;
}
