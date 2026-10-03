using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using TiaOpenness.Gui.Localization;
using Xunit;

namespace TiaOpenness.Gui.Tests;

/// <summary>
/// The catalogue holds both languages as one table of triples, so "a key exists in English but
/// not in Chinese" is impossible by construction. What is still possible - and what these tests
/// are for - is a duplicated key silently overwriting an earlier one, a blank translation, or a
/// Chinese entry whose {0} placeholders do not match the English one, which throws a
/// FormatException at the moment the operation it describes finishes.
/// </summary>
public class StringsTests
{
    private static readonly Regex Placeholder = new(@"\{(\d+)[^}]*\}", RegexOptions.Compiled);

    public static TheoryData<string, string, string> Entries()
    {
        var data = new TheoryData<string, string, string>();
        foreach (var (key, en, zh) in Strings.Catalogue) data.Add(key, en, zh);
        return data;
    }

    [Fact]
    public void The_catalogue_is_not_empty()
    {
        Assert.NotEmpty(Strings.Catalogue);
    }

    [Fact]
    public void No_key_is_declared_twice()
    {
        var duplicates = Strings.Catalogue
            .GroupBy(e => e.Key, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        Assert.Empty(duplicates);
    }

    [Fact]
    public void Both_languages_expose_exactly_the_same_keys()
    {
        Assert.Equal(
            Strings.English.Keys.OrderBy(k => k, StringComparer.Ordinal),
            Strings.Chinese.Keys.OrderBy(k => k, StringComparer.Ordinal));
    }

    [Theory]
    [MemberData(nameof(Entries))]
    public void Neither_translation_is_blank(string key, string en, string zh)
    {
        Assert.False(string.IsNullOrWhiteSpace(en), key + " has no English text");
        Assert.False(string.IsNullOrWhiteSpace(zh), key + " has no Chinese text");
    }

    /// <summary>
    /// A Chinese entry may reorder its placeholders - "{1} 中共 {0} 个程序块" is better Chinese
    /// than the English order - but it must use the same set, or formatting throws at runtime.
    /// </summary>
    [Theory]
    [MemberData(nameof(Entries))]
    public void Both_translations_use_the_same_placeholders(string key, string en, string zh)
    {
        Assert.True(IndexesIn(en).SequenceEqual(IndexesIn(zh)),
            key + ": English uses {" + string.Join("},{", IndexesIn(en)) +
            "} but Chinese uses {" + string.Join("},{", IndexesIn(zh)) + "}");
    }

    [Theory]
    [MemberData(nameof(Entries))]
    public void Every_entry_formats_without_throwing(string key, string en, string zh)
    {
        var arguments = Enumerable.Range(0, HighestIndex(en) + 1).Cast<object?>().ToArray();

        // The exception this guards against is FormatException from a stray brace.
        Assert.False(string.IsNullOrEmpty(string.Format(CultureInfo.InvariantCulture, en, arguments)), key);
        Assert.False(string.IsNullOrEmpty(string.Format(CultureInfo.InvariantCulture, zh, arguments)), key);
    }

    /// <summary>
    /// An entry that is identical in both languages is usually an untranslated string that was
    /// pasted twice. The exceptions are genuinely language-neutral: the product name, the file
    /// filters that carry glob patterns, and the labels of the language picker itself.
    /// </summary>
    [Fact]
    public void Chinese_entries_are_actually_translated()
    {
        string[] allowedToBeIdentical =
        [
            // A product name, a monogram, a version pill and two format frames: none of these is
            // prose, so translating them would be inventing a difference rather than removing one.
            "App.Title", "App.Monogram", "Badge.NoVersion", "Badge.Version",
            "Lang.English", "Lang.Chinese", "Status.Working",
        ];

        var untranslated = Strings.Catalogue
            .Where(e => !allowedToBeIdentical.Contains(e.Key, StringComparer.Ordinal))
            .Where(e => string.Equals(e.En, e.Zh, StringComparison.Ordinal))
            .Select(e => e.Key)
            .ToList();

        Assert.Empty(untranslated);
    }

    /// <summary>
    /// Keys are typed by hand into XAML and matched there by a regex, so anything outside a
    /// plain dotted identifier - a stray space, a hyphen - would simply never be found.
    /// </summary>
    [Fact]
    public void Keys_are_dotted_identifiers()
    {
        var malformed = Strings.Catalogue
            .Select(e => e.Key)
            .Where(key => !Regex.IsMatch(key, @"^[A-Za-z][A-Za-z0-9]*(\.[A-Za-z][A-Za-z0-9]*)*$"))
            .ToList();

        Assert.Empty(malformed);
    }

    private static IReadOnlyList<int> IndexesIn(string format)
        => Placeholder.Matches(format)
            .Select(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture))
            .Distinct()
            .OrderBy(i => i)
            .ToList();

    private static int HighestIndex(string format)
    {
        var indexes = IndexesIn(format);
        return indexes.Count == 0 ? -1 : indexes[^1];
    }
}
