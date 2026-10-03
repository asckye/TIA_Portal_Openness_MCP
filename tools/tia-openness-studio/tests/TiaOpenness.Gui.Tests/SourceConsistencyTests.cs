using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using TiaOpenness.Gui.Localization;
using Xunit;

namespace TiaOpenness.Gui.Tests;

/// <summary>
/// Cross-checks the UI's source against the two tables it depends on.
///
/// Both failures these catch are invisible at build time and nearly invisible at run time: a
/// mistyped catalogue key renders as "[Toolbar.Doctr]" on a button nobody clicked during
/// testing, and a mistyped resource key leaves a control with no brush at all, which in a dark
/// palette can be an invisible-on-invisible control rather than an obvious blank.
/// </summary>
[Collection(WpfCollection.Name)]
public class SourceConsistencyTests(WpfContext wpf)
{
    // {l:Tr Toolbar.Doctor}
    private static readonly Regex MarkupKey = new(@"\{l:Tr\s+([A-Za-z][\w.]*)", RegexOptions.Compiled);

    // Loc.Current["X"], Loc.Current.T("X", ...), SetStatus("X", ...), Guarded("X", ...),
    // LocalizedText.Key("X"), LocalizedText.Working("X")
    private static readonly Regex[] CodeKeys =
    [
        new(@"Loc\.Current\[""([A-Za-z][\w.]*)""\]", RegexOptions.Compiled),
        new(@"Loc\.Current\.T\(""([A-Za-z][\w.]*)""", RegexOptions.Compiled),
        new(@"SetStatus\(""([A-Za-z][\w.]*)""", RegexOptions.Compiled),
        new(@"Guarded\(""([A-Za-z][\w.]*)""", RegexOptions.Compiled),
        new(@"LocalizedText\.(?:Key|Working)\(""([A-Za-z][\w.]*)""", RegexOptions.Compiled),
    ];

    // {StaticResource Ui.Button} / {DynamicResource Ui.Accent}
    private static readonly Regex ResourceKey =
        new(@"\{(?:Static|Dynamic)Resource\s+([A-Za-z][\w.]*)\s*\}", RegexOptions.Compiled);

    [Fact]
    public void Every_key_used_in_the_markup_exists_in_the_catalogue()
    {
        var missing = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var file in SourceScan.Markup)
        {
            foreach (Match match in MarkupKey.Matches(file.Text))
            {
                var key = match.Groups[1].Value;
                if (!Strings.English.ContainsKey(key)) missing.Add(file.Name + ": " + key);
            }
        }

        Assert.Empty(missing);
    }

    [Fact]
    public void Every_key_used_from_code_exists_in_the_catalogue()
    {
        var missing = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var file in SourceScan.Code)
        {
            foreach (var pattern in CodeKeys)
            {
                foreach (Match match in pattern.Matches(file.Text))
                {
                    var key = match.Groups[1].Value;
                    if (!Strings.English.ContainsKey(key)) missing.Add(file.Name + ": " + key);
                }
            }
        }

        Assert.Empty(missing);
    }

    /// <summary>
    /// Guards against the scan quietly matching nothing - a broken regex would otherwise turn
    /// both tests above into permanent green.
    /// </summary>
    [Fact]
    public void The_scan_actually_finds_keys_to_check()
    {
        var markupKeys = SourceScan.Markup
            .SelectMany(f => MarkupKey.Matches(f.Text).Select(m => m.Groups[1].Value))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var codeKeys = SourceScan.Code
            .SelectMany(f => CodeKeys.SelectMany(p => p.Matches(f.Text).Select(m => m.Groups[1].Value)))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        Assert.True(markupKeys.Count > 40, "only found " + markupKeys.Count + " keys in the markup");
        Assert.True(codeKeys.Count > 20, "only found " + codeKeys.Count + " keys in the code");
    }

    /// <summary>
    /// Every brush, style, template and geometry the markup names has to be reachable from the
    /// application's resources - which is where the running app looks for it.
    /// </summary>
    [Fact]
    public void Every_resource_the_markup_names_can_be_found()
    {
        wpf.Run(() =>
        {
            var missing = new SortedSet<string>(StringComparer.Ordinal);

            foreach (var file in SourceScan.Markup)
            {
                foreach (Match match in ResourceKey.Matches(file.Text))
                {
                    var key = match.Groups[1].Value;
                    if (Application.Current.TryFindResource(key) is null)
                    {
                        missing.Add(file.Name + ": " + key);
                    }
                }
            }

            Assert.Empty(missing);
        });
    }

    /// <summary>
    /// A catalogue entry nobody reads is dead weight that still has to be translated. The
    /// language labels are the exception: they are read by the picker's own bindings, which
    /// this scan sees, but they are listed here so the intent is explicit rather than lucky.
    /// </summary>
    [Fact]
    public void The_catalogue_has_no_unused_entries()
    {
        var used = new HashSet<string>(StringComparer.Ordinal);

        foreach (var file in SourceScan.Markup)
        {
            foreach (Match match in MarkupKey.Matches(file.Text)) used.Add(match.Groups[1].Value);
        }

        foreach (var file in SourceScan.Code)
        {
            foreach (var pattern in CodeKeys)
            {
                foreach (Match match in pattern.Matches(file.Text)) used.Add(match.Groups[1].Value);
            }
        }

        // Built by LocalizedText.Working rather than named at any call site.
        used.Add("Status.Working");

        var unused = Strings.Catalogue
            .Select(e => e.Key)
            .Where(key => !used.Contains(key))
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToList();

        Assert.Empty(unused);
    }
}
