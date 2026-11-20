using System;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Controls;
using TiaOpenness.Gui.ViewModels;

namespace TiaOpenness.Gui.Views;

public partial class CallDetailDrawer : UserControl
{
    public CallDetailDrawer() { InitializeComponent(); DataContext = null; }
    public static readonly DependencyProperty JsonPreviewProperty = DependencyProperty.RegisterAttached(
        "JsonPreview", typeof(string), typeof(CallDetailDrawer), new PropertyMetadata("", OnJsonPreviewChanged));

    public static string GetJsonPreview(TextBlock text) => (string)text.GetValue(JsonPreviewProperty);
    public static void SetJsonPreview(TextBlock text, string value) => text.SetValue(JsonPreviewProperty, value);

    private static void OnJsonPreviewChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        var text = (TextBlock)sender;
        string source = (string?)e.NewValue ?? "";
        // Keep the plain-text value available to automation even before the control is loaded.
        text.Text = source;
        text.Inlines.Clear();
        byte[] bytes = Encoding.UTF8.GetBytes(source);
        var reader = new Utf8JsonReader(bytes);
        int position = 0;
        try
        {
            while (reader.Read())
            {
                string? brush = reader.TokenType switch
                {
                    JsonTokenType.PropertyName => "Primer.syntaxKeyword",
                    JsonTokenType.String => "Primer.syntaxString",
                    JsonTokenType.Number or JsonTokenType.False or JsonTokenType.True or JsonTokenType.Null => "Primer.syntaxNumber",
                    _ => null,
                };
                if (brush == null) continue;
                int start = (int)reader.TokenStartIndex;
                if (start > position) text.Inlines.Add(new Run(Encoding.UTF8.GetString(bytes, position, start - position)));
                // PropertyName consumes the colon too. Only the quoted name takes the keyword colour.
                int length = reader.TokenType == JsonTokenType.PropertyName ? reader.ValueSpan.Length + 2 : (int)reader.BytesConsumed - start;
                var run = new Run(Encoding.UTF8.GetString(bytes, start, length));
                run.SetResourceReference(TextElement.ForegroundProperty, brush);
                text.Inlines.Add(run);
                position = start + length;
            }
            if (position < bytes.Length) text.Inlines.Add(new Run(Encoding.UTF8.GetString(bytes.AsSpan(position))));
        }
        catch (JsonException) /* swallow(parse-fallback): preserve the complete redacted preview when a journal entry is truncated. */
        {
            text.Inlines.Clear();
            text.Inlines.Add(new Run(source));
        }
    }

    internal void SetControlActivity(string text) => ControlActivity.Text = text;
    private void OnCopy(object sender, RoutedEventArgs e)
    {
        var model = (FeaturePagesViewModel)DataContext;
        if (model.SelectedCall is { } call) model.Copy(call.Parameters);
    }
}
