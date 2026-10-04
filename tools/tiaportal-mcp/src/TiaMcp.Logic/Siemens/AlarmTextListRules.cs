using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;

namespace TiaMcpServer.Siemens
{
    internal static class AlarmTextListRules
    {
        internal static string RequireOneOf(string value, string[] allowed, string parameter) => ArgumentRules.RequireOneOf(value, allowed, parameter);
        internal static string[] ParseNames(string json, string parameter)
        {
            JsonNode? node;
            try { node = JsonNode.Parse(string.IsNullOrWhiteSpace(json) ? "[]" : json); }
            catch (Exception ex) { throw new ArgumentException(parameter + " must be a JSON array of strings: " + ex.Message); }
            if (node is not JsonArray array) throw new ArgumentException(parameter + " must be a JSON array of strings.");
            var names = array.Select(x => x is JsonValue v && v.TryGetValue<string>(out var s) ? s : throw new ArgumentException(parameter + " entries must be strings.")).ToArray();
            if (names.Any(n => string.IsNullOrWhiteSpace(n) || n.Trim() != n)) throw new ArgumentException(parameter + " entries must be exact nonempty names.");
            if (names.Distinct(StringComparer.Ordinal).Count() != names.Length) throw new ArgumentException(parameter + " contains duplicates.");
            return names;
        }

        // ---- alarm text lists XLSX (PlcAlarmTextListProvider) ----------------------------------------------------------------------------
        internal static readonly string[] XlsxActions = { "export", "import" };
        internal static readonly string[] XlsxImportOptions = { "None", "Override" };
        internal sealed class XlsxRequest { internal bool Writing; internal string[] TextLists = Array.Empty<string>(); internal string[] Cultures = Array.Empty<string>(); }
        internal static XlsxRequest ValidateXlsxRequest(string action, string filePath, string unitName, string unitKind, string textListNamesJson, string culturesJson, string importOption, bool confirmImport, bool dryRun)
        {
            RequireOneOf(action, XlsxActions, "action");
            if (string.IsNullOrWhiteSpace(filePath) || !Path.IsPathRooted(filePath) || !filePath.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Absolute filePath ending in .xlsx required.");
            if (!string.IsNullOrEmpty(unitName)) RequireOneOf(unitKind, SoftwareUnitDeepLogic.UnitKinds, "unitKind");
            var request = new XlsxRequest { TextLists = ParseNames(textListNamesJson, "textListNamesJson"), Cultures = ParseNames(culturesJson, "culturesJson") };
            if (action == "export")
            {
                if (importOption != "None" && !string.IsNullOrEmpty(importOption)) throw new ArgumentException("importOption applies to import only.");
                // Official: the filtered overload needs both lists; an empty list is not a valid filter.
                if ((request.TextLists.Length == 0) != (request.Cultures.Length == 0)) throw new ArgumentException("Filtered export needs both textListNamesJson and culturesJson (the native overload takes both); omit both to export every user text list in every project language.");
                return request;
            }
            if (request.TextLists.Length > 0 || request.Cultures.Length > 0) throw new ArgumentException("textListNamesJson / culturesJson apply to export only.");
            RequireOneOf(importOption, XlsxImportOptions, "importOption");
            if (dryRun) return request;
            if (!confirmImport) throw new ArgumentException("Real import requires confirmImport=true besides dryRun=false.");
            request.Writing = true;
            return request;
        }
    }
}
