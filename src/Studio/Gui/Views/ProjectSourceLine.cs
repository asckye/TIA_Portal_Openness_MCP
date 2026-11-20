using System;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;

namespace TiaOpenness.Gui.Views;

public sealed class SourceLineNumberConverter : IValueConverter
{
    public object Convert(object value, Type type, object parameter, CultureInfo culture) => value is int index ? (index + 1).ToString(culture) : "";
    public object ConvertBack(object value, Type type, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class ProjectSourceLine : TextBlock
{
    private static readonly Regex Tokens = new(
        """(?<Comment>//.*)|(?<String>'(?:\$'|[^'])*'|"(?:\$"|[^"])*")|(?<Variable>#[\w]+)|(?<Number>\b(?:\d+#[\da-f_]+|\d+(?:\.\d+)?)\b)|""" +
        @"(?<Keyword>\b(?:FUNCTION(?:_BLOCK)?|END_FUNCTION(?:_BLOCK)?|VAR(?:_INPUT|_OUTPUT|_IN_OUT|_TEMP|_STAT|_CONSTANT)?|END_VAR|" +
        @"BEGIN|END_IF|IF|ELSIF|THEN|ELSE|CASE|OF|END_CASE|FOR|TO|DO|END_FOR|WHILE|END_WHILE|RETURN|AND|OR|NOT|TRUE|FALSE|BOOL|REAL|INT|DINT|TIME)\b)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly string[] Categories = ["Keyword", "Comment", "String", "Number", "Variable"];
    public static readonly DependencyProperty SourceTextProperty = DependencyProperty.Register(nameof(SourceText), typeof(string),
        typeof(ProjectSourceLine), new PropertyMetadata("", Recolor));
    public static readonly DependencyProperty LanguageNameProperty = DependencyProperty.Register(nameof(LanguageName), typeof(string),
        typeof(ProjectSourceLine), new PropertyMetadata("", Recolor));
    public string SourceText { get => (string)GetValue(SourceTextProperty); set => SetValue(SourceTextProperty, value); }
    public string LanguageName { get => (string)GetValue(LanguageNameProperty); set => SetValue(LanguageNameProperty, value); }

    private static void Recolor(DependencyObject target, DependencyPropertyChangedEventArgs e)
    {
        var line = (ProjectSourceLine)target;
        var source = line.SourceText ?? "";
        line.Inlines.Clear();
        if (!string.Equals(line.LanguageName, "SCL", StringComparison.OrdinalIgnoreCase) || source.StartsWith("── ", StringComparison.Ordinal))
        {
            line.Text = source;
            return;
        }
        int position = 0;
        foreach (Match token in Tokens.Matches(source))
        {
            if (token.Index > position) line.Inlines.Add(new Run(source[position..token.Index]));
            var run = new Run(token.Value);
            foreach (var category in Categories)
                if (token.Groups[category].Success) { run.SetResourceReference(ForegroundProperty, "Primer.syntax" + category); break; }
            line.Inlines.Add(run);
            position = token.Index + token.Length;
        }
        if (position < source.Length) line.Inlines.Add(new Run(source[position..]));
    }
}
