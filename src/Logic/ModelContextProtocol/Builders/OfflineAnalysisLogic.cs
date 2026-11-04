using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace TiaMcpServer.ModelContextProtocol
{
    /// <summary>
    /// Offline analysis over exported PLC block documents: SimaticML (.xml), SIMATIC SD text (.s7dcl + .s7res)
    /// and external SCL sources (.scl). Pure file logic with no Siemens.Engineering dependency, so the whole
    /// file links into the offline test project. Nothing here touches TIA Portal.
    ///
    /// Volatile-noise handling follows movioli/TIA_ProjectVersionManager (MIT), src/main/xml-normalizer.js:
    /// it strips the UId / IId / RefId attributes ("change on every export without affecting block logic"),
    /// discards DocumentInfo (Created / ExportSetting / InstalledProducts, i.e. timestamps and product
    /// versions) and diffs only an [Interface] + [Network N] canonical form with a Myers engine.
    /// This implementation is a superset: ID attributes, date-like elements, GUIDs and ISO timestamps are
    /// normalized as well, and .s7dcl MLC_* resource ids are resolved through the sibling .s7res.
    /// </summary>
    public static partial class OfflineAnalysisLogic
    {
        public const int MaxLimit = 500;
        public const int MaxDiffLinesPerSide = 60000;
        private const int MaxLineLength = 400;
        private const int MaxAnnotationText = 300;

        public static readonly IReadOnlyList<string> DefaultMarkers = Array.AsReadOnly(new string[] { "TODO", "FIXME", "HACK", "XXX", "NOTE", "BUG" });
        public static readonly IReadOnlyList<string> DefaultExtensions = Array.AsReadOnly(new string[] { ".xml", ".s7dcl", ".scl" });

        public static readonly IReadOnlyList<string> NormalizationRules = Array.AsReadOnly(new string[] {
            "xml: DocumentInfo (Created, ExportSetting, InstalledProducts) and Engineering version dropped",
            "xml: attributes ID, UId, IId, RefId removed on every element",
            "xml: elements Created, Modified, CompileDate, ModifiedDate, CreationDate, LastModified removed",
            "xml: element namespaces ignored (local names only); SCL StructuredText rendered to plain lines",
            "text: UTF-8 BOM stripped, CRLF normalized, lines trimmed, blank lines dropped",
            "text: GUIDs masked as <guid>, ISO-8601 date/time values masked as <timestamp>",
            "s7dcl: MLC_* resource ids replaced by the sibling .s7res text (or <mlc> when unresolved)",
        });

        private static readonly HashSet<string> VolatileAttributes = new HashSet<string>(StringComparer.Ordinal) { "ID", "UId", "IId", "RefId" };
        private static readonly HashSet<string> VolatileElements = new HashSet<string>(StringComparer.Ordinal)
            { "DocumentInfo", "Engineering", "Created", "Modified", "CompileDate", "ModifiedDate", "CreationDate", "LastModified" };

        private static readonly Regex GuidRegex =
            new Regex(@"\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b", RegexOptions.Compiled);
        private static readonly Regex TimestampRegex =
            new Regex(@"\b\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}:\d{2}(\.\d+)?(Z|[+-]\d{2}:\d{2})?\b", RegexOptions.Compiled);
        private static readonly Regex MlcRegex = new Regex(@"\bMLC_[A-Za-z0-9]+\b", RegexOptions.Compiled);
        private static readonly Regex DeclarationRegex =
            new Regex(@"^(FUNCTION_BLOCK|FUNCTION|ORGANIZATION_BLOCK|DATA_BLOCK|TYPE)\s+""([^""]+)""(\s*:\s*([^\s{]+))?", RegexOptions.Compiled | RegexOptions
                .IgnoreCase);
        private static readonly Regex MemberRegex = new Regex(@"^(""[^""]+""|[A-Za-z_][\w]*)\s*(\{[^}]*\})?\s*:\s*(.+?)\s*$", RegexOptions.Compiled);
        private static readonly Regex AttributePairRegex = new Regex(@"([A-Za-z_][\w]*)\s*:=\s*(""[^""]*""|'[^']*'|[^;,}]+)", RegexOptions.Compiled);
        private static readonly Regex QuotedCallRegex = new Regex(@"""([^""]+)""\s*(\{[^}]*\})?\s*\(", RegexOptions.Compiled);
        private static readonly Regex InstanceCallRegex = new Regex(@"#([A-Za-z_][\w]*)(?:\.([A-Za-z_][\w]*))?\s*(\{[^}]*\})?\s*\(", RegexOptions.Compiled);
        private static readonly Regex BareCallRegex = new Regex(@"(?<![#""\w.])([A-Za-z_][\w]*)\s*(\{[^}]*\})?\s*\(", RegexOptions.Compiled);
        private static readonly Regex NestOpenRegex = new Regex(@"\b(IF|CASE|FOR|WHILE|REPEAT)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex NestCloseRegex = new Regex(@"\bEND_(IF|CASE|FOR|WHILE|REPEAT)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly HashSet<string> SclKeywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "IF", "ELSIF", "ELSE", "THEN", "CASE", "OF", "FOR", "TO", "BY", "DO", "WHILE", "REPEAT", "UNTIL", "RETURN", "EXIT", "CONTINUE",
            "NOT", "AND", "OR", "XOR", "MOD", "TRUE", "FALSE", "GOTO", "REGION", "END_REGION", "RUNG", "END_RUNG", "NETWORK", "END_NETWORK",
            "FUNCTION", "FUNCTION_BLOCK", "ORGANIZATION_BLOCK", "DATA_BLOCK", "TYPE", "VAR", "END_VAR", "BEGIN", "STRUCT", "END_STRUCT", "ARRAY",
        };

        public sealed class InterfaceMember
        {
            public string Section = "";
            public string Path = "";
            public string Datatype = "";
        }

        public sealed class NetworkInfo
        {
            public int Index;
            public string Title = "";
            public string Comment = "";
            public string Language = "";
            public List<string> SourceLines = new List<string>();
        }

        public sealed class BlockDocument
        {
            public string SourcePath = "";
            public string Format = "";      // SimaticML | S7DCL | SCL
            public string BlockName = "";
            public string BlockType = "";   // FC | FB | OB | GlobalDB | InstanceDB | PlcStruct | PlcTagTable | ...
            public string Language = "";
            public string Title = "";
            public string Comment = "";
            public SortedDictionary<string, string> Attributes = new SortedDictionary<string, string>(StringComparer.Ordinal);
            public List<InterfaceMember> Members = new List<InterfaceMember>();
            public List<NetworkInfo> Networks = new List<NetworkInfo>();
            public List<string> Canonical = new List<string>();
            public List<string> BlockCalls = new List<string>();     // "FC:Name" / "FB:Name" / "Instance:Name.Type"
            public List<string> Instructions = new List<string>();   // instruction / part names, one entry per occurrence
            public int SourceLines;
            public int CommentLines;
            public int? MaxNesting;
            public List<string> Warnings = new List<string>();
        }

        public sealed class AnnotationRow
        {
            public string File = "";
            public string BlockName = "";
            public int? Network;
            public int Line;
            public string Marker = "";
            public string Text = "";
            public string Kind = "";
        }

        public enum DiffOp { Equal, Delete, Insert }

        public struct DiffEntry
        {
            public DiffOp Op;
            public string Text;
            public int LeftLine;   // 1-based, 0 when the entry has no left line
            public int RightLine;  // 1-based, 0 when the entry has no right line
        }

        // ------------------------------------------------------------------ parsing

        public static BlockDocument ParseFile(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path))
                throw new ArgumentException("Absolute document file path required: '" + path + "'.");
            if (Directory.Exists(path)) throw new ArgumentException("Path is a directory, a document file is required: '" + path + "'.");
            if (!File.Exists(path)) throw new FileNotFoundException("Document not found: " + path, path);
            var text = File.ReadAllText(path, Encoding.UTF8);
            string? s7res = null;
            var resPath = Path.ChangeExtension(path, ".s7res");
            if (!string.Equals(resPath, path, StringComparison.OrdinalIgnoreCase) && File.Exists(resPath)) s7res = File.ReadAllText(resPath, Encoding.UTF8);
            return ParseText(text, path, s7res);
        }

        public static BlockDocument ParseText(string text, string sourcePath, string? s7resText = null)
        {
            var body = (text ?? "").TrimStart('﻿');
            var ext = Path.GetExtension(sourcePath ?? "").ToLowerInvariant();
            BlockDocument doc;
            if (LooksLikeXml(body)) doc = ParseSimaticMl(body);
            else doc =
                ParseTextSource(body, ReadS7Res(s7resText), ext == ".scl" ? "SCL" : ext == ".s7dcl" ? "S7DCL"
                    : (body.IndexOf("NETWORK", StringComparison.Ordinal) >= 0 ? "S7DCL" : "SCL"));
            doc.SourcePath = sourcePath ?? "";
            return doc;
        }

        public static bool LooksLikeXml(string text)
        {
            var i = 0;
            while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
            return i < text.Length && text[i] == '<';
        }

        // .s7res is YAML: "MultiLingualTexts:" / "  - id: MLC_x" / "    zh-CN: text". First text per id wins.
        public static Dictionary<string, string> ReadS7Res(string? s7resText)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            if (string.IsNullOrWhiteSpace(s7resText)) return map;
            string? current = null;
            foreach (var raw in s7resText!.Split('\n'))
            {
                var line = raw.TrimStart('﻿').TrimEnd('\r').TrimEnd();
                var trimmed = line.TrimStart();
                if (trimmed.Length == 0 || trimmed[0] == '#') continue;
                if (trimmed.StartsWith("-", StringComparison.Ordinal))
                {
                    var rest = trimmed.Substring(1).TrimStart();
                    if (rest.StartsWith("id:", StringComparison.OrdinalIgnoreCase)) current = rest.Substring(3).Trim().Trim('"', '\'');
                    else current = null;
                    continue;
                }
                if (current == null) continue;
                var colon = trimmed.IndexOf(':');
                if (colon <= 0) continue;
                var value = trimmed.Substring(colon + 1).Trim();
                if (value.Length >= 2 && (value[0] == '\'' && value[value.Length - 1] == '\'' || value[0] == '"' && value[value.Length - 1] == '"'))
                    value = value.Substring(1, value.Length - 2).Replace("''", "'");
                if (!map.ContainsKey(current) && value.Length > 0) map[current] = value;
            }
            return map;
        }
    }
}
