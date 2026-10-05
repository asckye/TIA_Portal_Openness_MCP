using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace TiaDesktop.Glass
{
    // A selectable, view-only rendering of the existing timestamped log string.
    // The original text remains the source for Copy/Clear and all operation state.
    public sealed class GlassLogView : RichTextBox
    {
        public static readonly DependencyProperty LogTextProperty = DependencyProperty.Register(
            "LogText", typeof(string), typeof(GlassLogView), new PropertyMetadata("", Refresh));
        public string LogText
        {
            get { return (string)GetValue(LogTextProperty); }
            set { SetValue(LogTextProperty, value); }
        }

        public GlassLogView()
        {
            IsReadOnly = true;
            BorderThickness = new Thickness(0);
            Background = Brushes.Transparent;
            Padding = new Thickness(0);
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            SetResourceReference(FontFamilyProperty, "Ui.FontMono");
            FontSize = 11;
            Document.PagePadding = new Thickness(0);
            Document.ColumnWidth = double.PositiveInfinity;
        }

        public override void OnApplyTemplate()
        {
            base.OnApplyTemplate();
            // RichTextBox adds caret padding while attaching its document to the template.
            Document.PagePadding = new Thickness(0);
        }

        private static void Refresh(DependencyObject sender, DependencyPropertyChangedEventArgs e)
        {
            var view = (GlassLogView)sender;
            view.Document.Blocks.Clear();
            var table = new Table { CellSpacing = 0, Margin = new Thickness(0) };
            table.Columns.Add(new TableColumn { Width = new GridLength(72) });
            table.Columns.Add(new TableColumn());
            var rows = new TableRowGroup();
            foreach (string raw in (view.LogText ?? "").Split('\n'))
            {
                string line = raw.TrimEnd('\r');
                if (line.Length == 0) continue;
                bool timed = line.Length >= 9 && line[2] == ':' && line[5] == ':';
                string time = timed ? line.Substring(0, 8) : "";
                string message = timed ? line.Substring(8).TrimStart() : line;
                var stamp = new Paragraph(new Run(time)) { Margin = new Thickness(0), LineHeight = 17 };
                stamp.SetResourceReference(TextElement.ForegroundProperty, "Ui.TertiaryLabel");
                var body = new Paragraph(new Run(message)) { Margin = new Thickness(0), LineHeight = 17 };
                if (message.StartsWith("Test", StringComparison.OrdinalIgnoreCase)
                    || message.StartsWith("测试", StringComparison.Ordinal)
                    || message.Contains(" error(s), ") || message.Contains(" 个错误,"))
                    body.SetResourceReference(TextElement.ForegroundProperty, "Ui.Accent");
                var row = new TableRow();
                row.Cells.Add(new TableCell(stamp) { Padding = new Thickness(0, 0, 12, 6) });
                row.Cells.Add(new TableCell(body) { Padding = new Thickness(0, 0, 0, 6) });
                rows.Rows.Add(row);
            }
            table.RowGroups.Add(rows);
            view.Document.Blocks.Add(table);
            view.ScrollToEnd();
        }
    }
}
