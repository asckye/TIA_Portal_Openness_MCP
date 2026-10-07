using System;
using System.Linq;
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
            IsVisibleChanged += (_, _) => { if (IsVisible) Schedule(); };
            Loaded += (_, _) => Schedule();
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

        private string _rendered = "";
        private TableRowGroup? _rows;
        private bool _queued;
        private static void Refresh(DependencyObject sender, DependencyPropertyChangedEventArgs e) => ((GlassLogView)sender).Schedule();
        private void Schedule()
        {
            if (_queued || IsLoaded && !IsVisible) return;
            _queued = true;
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(FlushPending));
        }
        internal void FlushPending()
        {
            _queued = false;
            string text = LogText ?? "";
            int drop = text.Count(c => c == '\n') - 1000, start = 0;
            while (drop-- > 0) start = text.IndexOf('\n', start) + 1;
            if (start > 0) text = text[start..];
            if (text == _rendered) return;
            bool follow = VerticalOffset >= ExtentHeight - ViewportHeight - 2;
            int retained = 0, removed = 0;
            if (_rows != null && text.StartsWith(_rendered, StringComparison.Ordinal)) retained = _rendered.Length;
            else if (_rows != null && text.Length > 0)
            {
                int end = text.IndexOf('\n');
                string first = end < 0 ? text : text[..(end + 1)];
                int offset = _rendered.IndexOf(first, StringComparison.Ordinal);
                if (offset >= 0 && text.StartsWith(_rendered[offset..], StringComparison.Ordinal))
                {
                    removed = _rendered[..offset].Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
                    retained = _rendered.Length - offset;
                }
                else _rows = null;
            }
            else _rows = null;
            if (_rows == null)
            {
                Document.Blocks.Clear();
                var table = new Table { CellSpacing = 0, Margin = new Thickness(0) };
                table.Columns.Add(new TableColumn { Width = new GridLength(72) }); table.Columns.Add(new TableColumn());
                _rows = new TableRowGroup(); table.RowGroups.Add(_rows); Document.Blocks.Add(table);
            }
            while (removed-- > 0 && _rows.Rows.Count > 0) _rows.Rows.RemoveAt(0);
            foreach (string raw in text[retained..].Split('\n'))
            {
                string line = raw.TrimEnd('\r');
                if (line.Length == 0) continue;
                bool timed = line.Length >= 9 && line[2] == ':' && line[5] == ':';
                string time = timed ? line.Substring(0, 8) : "";
                string message = timed ? line.Substring(8).TrimStart() : line;
                var stamp = new Paragraph(new Run(time)) { Margin = new Thickness(0), LineHeight = 17 };
                stamp.SetResourceReference(TextElement.ForegroundProperty, "Ui.TertiaryLabel");
                var body = new Paragraph(new Run(message)) { Margin = new Thickness(0), LineHeight = 17 };
                var row = new TableRow();
                row.Cells.Add(new TableCell(stamp) { Padding = new Thickness(0, 0, 12, 6) });
                row.Cells.Add(new TableCell(body) { Padding = new Thickness(0, 0, 0, 6) }); _rows.Rows.Add(row);
            }
            while (_rows.Rows.Count > 1000) _rows.Rows.RemoveAt(0);
            _rendered = text;
            if (follow) Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(ScrollToEnd));
        }
    }
}
