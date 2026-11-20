using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Logic.Exchange;

namespace TiaMcp.PlcWorker;

internal static class ProjectTextProbe
{
    internal const string OtherCell = "<other>";
    // Column titles of the native project-text workbook that the repository's export/import code knows. ExportProjectTexts and
    // ImportProjectTexts pass the file through without naming any column, so the list is empty until a title is verified.
    internal static readonly string[] KnownColumnTitles = Array.Empty<string>();

    internal static Dictionary<string, object?> Scan(string? enabled, string release, string plcPath, string sheetName,
        string ownerColumn, string categoryColumn, string textListCategory, string alarmCategory,
        Func<string, HardwareAddressingReply> export, Action<string> journal, params string[] callerTitles)
    {
        var steps = new List<string>();
        var reply = new Dictionary<string, object?> { ["status"] = "disabled", ["steps"] = steps };
        if (enabled != "1") return reply;
        if (release is "14sp1" or "15.1") { reply["status"] = "unsupported"; return reply; }
        string PathOf(string path) => path.Replace('\\', '/').Trim('/');
        string plc = PathOf(plcPath);
        bool layoutOnly = ownerColumn == "" && categoryColumn == "" && textListCategory == "" && alarmCategory == "";
        if (string.IsNullOrWhiteSpace(plc) || !layoutOnly && (string.IsNullOrWhiteSpace(ownerColumn) || string.IsNullOrWhiteSpace(categoryColumn)
            || ownerColumn == categoryColumn || string.IsNullOrWhiteSpace(textListCategory) || string.IsNullOrWhiteSpace(alarmCategory)
            || textListCategory == alarmCategory)) { reply["status"] = "invalid-argument"; return reply; }
        string directory = Path.Combine(Path.GetTempPath(), "tia-project-texts-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(directory);
            string file = Path.Combine(directory, "texts.xlsx");
            try
            {
                const string step = "Project.ExportProjectTexts";
                journal(step);
                steps.Add(step);
                var exported = export(file);
                if (!exported.Meta.TryGetValue("apiCallSuccess", out var success) || !Equals(success, true))
                { reply["status"] = "export-failed"; return reply; }
                if (new FileInfo(file).Length > TableFile.MaximumBytes) throw new InvalidDataException();
                var bytes = File.ReadAllBytes(file);
                var headers = TableFile.ReadXlsxHeaders(bytes);
                // Row 1 may hold project texts. Echo only empty cells, known titles and the titles this caller supplied.
                var known = KnownColumnTitles.Concat(callerTitles).Concat(new[] { ownerColumn, categoryColumn }).Where(title => title.Length != 0).ToArray();
                reply["worksheetHeaders"] = headers.ToDictionary(sheet => sheet.Key, sheet => sheet.Value
                    .Select(cell => cell.Length == 0 || known.Contains(cell, StringComparer.Ordinal) ? cell : OtherCell).ToArray());
                reply["worksheetColumnCounts"] = headers.ToDictionary(sheet => sheet.Key, sheet => sheet.Value.Length);
                if (layoutOnly) { reply["status"] = "layout-only"; return reply; }
                if (sheetName.Length == 0 ? headers.Count != 1 : !headers.ContainsKey(sheetName))
                { reply["status"] = "select-sheet"; return reply; }
                var table = TableFile.ReadXlsx(bytes, sheetName);
                int owner = Array.IndexOf(table.Columns, ownerColumn), kind = Array.IndexOf(table.Columns, categoryColumn);
                if (owner < 0 || kind < 0) { reply["status"] = "unknown-columns"; return reply; }
                int owned = 0, textLists = 0, alarms = 0;
                foreach (var row in table.Rows)
                {
                    string path = PathOf(row[owner]);
                    if (!path.Equals(plc, StringComparison.Ordinal) && !path.StartsWith(plc + "/", StringComparison.Ordinal)) continue;
                    owned++;
                    if (row[kind] == textListCategory) textLists++;
                    if (row[kind] == alarmCategory) alarms++;
                }
                reply["ownerRows"] = owned;
                reply["textListRows"] = textLists;
                reply["alarmTextRows"] = alarms;
                reply["status"] = "ok";
            }
            finally
            {
                File.Delete(file);
                Directory.Delete(directory);
            }
        }
        catch (Exception error)
        {
            reply["status"] = "read-failed";
            reply["exceptionType"] = error.GetBaseException().GetType().FullName;
        }
        return reply;
    }
}
