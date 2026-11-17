using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;

namespace TiaMcp.Logic.Exchange
{
    public enum EmptyTableRows { Skip, TrimTrailing, Keep }

    public sealed class TableData
    {
        public string[] Columns { get; }
        public List<string[]> Rows { get; }
        public TableData(string[] columns, IEnumerable<string[]> rows) { Columns = columns; Rows = rows.ToList(); }
    }

    public static partial class TableFile
    {
        public const int MaximumBytes = 16 * 1024 * 1024, MaximumRows = 10000, MaximumColumns = 1024;
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(true, true);

        public static ArgumentException Error(string file, int row, int column, string message)
        {
            var error = new ArgumentException(file + ": row " + row + ", column " + column + ": " + message);
            error.Data["tableReason"] = message;
            error.Data["tableRow"] = row; error.Data["tableColumn"] = column;
            return error;
        }

        private static bool FormulaStart(char value) => value == '=' || value == '+' || value == '-' || value == '@' || value == '\t' || value == '\r';
        private static bool FormulaText(string value, int start = 0)
        {
            while (start < value.Length && value[start] == '\'') start++;
            return start < value.Length && FormulaStart(value[start]);
        }
        private static string ExportCell(string value) => FormulaText(value) ? "'" + value : value;
        private static string ImportCell(string value) => value.Length > 1 && value[0] == '\'' && FormulaText(value, 1) ? value.Substring(1) : value;

        public static void Validate(TableData table, string file = "table", int maximumCell = 65536, bool xmlCharacters = true)
        {
            if (table.Columns.Length == 0 || table.Columns.Length > MaximumColumns)
                throw Error(file, 1, 1, "Expected 1..1024 columns.");
            var names = new HashSet<string>(StringComparer.Ordinal);
            for (int c = 0; c < table.Columns.Length; c++)
                if (string.IsNullOrEmpty(table.Columns[c]) || !names.Add(table.Columns[c]))
                    throw Error(file, 1, c + 1, "Headers must be nonempty and unique.");
            if (table.Rows.Count > MaximumRows) throw Error(file, MaximumRows + 2, 1, "More than 10000 data rows.");
            int r = 0;
            foreach (var row in new[] { table.Columns }.Concat(table.Rows))
            {
                r++;
                if (row.Length != table.Columns.Length) throw Error(file, r, row.Length + 1, "Row width differs from header.");
                for (int c = 0; c < row.Length; c++)
                {
                    if (row[c] == null || row[c].IndexOf('\0') >= 0 || row[c].IndexOf('\ufeff') >= 0)
                        throw Error(file, r, c + 1, "Invalid CSV character.");
                    if (row[c].Length > maximumCell) throw Error(file, r, c + 1, "Cell exceeds " + maximumCell + " characters.");
                    try { if (xmlCharacters) XmlConvert.VerifyXmlChars(row[c]); }
                    catch (XmlException) { throw Error(file, r, c + 1, "XML-invalid character."); }
                }
            }
        }

        public static byte[] WriteCsv(TableData table, string file = "CSV", bool formulaGuard = true, bool xmlCharacters = true)
        {
            Validate(table, file, xmlCharacters: xmlCharacters);
            long length = Utf8.GetPreamble().Length; int row = 0;
            foreach (var cells in new[] { table.Columns }.Concat(table.Rows))
            {
                row++;
                for (int c = 0; c < cells.Length; c++)
                {
                    string value = formulaGuard ? ExportCell(cells[c]) : cells[c];
                    length += Utf8.GetByteCount(value) + value.Count(ch => ch == '"') + 3L;
                    if (length > MaximumBytes) throw Error(file, row, c + 1, "File exceeds 16 MiB.");
                }
            }
            var text = new StringBuilder();
            foreach (var cells in new[] { table.Columns }.Concat(table.Rows))
                text.Append(string.Join(",", cells.Select(c => "\"" + (formulaGuard ? ExportCell(c) : c).Replace("\"", "\"\"") + "\""))).Append('\n');
            var bytes = Utf8.GetPreamble().Concat(Utf8.GetBytes(text.ToString())).ToArray();
            return bytes;
        }

        public static TableData ReadCsv(byte[] bytes, string file = "CSV", bool formulaGuard = true,
            EmptyTableRows emptyRows = EmptyTableRows.Skip, bool xmlCharacters = true)
        {
            if (bytes.Length > MaximumBytes) throw Error(file, 1, 1, "File exceeds 16 MiB.");
            int offset = bytes.Length >= 3 && bytes[0] == 239 && bytes[1] == 187 && bytes[2] == 191 ? 3 : 0;
            string text;
            try { text = Utf8.GetString(bytes, offset, bytes.Length - offset); }
            catch (DecoderFallbackException) { throw Error(file, 1, 1, "Invalid UTF-8."); }
            var rows = new List<string[]>(); var row = new List<string>(); var cell = new StringBuilder();
            bool quoted = false, closed = false; int sourceRow = 1, pendingEmptyRows = 0;
            ArgumentException Invalid(string message) => Error(file, sourceRow, row.Count + 1, message);
            void AddRow()
            {
                bool nonempty = row.Any(value => value.Length != 0);
                if (nonempty || emptyRows == EmptyTableRows.Keep)
                {
                    if (rows.Count != 0 && row.Count != rows[0].Length) throw Invalid("Row width differs from header.");
                    if ((long)rows.Count + pendingEmptyRows > MaximumRows) throw Invalid("CSV row/cell limit exceeded.");
                    while (pendingEmptyRows > 0) { rows.Add(Enumerable.Repeat("", rows[0].Length).ToArray()); pendingEmptyRows--; }
                    rows.Add(formulaGuard ? row.Select(ImportCell).ToArray() : row.ToArray());
                }
                else if (rows.Count != 0 && emptyRows == EmptyTableRows.TrimTrailing) pendingEmptyRows++;
                row.Clear(); sourceRow++;
            }
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '\0' || c == '\ufeff') throw Invalid("Invalid CSV character.");
                if (quoted)
                {
                    if (c == '"')
                    {
                        if (i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                        else { quoted = false; closed = true; }
                    }
                    else cell.Append(c);
                }
                else if (c == '"')
                {
                    if (cell.Length != 0 || closed) throw Invalid("Invalid CSV quoting.");
                    quoted = true;
                }
                else if (c == ',' || c == '\n' || c == '\r')
                {
                    row.Add(cell.ToString()); cell.Clear(); closed = false;
                    if (row.Count > MaximumColumns) throw Invalid("Too many CSV columns.");
                    if (c != ',')
                    {
                        if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                        AddRow();
                    }
                }
                else { if (closed) throw Invalid("Unexpected text after a CSV quote."); cell.Append(c); }
                int wireMaximum = formulaGuard && cell.Length > 0 && cell[0] == '\'' ? 65537 : 65536;
                if (rows.Count > MaximumRows + 1 || cell.Length > wireMaximum) throw Invalid("CSV row/cell limit exceeded.");
            }
            if (quoted) throw Invalid("Unterminated CSV quote.");
            if (cell.Length > 0 || row.Count > 0 || closed) { row.Add(cell.ToString()); AddRow(); }
            var table = new TableData(rows.FirstOrDefault() ?? Array.Empty<string>(), rows.Skip(1));
            Validate(table, file, xmlCharacters: xmlCharacters);
            return table;
        }
    }
}
