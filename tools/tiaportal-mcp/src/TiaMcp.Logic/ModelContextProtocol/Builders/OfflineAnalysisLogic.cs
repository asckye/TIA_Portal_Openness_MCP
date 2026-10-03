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
    public static class OfflineAnalysisLogic
    {
        public const int MaxLimit = 500;
        public const int MaxDiffLinesPerSide = 60000;
        private const int MaxLineLength = 400;
        private const int MaxAnnotationText = 300;

        public static readonly string[] DefaultMarkers = { "TODO", "FIXME", "HACK", "XXX", "NOTE", "BUG" };
        public static readonly string[] DefaultExtensions = { ".xml", ".s7dcl", ".scl" };

        public static readonly string[] NormalizationRules =
        {
            "xml: DocumentInfo (Created, ExportSetting, InstalledProducts) and Engineering version dropped",
            "xml: attributes ID, UId, IId, RefId removed on every element",
            "xml: elements Created, Modified, CompileDate, ModifiedDate, CreationDate, LastModified removed",
            "xml: element namespaces ignored (local names only); SCL StructuredText rendered to plain lines",
            "text: UTF-8 BOM stripped, CRLF normalized, lines trimmed, blank lines dropped",
            "text: GUIDs masked as <guid>, ISO-8601 date/time values masked as <timestamp>",
            "s7dcl: MLC_* resource ids replaced by the sibling .s7res text (or <mlc> when unresolved)",
        };

        private static readonly HashSet<string> VolatileAttributes = new HashSet<string>(StringComparer.Ordinal) { "ID", "UId", "IId", "RefId" };
        private static readonly HashSet<string> VolatileElements = new HashSet<string>(StringComparer.Ordinal)
            { "DocumentInfo", "Engineering", "Created", "Modified", "CompileDate", "ModifiedDate", "CreationDate", "LastModified" };

        private static readonly Regex GuidRegex = new Regex(@"\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b", RegexOptions.Compiled);
        private static readonly Regex TimestampRegex = new Regex(@"\b\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}:\d{2}(\.\d+)?(Z|[+-]\d{2}:\d{2})?\b", RegexOptions.Compiled);
        private static readonly Regex MlcRegex = new Regex(@"\bMLC_[A-Za-z0-9]+\b", RegexOptions.Compiled);
        private static readonly Regex DeclarationRegex = new Regex(@"^(FUNCTION_BLOCK|FUNCTION|ORGANIZATION_BLOCK|DATA_BLOCK|TYPE)\s+""([^""]+)""(\s*:\s*([^\s{]+))?", RegexOptions.Compiled | RegexOptions.IgnoreCase);
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
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path)) throw new ArgumentException("Absolute document file path required: '" + path + "'.");
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
            else doc = ParseTextSource(body, ReadS7Res(s7resText), ext == ".scl" ? "SCL" : ext == ".s7dcl" ? "S7DCL" : (body.IndexOf("NETWORK", StringComparison.Ordinal) >= 0 ? "S7DCL" : "SCL"));
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

        private static string Local(XElement e) => e.Name.LocalName;

        private static XElement? Child(XElement? e, string local) => e?.Elements().FirstOrDefault(x => Local(x) == local);

        private static BlockDocument ParseSimaticMl(string xml)
        {
            var doc = new BlockDocument { Format = "SimaticML" };
            var xdoc = XDocument.Parse(xml, LoadOptions.None);
            var root = xdoc.Root ?? throw new InvalidDataException("Empty XML document.");
            var block = root.Elements().FirstOrDefault(e => Local(e).StartsWith("SW.", StringComparison.Ordinal))
                        ?? (Local(root).StartsWith("SW.", StringComparison.Ordinal) ? root : null);
            if (block == null)
            {
                doc.Warnings.Add("No SW.* element found; document treated as generic XML (no interface/network structure).");
                doc.BlockType = Local(root);
                doc.Canonical = MaskLines(StrippedXmlLines(root));
                doc.SourceLines = doc.Canonical.Count;
                return doc;
            }

            var typeName = Local(block);
            doc.BlockType = typeName.StartsWith("SW.Blocks.", StringComparison.Ordinal) ? typeName.Substring("SW.Blocks.".Length)
                : typeName.StartsWith("SW.Types.", StringComparison.Ordinal) ? typeName.Substring("SW.Types.".Length)
                : typeName.StartsWith("SW.Tags.", StringComparison.Ordinal) ? typeName.Substring("SW.Tags.".Length)
                : typeName;

            var attributeList = Child(block, "AttributeList");
            if (attributeList != null)
            {
                foreach (var child in attributeList.Elements())
                {
                    var name = Local(child);
                    if (name == "Interface") { ParseXmlInterface(child, doc); continue; }
                    if (name == "Name") { doc.BlockName = child.Value.Trim(); continue; }
                    if (name == "ProgrammingLanguage") { doc.Language = child.Value.Trim(); continue; }
                    if (VolatileElements.Contains(name)) continue;
                    if (!child.HasElements) doc.Attributes[name] = child.Value.Trim();
                }
            }

            var objectList = Child(block, "ObjectList");
            if (objectList != null)
            {
                foreach (var ml in objectList.Elements().Where(e => Local(e) == "MultilingualText"))
                {
                    var composition = (string?)ml.Attribute("CompositionName") ?? "";
                    if (composition == "Title") doc.Title = MultilingualTextValue(ml);
                    else if (composition == "Comment") doc.Comment = MultilingualTextValue(ml);
                }
            }

            var index = 0;
            foreach (var unit in block.Descendants().Where(e => Local(e) == "SW.Blocks.CompileUnit"))
            {
                var net = new NetworkInfo { Index = ++index };
                var unitAttributes = Child(unit, "AttributeList");
                net.Language = Child(unitAttributes, "ProgrammingLanguage")?.Value.Trim() ?? "";
                var unitObjects = Child(unit, "ObjectList");
                if (unitObjects != null)
                {
                    foreach (var ml in unitObjects.Elements().Where(e => Local(e) == "MultilingualText"))
                    {
                        var composition = (string?)ml.Attribute("CompositionName") ?? "";
                        if (composition == "Title") net.Title = MultilingualTextValue(ml);
                        else if (composition == "Comment") net.Comment = MultilingualTextValue(ml);
                    }
                }
                var source = Child(unitAttributes, "NetworkSource")?.Elements().FirstOrDefault();
                if (source != null)
                {
                    if (Local(source) == "StructuredText")
                    {
                        var rendered = RenderStructuredText(source, out var commentLines);
                        net.SourceLines = rendered;
                        doc.CommentLines += commentLines;
                        doc.SourceLines += rendered.Count(l => l.Trim().Length > 0);
                        var code = rendered.Select(l => StripCommentsSimple(l)).ToList();
                        doc.MaxNesting = Math.Max(doc.MaxNesting ?? 0, MaxNesting(code));
                        CollectTextCalls(code, doc);
                    }
                    else
                    {
                        // Graphical networks: the stripped FlgNet XML is diffable but is not "source lines".
                        net.SourceLines = StrippedXmlLines(source);
                    }
                    foreach (var call in source.Descendants().Where(e => Local(e) == "CallInfo"))
                    {
                        var callName = (string?)call.Attribute("Name") ?? "";
                        var blockType = (string?)call.Attribute("BlockType") ?? "";
                        if (callName.Length > 0 && Local(source) != "StructuredText") doc.BlockCalls.Add(blockType + ":" + callName);
                    }
                    foreach (var part in source.Descendants().Where(e => Local(e) == "Part" || (Local(e) == "Instruction" && Local(source) != "StructuredText")))
                    {
                        var partName = (string?)part.Attribute("Name") ?? "";
                        if (partName.Length > 0) doc.Instructions.Add(partName);
                    }
                }
                doc.Networks.Add(net);
            }

            BuildCanonical(doc);
            return doc;
        }

        private static void ParseXmlInterface(XElement interfaceElement, BlockDocument doc)
        {
            foreach (var section in interfaceElement.Descendants().Where(e => Local(e) == "Section"))
            {
                var sectionName = (string?)section.Attribute("Name") ?? "";
                foreach (var member in section.Elements().Where(e => Local(e) == "Member")) AddXmlMember(sectionName, "", member, doc);
            }
        }

        private static void AddXmlMember(string section, string prefix, XElement member, BlockDocument doc)
        {
            var name = (string?)member.Attribute("Name") ?? "";
            var path = prefix.Length == 0 ? name : prefix + "." + name;
            doc.Members.Add(new InterfaceMember { Section = section, Path = path, Datatype = ((string?)member.Attribute("Datatype") ?? "").Trim() });
            foreach (var child in member.Elements().Where(e => Local(e) == "Member")) AddXmlMember(section, path, child, doc);
        }

        private static string MultilingualTextValue(XElement multilingualText)
        {
            var texts = multilingualText.Descendants().Where(e => Local(e) == "Text").Select(e => e.Value.Trim()).Where(t => t.Length > 0).ToList();
            return string.Join(" | ", texts);
        }

        // Renders the StructuredText/v4 token stream back to source lines (diff/metric view, not re-importable).
        public static List<string> RenderStructuredText(XElement structuredText, out int commentLines)
        {
            var sb = new StringBuilder();
            var comments = 0;
            RenderNode(structuredText, sb, ref comments, null);
            commentLines = comments;
            return sb.ToString().Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        }

        private static void RenderNode(XElement e, StringBuilder sb, ref int comments, string? scope)
        {
            switch (Local(e))
            {
                case "Token": sb.Append((string?)e.Attribute("Text") ?? ""); return;
                case "Text": sb.Append(e.Value); return;   // REGION names and other raw text at StructuredText level
                case "PredefinedVariable": sb.Append((string?)e.Attribute("Name") ?? "ENO"); return;   // SCL: ENO := …
                case "Blank":
                    var num = (string?)e.Attribute("Num");
                    sb.Append(' ', int.TryParse(num, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : 1);
                    return;
                case "NewLine": sb.Append('\n'); return;
                case "LineComment": sb.Append("// ").Append(CollapseWhitespace(e.Value)); comments++; return;
                case "Comment": sb.Append("(* ").Append(CollapseWhitespace(e.Value)).Append(" *)"); comments++; return;
                case "Access":
                    foreach (var child in e.Elements()) RenderNode(child, sb, ref comments, (string?)e.Attribute("Scope"));
                    return;
                case "Symbol":
                    var first = true;
                    foreach (var child in e.Elements())
                    {
                        if (Local(child) == "Component" && first)
                        {
                            var componentName = (string?)child.Attribute("Name") ?? "";
                            if (scope == "LocalVariable" || scope == "LocalConstant") sb.Append('#').Append(componentName);
                            else if (scope == "GlobalVariable" || scope == "GlobalConstant") sb.Append('"').Append(componentName).Append('"');
                            else sb.Append(componentName);
                            foreach (var sub in child.Elements()) RenderNode(sub, sb, ref comments, scope);
                            first = false;
                            continue;
                        }
                        RenderNode(child, sb, ref comments, scope);
                    }
                    return;
                case "Component":
                    sb.Append((string?)e.Attribute("Name") ?? "");
                    foreach (var child in e.Elements()) RenderNode(child, sb, ref comments, scope);
                    return;
                case "Constant":
                {
                    // LocalConstant / GlobalConstant carry only a Name (no ConstantValue): render it as the source does.
                    var constantName = (string?)e.Attribute("Name");
                    if (!string.IsNullOrEmpty(constantName))
                    {
                        sb.Append(scope == "GlobalConstant" ? "\"" + constantName + "\"" : "#" + constantName);
                        return;
                    }
                    foreach (var child in e.Elements()) RenderNode(child, sb, ref comments, scope);
                    return;
                }
                case "ConstantValue": sb.Append(e.Value.Trim()); return;
                case "ConstantType": return;
                case "IntegerAttribute": case "BooleanAttribute": case "StringAttribute": case "DateAttribute": return;   // informative metadata, not source
                case "CallInfo":
                    var callName = (string?)e.Attribute("Name") ?? "";
                    var blockType = (string?)e.Attribute("BlockType") ?? "";
                    if (blockType == "FC" || blockType == "FB") sb.Append('"').Append(callName).Append('"'); else sb.Append(callName);
                    foreach (var child in e.Elements()) RenderNode(child, sb, ref comments, scope);
                    return;
                case "Instruction":
                    sb.Append((string?)e.Attribute("Name") ?? "");
                    foreach (var child in e.Elements()) RenderNode(child, sb, ref comments, scope);
                    return;
                case "Parameter":
                    sb.Append((string?)e.Attribute("Name") ?? "");
                    foreach (var child in e.Elements()) RenderNode(child, sb, ref comments, scope);
                    return;
                default:
                    foreach (var child in e.Elements()) RenderNode(child, sb, ref comments, scope);
                    return;
            }
        }

        private static string CollapseWhitespace(string text) => Regex.Replace(text ?? "", @"\s+", " ").Trim();

        // Volatile attributes/elements removed, then serialized without namespaces, one trimmed line per element line.
        public static List<string> StrippedXmlLines(XElement element)
        {
            var clone = new XElement(element);
            StripVolatile(clone);
            var text = clone.ToString(SaveOptions.None);
            return text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        }

        private static void StripVolatile(XElement element)
        {
            foreach (var volatileChild in element.Descendants().Where(e => VolatileElements.Contains(Local(e))).ToList()) volatileChild.Remove();
            foreach (var e in element.DescendantsAndSelf())
            {
                foreach (var attr in e.Attributes().Where(a => VolatileAttributes.Contains(a.Name.LocalName) || a.IsNamespaceDeclaration).ToList()) attr.Remove();
                e.Name = XName.Get(e.Name.LocalName);
            }
        }

        private static BlockDocument ParseTextSource(string text, Dictionary<string, string> resources, string format)
        {
            var doc = new BlockDocument { Format = format };
            var lines = text.Replace("\r\n", "\n").Split('\n');
            var inBlockComment = false;
            var declared = false;
            string? section = null;
            var structPrefix = new List<string>();
            NetworkInfo? current = null;
            var implicitBody = false;
            var pendingAttributes = new Dictionary<string, string>(StringComparer.Ordinal);
            var attributeBuffer = new StringBuilder();
            var inAttributeBlock = false;
            var networkIndex = 0;
            var codeLines = new List<string>();
            var canonical = new List<string>();

            string Resolve(string value)
            {
                var v = value.Trim().Trim('"', '\'');
                return MlcRegex.IsMatch(v) ? MlcRegex.Replace(v, m => resources.TryGetValue(m.Value, out var r) ? r : "<mlc>") : v;
            }

            for (var i = 0; i < lines.Length; i++)
            {
                var raw = lines[i].TrimEnd();
                var code = StripComments(raw, ref inBlockComment, out var comment);
                var codeTrim = code.Trim();
                var hasComment = comment.Trim().Length > 0;

                var canonicalLine = MlcRegex.Replace(raw.Trim(), m => resources.TryGetValue(m.Value, out var r) ? "\"" + r + "\"" : "<mlc>");
                if (canonicalLine.Length > 0) canonical.Add(Mask(canonicalLine));

                if (current != null && (hasComment || codeTrim.Length > 0))
                {
                    if (!codeTrim.StartsWith("END_NETWORK", StringComparison.OrdinalIgnoreCase)
                        && !(implicitBody && (codeTrim.StartsWith("END_FUNCTION", StringComparison.OrdinalIgnoreCase) || codeTrim.StartsWith("END_ORGANIZATION", StringComparison.OrdinalIgnoreCase) || codeTrim.StartsWith("END_DATA_BLOCK", StringComparison.OrdinalIgnoreCase))))
                    {
                        current.SourceLines.Add(raw.Trim());
                        doc.SourceLines++;
                        if (hasComment) doc.CommentLines++;
                        if (codeTrim.Length > 0 && !codeTrim.StartsWith("{", StringComparison.Ordinal)) codeLines.Add(codeTrim);
                    }
                }

                if (inAttributeBlock)
                {
                    attributeBuffer.Append(' ').Append(code);
                    if (code.IndexOf('}') >= 0)
                    {
                        inAttributeBlock = false;
                        ApplyAttributeBlock(attributeBuffer.ToString(), doc, pendingAttributes, declared, current != null, Resolve);
                        attributeBuffer.Clear();
                    }
                    continue;
                }
                if (codeTrim.StartsWith("{", StringComparison.Ordinal))
                {
                    if (codeTrim.IndexOf('}') >= 0) ApplyAttributeBlock(codeTrim, doc, pendingAttributes, declared, current != null, Resolve);
                    else { inAttributeBlock = true; attributeBuffer.Append(codeTrim); }
                    continue;
                }
                if (codeTrim.Length == 0) continue;

                if (!declared)
                {
                    var decl = DeclarationRegex.Match(codeTrim);
                    if (decl.Success)
                    {
                        declared = true;
                        doc.BlockName = decl.Groups[2].Value;
                        doc.BlockType = decl.Groups[1].Value.ToUpperInvariant() switch
                        {
                            "FUNCTION_BLOCK" => "FB", "FUNCTION" => "FC", "ORGANIZATION_BLOCK" => "OB", "DATA_BLOCK" => "GlobalDB", "TYPE" => "PlcStruct", _ => decl.Groups[1].Value
                        };
                        if (doc.BlockType == "FC" && decl.Groups[4].Success)
                            doc.Members.Add(new InterfaceMember { Section = "Return", Path = "Ret_Val", Datatype = decl.Groups[4].Value.Trim() });
                    }
                    continue;
                }

                if (codeTrim.StartsWith("VERSION", StringComparison.OrdinalIgnoreCase) && codeTrim.IndexOf(':') > 0 && section == null && current == null)
                {
                    doc.Attributes["S7_Version"] = codeTrim.Substring(codeTrim.IndexOf(':') + 1).Trim().TrimEnd(';').Trim();
                    continue;
                }

                var upper = codeTrim.ToUpperInvariant();
                if (current == null)
                {
                    var newSection = SectionFor(upper, doc.BlockType);
                    if (newSection != null) { section = newSection; structPrefix.Clear(); continue; }
                    if (upper.StartsWith("END_VAR", StringComparison.Ordinal) || (upper.StartsWith("END_STRUCT", StringComparison.Ordinal) && structPrefix.Count == 0)) { section = null; continue; }
                    if (upper.StartsWith("END_STRUCT", StringComparison.Ordinal) && structPrefix.Count > 0) { structPrefix.RemoveAt(structPrefix.Count - 1); continue; }
                    if (section != null)
                    {
                        var m = MemberRegex.Match(codeTrim);
                        if (m.Success)
                        {
                            var name = m.Groups[1].Value.Trim('"');
                            var typeText = m.Groups[3].Value;
                            var assign = typeText.IndexOf(":=", StringComparison.Ordinal);
                            if (assign >= 0) typeText = typeText.Substring(0, assign);
                            typeText = typeText.Trim().TrimEnd(';').Trim();
                            var path = structPrefix.Count == 0 ? name : string.Join(".", structPrefix) + "." + name;
                            doc.Members.Add(new InterfaceMember { Section = section, Path = path, Datatype = typeText });
                            if (typeText.Equals("Struct", StringComparison.OrdinalIgnoreCase) || typeText.EndsWith(" of Struct", StringComparison.OrdinalIgnoreCase)) structPrefix.Add(name);
                        }
                        continue;
                    }
                    if (upper == "BEGIN")
                    {
                        // .scl bodies without NETWORK keywords are treated as one implicit network.
                        implicitBody = true;
                        current = new NetworkInfo { Index = ++networkIndex, Language = "SCL" };
                        doc.Networks.Add(current);
                        continue;
                    }
                }

                if (upper == "NETWORK" || upper.StartsWith("NETWORK ", StringComparison.Ordinal))
                {
                    if (implicitBody && current != null && current.SourceLines.Count == 0) { doc.Networks.Remove(current); networkIndex--; implicitBody = false; }
                    current = new NetworkInfo { Index = ++networkIndex };
                    pendingAttributes.TryGetValue("S7_NetworkTitle", out var title);
                    pendingAttributes.TryGetValue("S7_NetworkComment", out var netComment);
                    pendingAttributes.TryGetValue("S7_Language", out var language);
                    current.Title = title ?? ""; current.Comment = netComment ?? ""; current.Language = language ?? "";
                    pendingAttributes.Clear();
                    doc.Networks.Add(current);
                    continue;
                }
                if (upper.StartsWith("END_NETWORK", StringComparison.Ordinal)) { current = null; continue; }
                if (implicitBody && (upper.StartsWith("END_FUNCTION", StringComparison.Ordinal) || upper.StartsWith("END_ORGANIZATION", StringComparison.Ordinal) || upper.StartsWith("END_DATA_BLOCK", StringComparison.Ordinal))) { current = null; implicitBody = false; }
            }

            if (!declared) doc.Warnings.Add("No FUNCTION/FUNCTION_BLOCK/ORGANIZATION_BLOCK/DATA_BLOCK/TYPE declaration found.");
            if (doc.Attributes.TryGetValue("S7_BlockTitle", out var blockTitle)) { doc.Title = blockTitle; doc.Attributes.Remove("S7_BlockTitle"); }
            if (doc.Attributes.TryGetValue("S7_BlockComment", out var blockComment)) { doc.Comment = blockComment; doc.Attributes.Remove("S7_BlockComment"); }
            if (doc.Attributes.TryGetValue("S7_PreferredLanguage", out var preferred)) doc.Language = preferred;
            else if (format == "SCL") doc.Language = "SCL";
            else
            {
                var languages = doc.Networks.Select(n => n.Language).Where(l => l.Length > 0).Distinct().ToList();
                doc.Language = languages.Count == 1 ? languages[0] : languages.Count > 1 ? "Mixed" : "";
            }
            foreach (var n in doc.Networks.Where(n => n.Language.Length == 0)) n.Language = doc.Language;

            var sclCode = doc.Format == "SCL" || doc.Networks.Any(n => n.Language.Equals("SCL", StringComparison.OrdinalIgnoreCase));
            if (sclCode) doc.MaxNesting = MaxNesting(codeLines);
            CollectTextCalls(codeLines, doc);
            doc.Canonical = canonical;
            return doc;
        }

        private static string? SectionFor(string upper, string blockType)
        {
            if (upper.StartsWith("VAR_INPUT", StringComparison.Ordinal)) return "Input";
            if (upper.StartsWith("VAR_OUTPUT", StringComparison.Ordinal)) return "Output";
            if (upper.StartsWith("VAR_IN_OUT", StringComparison.Ordinal)) return "InOut";
            if (upper.StartsWith("VAR_TEMP", StringComparison.Ordinal)) return "Temp";
            if (upper.StartsWith("VAR CONSTANT", StringComparison.Ordinal) || upper.StartsWith("VAR_CONSTANT", StringComparison.Ordinal)) return "Constant";
            if (upper == "VAR" || upper.StartsWith("VAR ", StringComparison.Ordinal)) return "Static";
            if (upper == "STRUCT") return blockType == "PlcStruct" ? "None" : "Static";
            return null;
        }

        private static void ApplyAttributeBlock(string block, BlockDocument doc, Dictionary<string, string> pending, bool declared, bool insideNetwork, Func<string, string> resolve)
        {
            if (insideNetwork) return; // S7_Templates etc. inside rungs are part of the source, not metadata
            foreach (Match m in AttributePairRegex.Matches(block))
            {
                var key = m.Groups[1].Value;
                var value = resolve(m.Groups[2].Value);
                if (key == "S7_Language" || key == "S7_NetworkTitle" || key == "S7_NetworkComment") pending[key] = value;
                else if (key == "S7_Optimized_Access") doc.Attributes["S7_Optimized"] = value;
                else doc.Attributes[key] = value;
            }
        }

        // Splits a source line into code and comment parts; handles // and multi-line (* *) and skips string literals.
        public static string StripComments(string line, ref bool inBlockComment, out string comment)
        {
            var code = new StringBuilder();
            var commentText = new StringBuilder();
            var i = 0;
            while (i < line.Length)
            {
                if (inBlockComment)
                {
                    var end = line.IndexOf("*)", i, StringComparison.Ordinal);
                    if (end < 0) { commentText.Append(line, i, line.Length - i); i = line.Length; }
                    else { commentText.Append(line, i, end - i).Append(' '); i = end + 2; inBlockComment = false; }
                    continue;
                }
                var c = line[i];
                if (c == '\'' )
                {
                    var close = line.IndexOf('\'', i + 1);
                    if (close < 0) close = line.Length - 1;
                    code.Append(line, i, close - i + 1); i = close + 1; continue;
                }
                if (c == '/' && i + 1 < line.Length && line[i + 1] == '/') { commentText.Append(line, i + 2, line.Length - i - 2); break; }
                if (c == '(' && i + 1 < line.Length && line[i + 1] == '*') { inBlockComment = true; i += 2; continue; }
                code.Append(c); i++;
            }
            comment = commentText.ToString();
            return code.ToString();
        }

        private static string StripCommentsSimple(string line)
        {
            var dummy = false;
            return StripComments(line, ref dummy, out _);
        }

        public static int MaxNesting(IEnumerable<string> codeLines)
        {
            var depth = 0; var max = 0;
            foreach (var line in codeLines)
            {
                depth += NestOpenRegex.Matches(line).Count;
                if (depth > max) max = depth;
                depth -= NestCloseRegex.Matches(line).Count;
                if (depth < 0) depth = 0;
            }
            return max;
        }

        private static void CollectTextCalls(IEnumerable<string> codeLines, BlockDocument doc)
        {
            foreach (var line in codeLines)
            {
                foreach (Match m in QuotedCallRegex.Matches(line)) doc.BlockCalls.Add("Block:" + m.Groups[1].Value);
                foreach (Match m in InstanceCallRegex.Matches(line))
                    doc.BlockCalls.Add("Instance:" + m.Groups[1].Value + (m.Groups[2].Success ? "." + m.Groups[2].Value : ""));
                foreach (Match m in BareCallRegex.Matches(line))
                {
                    var name = m.Groups[1].Value;
                    if (!SclKeywords.Contains(name)) doc.Instructions.Add(name);
                }
            }
        }

        private static void BuildCanonical(BlockDocument doc)
        {
            var lines = new List<string>
            {
                "[Block] Name=" + doc.BlockName + " Type=" + doc.BlockType + " Language=" + doc.Language
            };
            if (doc.Title.Length > 0) lines.Add("[Title] " + doc.Title);
            if (doc.Comment.Length > 0) lines.Add("[Comment] " + doc.Comment);
            foreach (var kv in doc.Attributes) lines.Add("[Attribute] " + kv.Key + "=" + kv.Value);
            foreach (var m in doc.Members) lines.Add("[Interface] " + m.Section + " " + m.Path + " : " + m.Datatype);
            foreach (var n in doc.Networks)
            {
                lines.Add("[Network " + n.Index + "] Language=" + n.Language);
                if (n.Title.Length > 0) lines.Add("[Network " + n.Index + "] Title=" + n.Title);
                if (n.Comment.Length > 0) lines.Add("[Network " + n.Index + "] Comment=" + n.Comment);
                foreach (var s in n.SourceLines) { var t = s.Trim(); if (t.Length > 0) lines.Add(t); }
            }
            doc.Canonical = MaskLines(lines);
        }

        public static string Mask(string line)
        {
            var masked = GuidRegex.Replace(line, "<guid>");
            masked = TimestampRegex.Replace(masked, "<timestamp>");
            return masked.Length > MaxLineLength ? masked.Substring(0, MaxLineLength) + "…" : masked;
        }

        private static List<string> MaskLines(IEnumerable<string> lines) => lines.Select(Mask).ToList();

        // ------------------------------------------------------------------ Myers diff (linear space, middle snake)

        public static List<DiffEntry> Diff(IReadOnlyList<string> a, IReadOnlyList<string> b)
        {
            if (a.Count > MaxDiffLinesPerSide || b.Count > MaxDiffLinesPerSide)
                throw new ArgumentException("Documents exceed the diff bound of " + MaxDiffLinesPerSide + " normalized lines per side.");
            var output = new List<DiffEntry>();
            var stack = new Stack<object>();
            stack.Push(new int[] { 0, a.Count, 0, b.Count });
            while (stack.Count > 0)
            {
                var item = stack.Pop();
                if (item is List<DiffEntry> deferred) { output.AddRange(deferred); continue; }
                var seg = (int[])item;
                int aStart = seg[0], aEnd = seg[1], bStart = seg[2], bEnd = seg[3];
                while (aStart < aEnd && bStart < bEnd && a[aStart] == b[bStart])
                {
                    output.Add(new DiffEntry { Op = DiffOp.Equal, Text = a[aStart], LeftLine = aStart + 1, RightLine = bStart + 1 });
                    aStart++; bStart++;
                }
                var suffix = new List<DiffEntry>();
                while (aStart < aEnd && bStart < bEnd && a[aEnd - 1] == b[bEnd - 1])
                {
                    suffix.Add(new DiffEntry { Op = DiffOp.Equal, Text = a[aEnd - 1], LeftLine = aEnd, RightLine = bEnd });
                    aEnd--; bEnd--;
                }
                suffix.Reverse();
                if (suffix.Count > 0) stack.Push(suffix);
                if (aStart == aEnd)
                {
                    for (var j = bStart; j < bEnd; j++) output.Add(new DiffEntry { Op = DiffOp.Insert, Text = b[j], RightLine = j + 1 });
                    continue;
                }
                if (bStart == bEnd)
                {
                    for (var j = aStart; j < aEnd; j++) output.Add(new DiffEntry { Op = DiffOp.Delete, Text = a[j], LeftLine = j + 1 });
                    continue;
                }
                var (x, y) = MiddleSnake(a, b, aStart, aEnd, bStart, bEnd);
                if ((x == aStart && y == bStart) || (x == aEnd && y == bEnd))
                {
                    for (var j = aStart; j < aEnd; j++) output.Add(new DiffEntry { Op = DiffOp.Delete, Text = a[j], LeftLine = j + 1 });
                    for (var j = bStart; j < bEnd; j++) output.Add(new DiffEntry { Op = DiffOp.Insert, Text = b[j], RightLine = j + 1 });
                    continue;
                }
                stack.Push(new int[] { x, aEnd, y, bEnd });
                stack.Push(new int[] { aStart, x, bStart, y });
            }
            return output;
        }

        private static (int x, int y) MiddleSnake(IReadOnlyList<string> a, IReadOnlyList<string> b, int aStart, int aEnd, int bStart, int bEnd)
        {
            var n = aEnd - aStart; var m = bEnd - bStart;
            var max = (n + m + 1) / 2 + 1;
            var delta = n - m;
            var vf = new int[2 * max + 2];
            var vb = new int[2 * max + 2];
            var off = max;
            vf[off + 1] = 0; vb[off + 1] = 0;
            var oddDelta = (delta & 1) != 0;
            for (var d = 0; d <= max; d++)
            {
                for (var k = -d; k <= d; k += 2)
                {
                    int x;
                    if (k == -d || (k != d && vf[off + k - 1] < vf[off + k + 1])) x = vf[off + k + 1]; else x = vf[off + k - 1] + 1;
                    var y = x - k;
                    while (x < n && y < m && a[aStart + x] == b[bStart + y]) { x++; y++; }
                    vf[off + k] = x;
                    if (oddDelta && delta - k >= -(d - 1) && delta - k <= d - 1)
                    {
                        if (x + vb[off + delta - k] >= n) return (aStart + x, bStart + y);
                    }
                }
                for (var k = -d; k <= d; k += 2)
                {
                    int x;
                    if (k == -d || (k != d && vb[off + k - 1] < vb[off + k + 1])) x = vb[off + k + 1]; else x = vb[off + k - 1] + 1;
                    var y = x - k;
                    while (x < n && y < m && a[aEnd - 1 - x] == b[bEnd - 1 - y]) { x++; y++; }
                    vb[off + k] = x;
                    if (!oddDelta && delta - k >= -d && delta - k <= d)
                    {
                        if (vf[off + delta - k] + x >= n) return (aEnd - x, bEnd - y);
                    }
                }
            }
            return (aStart, bStart);
        }

        public static JsonArray BuildHunks(List<DiffEntry> entries, int contextLines)
        {
            var hunks = new JsonArray();
            var i = 0;
            while (i < entries.Count)
            {
                if (entries[i].Op == DiffOp.Equal) { i++; continue; }
                var start = Math.Max(0, i - contextLines);
                var end = i;
                var lastChange = i;
                while (end < entries.Count)
                {
                    if (entries[end].Op != DiffOp.Equal) lastChange = end;
                    else if (end - lastChange > 2 * contextLines) break;
                    end++;
                }
                end = Math.Min(entries.Count, lastChange + contextLines + 1);
                var lines = new JsonArray();
                int leftStart = 0, rightStart = 0, leftCount = 0, rightCount = 0, added = 0, removed = 0;
                for (var j = start; j < end; j++)
                {
                    var e = entries[j];
                    if (e.LeftLine > 0) { if (leftStart == 0) leftStart = e.LeftLine; leftCount++; }
                    if (e.RightLine > 0) { if (rightStart == 0) rightStart = e.RightLine; rightCount++; }
                    if (e.Op == DiffOp.Insert) added++; else if (e.Op == DiffOp.Delete) removed++;
                    lines.Add((e.Op == DiffOp.Equal ? " " : e.Op == DiffOp.Delete ? "-" : "+") + e.Text);
                }
                hunks.Add(new JsonObject
                {
                    ["leftStart"] = leftStart, ["leftCount"] = leftCount, ["rightStart"] = rightStart, ["rightCount"] = rightCount,
                    ["added"] = added, ["removed"] = removed, ["lines"] = lines
                });
                i = end;
            }
            return hunks;
        }

        // ------------------------------------------------------------------ compare

        public static JsonObject CompareFiles(string leftPath, string rightPath, int offset, int limit, int contextLines = 2)
            => Compare(ParseFile(leftPath), ParseFile(rightPath), offset, limit, contextLines);

        public static JsonObject Compare(BlockDocument left, BlockDocument right, int offset, int limit, int contextLines = 2)
        {
            ValidatePage(offset, limit);
            var identical = left.Canonical.SequenceEqual(right.Canonical, StringComparer.Ordinal);
            var structural = CompareStructure(left, right);
            var entries = identical ? new List<DiffEntry>() : Diff(left.Canonical, right.Canonical);
            var hunks = BuildHunks(entries, contextLines);
            var page = new JsonArray();
            for (var i = offset; i < hunks.Count && page.Count < limit; i++) page.Add(hunks[i]!.DeepClone());
            var result = new JsonObject
            {
                ["left"] = Describe(left),
                ["right"] = Describe(right),
                ["identicalAfterNormalization"] = identical,
                ["structurallyIdentical"] = structural["differenceCount"]!.GetValue<int>() == 0,
                ["formatMismatch"] = !string.Equals(left.Format, right.Format, StringComparison.Ordinal),
                ["structural"] = structural,
                ["lineStats"] = new JsonObject
                {
                    ["added"] = entries.Count(e => e.Op == DiffOp.Insert),
                    ["removed"] = entries.Count(e => e.Op == DiffOp.Delete),
                    ["unchanged"] = entries.Count(e => e.Op == DiffOp.Equal),
                },
                ["hunkCount"] = hunks.Count,
                ["offset"] = offset,
                ["limit"] = limit,
                ["hunks"] = page,
                ["dataComplete"] = offset + page.Count >= hunks.Count,
                ["normalizationRules"] = new JsonArray(NormalizationRules.Select(r => (JsonNode)r).ToArray()),
            };
            if (!string.Equals(left.Format, right.Format, StringComparison.Ordinal))
                result["warning"] = "Documents have different formats (" + left.Format + " vs " + right.Format + "); the line diff compares different canonical renderings, rely on the structural section.";
            return result;
        }

        private static JsonObject Describe(BlockDocument doc) => new JsonObject
        {
            ["path"] = doc.SourcePath, ["format"] = doc.Format, ["blockName"] = doc.BlockName, ["blockType"] = doc.BlockType, ["language"] = doc.Language,
            ["canonicalLines"] = doc.Canonical.Count, ["networkCount"] = doc.Networks.Count, ["memberCount"] = doc.Members.Count,
            ["warnings"] = new JsonArray(doc.Warnings.Select(w => (JsonNode)w).ToArray()),
        };

        private static JsonObject Pair(string l, string r) => new JsonObject { ["left"] = l, ["right"] = r, ["equal"] = string.Equals(l, r, StringComparison.Ordinal) };

        public static JsonObject CompareStructure(BlockDocument left, BlockDocument right)
        {
            var differences = 0;
            var sameFormat = string.Equals(left.Format, right.Format, StringComparison.Ordinal);
            var result = new JsonObject();
            foreach (var (key, l, r) in new[] { ("blockName", left.BlockName, right.BlockName), ("blockType", left.BlockType, right.BlockType), ("language", left.Language, right.Language), ("title", left.Title, right.Title), ("comment", left.Comment, right.Comment) })
            {
                result[key] = Pair(l, r);
                if (!string.Equals(l, r, StringComparison.Ordinal)) differences++;
            }

            var changed = new JsonArray(); var onlyLeft = new JsonArray(); var onlyRight = new JsonArray();
            foreach (var kv in left.Attributes)
            {
                if (right.Attributes.TryGetValue(kv.Key, out var rv))
                {
                    if (!string.Equals(kv.Value, rv, StringComparison.Ordinal)) { changed.Add(new JsonObject { ["name"] = kv.Key, ["left"] = kv.Value, ["right"] = rv }); differences++; }
                }
                else { onlyLeft.Add(kv.Key); if (sameFormat) differences++; }
            }
            foreach (var kv in right.Attributes.Where(kv => !left.Attributes.ContainsKey(kv.Key))) { onlyRight.Add(kv.Key); if (sameFormat) differences++; }
            result["attributes"] = new JsonObject { ["changed"] = changed, ["onlyLeft"] = onlyLeft, ["onlyRight"] = onlyRight };

            var added = new JsonArray(); var removed = new JsonArray(); var typeChanged = new JsonArray();
            var leftMembers = left.Members.ToDictionary(m => m.Section + "/" + m.Path, m => m, StringComparer.Ordinal);
            var rightMembers = right.Members.ToDictionary(m => m.Section + "/" + m.Path, m => m, StringComparer.Ordinal);
            foreach (var kv in leftMembers)
            {
                if (!rightMembers.TryGetValue(kv.Key, out var rm)) { removed.Add(MemberJson(kv.Value)); differences++; continue; }
                if (!SameType(kv.Value.Datatype, rm.Datatype)) { typeChanged.Add(new JsonObject { ["section"] = rm.Section, ["path"] = rm.Path, ["left"] = kv.Value.Datatype, ["right"] = rm.Datatype }); differences++; }
            }
            foreach (var kv in rightMembers.Where(kv => !leftMembers.ContainsKey(kv.Key))) { added.Add(MemberJson(kv.Value)); differences++; }
            var leftSections = left.Members.Select(m => m.Section).Distinct().ToList();
            var rightSections = right.Members.Select(m => m.Section).Distinct().ToList();
            result["interface"] = new JsonObject
            {
                ["memberCount"] = new JsonObject { ["left"] = left.Members.Count, ["right"] = right.Members.Count },
                ["added"] = added, ["removed"] = removed, ["typeChanged"] = typeChanged,
                ["sectionsAdded"] = new JsonArray(rightSections.Except(leftSections).Select(s => (JsonNode)s).ToArray()),
                ["sectionsRemoved"] = new JsonArray(leftSections.Except(rightSections).Select(s => (JsonNode)s).ToArray()),
            };

            var titlesChanged = new JsonArray(); var commentsChanged = new JsonArray(); var languagesChanged = new JsonArray();
            var common = Math.Min(left.Networks.Count, right.Networks.Count);
            for (var i = 0; i < common; i++)
            {
                var ln = left.Networks[i]; var rn = right.Networks[i];
                if (!string.Equals(ln.Title, rn.Title, StringComparison.Ordinal)) { titlesChanged.Add(new JsonObject { ["index"] = ln.Index, ["left"] = ln.Title, ["right"] = rn.Title }); differences++; }
                if (!string.Equals(ln.Comment, rn.Comment, StringComparison.Ordinal)) { commentsChanged.Add(new JsonObject { ["index"] = ln.Index, ["left"] = ln.Comment, ["right"] = rn.Comment }); differences++; }
                if (!string.Equals(ln.Language, rn.Language, StringComparison.OrdinalIgnoreCase)) { languagesChanged.Add(new JsonObject { ["index"] = ln.Index, ["left"] = ln.Language, ["right"] = rn.Language }); differences++; }
            }
            if (left.Networks.Count != right.Networks.Count) differences++;
            result["networks"] = new JsonObject
            {
                ["count"] = new JsonObject { ["left"] = left.Networks.Count, ["right"] = right.Networks.Count, ["equal"] = left.Networks.Count == right.Networks.Count },
                ["added"] = new JsonArray(right.Networks.Skip(common).Select(n => (JsonNode)n.Index).ToArray()),
                ["removed"] = new JsonArray(left.Networks.Skip(common).Select(n => (JsonNode)n.Index).ToArray()),
                ["titlesChanged"] = titlesChanged, ["commentsChanged"] = commentsChanged, ["languagesChanged"] = languagesChanged,
                ["titles"] = new JsonObject
                {
                    ["left"] = new JsonArray(left.Networks.Select(n => (JsonNode)n.Title).ToArray()),
                    ["right"] = new JsonArray(right.Networks.Select(n => (JsonNode)n.Title).ToArray()),
                },
            };
            result["differenceCount"] = differences;
            return result;
        }

        private static JsonObject MemberJson(InterfaceMember m) => new JsonObject { ["section"] = m.Section, ["path"] = m.Path, ["datatype"] = m.Datatype };

        private static bool SameType(string l, string r) => string.Equals(CollapseWhitespace(l), CollapseWhitespace(r), StringComparison.OrdinalIgnoreCase);

        public static void ValidatePage(int offset, int limit)
        {
            if (offset < 0) throw new ArgumentException("offset must be >= 0.");
            if (limit < 1 || limit > MaxLimit) throw new ArgumentException("limit must be between 1 and " + MaxLimit + ".");
        }

        // ------------------------------------------------------------------ annotations

        public static string[] ParseMarkers(string markersJson)
        {
            if (string.IsNullOrWhiteSpace(markersJson)) return DefaultMarkers;
            JsonNode? node;
            try { node = JsonNode.Parse(markersJson); } catch (JsonException ex) { throw new ArgumentException("markersJson must be a JSON string array: " + ex.Message); }
            if (node is not JsonArray array || array.Count == 0) throw new ArgumentException("markersJson must be a non-empty JSON string array, e.g. [\"TODO\",\"FIXME\"].");
            var markers = new List<string>();
            foreach (var item in array)
            {
                var value = item?.GetValue<string>()?.Trim() ?? "";
                if (value.Length == 0 || !Regex.IsMatch(value, @"^[A-Za-z0-9_]+$")) throw new ArgumentException("Marker '" + value + "' must be a plain word (letters, digits, underscore).");
                if (!markers.Contains(value, StringComparer.Ordinal)) markers.Add(value);
            }
            return markers.ToArray();
        }

        public static string[] ParseExtensions(string extensionsJson)
        {
            if (string.IsNullOrWhiteSpace(extensionsJson)) return DefaultExtensions;
            JsonNode? node;
            try { node = JsonNode.Parse(extensionsJson); } catch (JsonException ex) { throw new ArgumentException("extensionsJson must be a JSON string array: " + ex.Message); }
            if (node is not JsonArray array || array.Count == 0) throw new ArgumentException("extensionsJson must be a non-empty JSON string array, e.g. [\".xml\",\".s7dcl\"].");
            var list = new List<string>();
            foreach (var item in array)
            {
                var value = item?.GetValue<string>()?.Trim().ToLowerInvariant() ?? "";
                if (value.Length == 0) throw new ArgumentException("Empty extension in extensionsJson.");
                if (!value.StartsWith(".", StringComparison.Ordinal)) value = "." + value;
                if (!list.Contains(value)) list.Add(value);
            }
            return list.ToArray();
        }

        public static List<string> EnumerateDocuments(string directory, bool recursive, string[] extensions)
        {
            if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathRooted(directory)) throw new ArgumentException("Absolute directory path required: '" + directory + "'.");
            if (!Directory.Exists(directory)) throw new DirectoryNotFoundException("Directory not found: " + directory);
            return Directory.EnumerateFiles(directory, "*", recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
                .Where(f => extensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public static List<AnnotationRow> ScanFile(string path, string[] markers)
        {
            var text = File.ReadAllText(path, Encoding.UTF8);
            string? s7res = null;
            var resPath = Path.ChangeExtension(path, ".s7res");
            if (!string.Equals(resPath, path, StringComparison.OrdinalIgnoreCase) && File.Exists(resPath)) s7res = File.ReadAllText(resPath, Encoding.UTF8);
            return ScanText(path, text, markers, s7res);
        }

        public static List<AnnotationRow> ScanText(string filePath, string text, string[] markers, string? s7resText = null)
        {
            if (markers == null || markers.Length == 0) throw new ArgumentException("At least one marker is required.");
            var regex = new Regex(@"\b(" + string.Join("|", markers.Select(Regex.Escape)) + @")\b");
            var body = (text ?? "").TrimStart('﻿');
            return LooksLikeXml(body) ? ScanXml(filePath, body, regex) : ScanSource(filePath, body, regex, ReadS7Res(s7resText));
        }

        private static List<AnnotationRow> ScanXml(string filePath, string xml, Regex regex)
        {
            var rows = new List<AnnotationRow>();
            var xdoc = XDocument.Parse(xml, LoadOptions.SetLineInfo);
            var root = xdoc.Root;
            if (root == null) return rows;
            var block = root.Elements().FirstOrDefault(e => Local(e).StartsWith("SW.", StringComparison.Ordinal)) ?? (Local(root).StartsWith("SW.", StringComparison.Ordinal) ? root : null);
            var blockName = Child(Child(block, "AttributeList"), "Name")?.Value.Trim() ?? "";
            var units = new Dictionary<XElement, int>();
            if (block != null) foreach (var unit in block.Descendants().Where(e => Local(e) == "SW.Blocks.CompileUnit")) units[unit] = units.Count + 1;

            foreach (var textNode in xdoc.DescendantNodes().OfType<XText>())
            {
                var value = textNode.Value;
                if (string.IsNullOrWhiteSpace(value)) continue;
                var parent = textNode.Parent;
                if (parent != null && Local(parent) == "Name") continue;
                foreach (Match m in regex.Matches(value))
                {
                    int? network = null;
                    var kind = "xmlText";
                    for (var e = parent; e != null; e = e.Parent)
                    {
                        var local = Local(e);
                        if (kind == "xmlText")
                        {
                            if (local == "LineComment" || local == "Comment" && e.Parent != null && Local(e.Parent) == "StructuredText") kind = "sclComment";
                            else if (local == "MultilingualText")
                            {
                                var composition = (string?)e.Attribute("CompositionName") ?? "";
                                kind = composition == "Title" ? "title" : composition == "Comment" ? "comment" : "multilingualText";
                            }
                            else if (local == "Comment" && e.Elements().Any(c => Local(c) == "MultiLanguageText")) kind = "memberComment";
                        }
                        if (units.TryGetValue(e, out var idx)) { network = idx; break; }
                    }
                    if (kind == "title" || kind == "comment") kind = (network.HasValue ? "network" : "block") + (kind == "title" ? "Title" : "Comment");
                    rows.Add(new AnnotationRow
                    {
                        File = filePath, BlockName = blockName, Network = network,
                        Line = ((IXmlLineInfo)textNode).HasLineInfo() ? ((IXmlLineInfo)textNode).LineNumber : 0,
                        Marker = m.Groups[1].Value, Text = Clip(CollapseWhitespace(value)), Kind = kind,
                    });
                }
            }
            return rows;
        }

        private static List<AnnotationRow> ScanSource(string filePath, string text, Regex regex, Dictionary<string, string> resources)
        {
            var rows = new List<AnnotationRow>();
            var lines = text.Replace("\r\n", "\n").Split('\n');
            var inBlockComment = false;
            var blockName = "";
            var networkIndex = 0;
            var inNetwork = false;
            var implicitBody = false;
            for (var i = 0; i < lines.Length; i++)
            {
                var raw = lines[i];
                var code = StripComments(raw, ref inBlockComment, out var comment);
                var codeTrim = code.Trim();
                var upper = codeTrim.ToUpperInvariant();
                if (blockName.Length == 0) { var decl = DeclarationRegex.Match(codeTrim); if (decl.Success) blockName = decl.Groups[2].Value; }
                if (upper == "NETWORK" || upper.StartsWith("NETWORK ", StringComparison.Ordinal))
                {
                    if (implicitBody && inNetwork) { networkIndex--; implicitBody = false; }
                    networkIndex++; inNetwork = true;
                }
                else if (upper.StartsWith("END_NETWORK", StringComparison.Ordinal)) inNetwork = false;
                else if (upper == "BEGIN" && networkIndex == 0) { networkIndex = 1; inNetwork = true; implicitBody = true; }
                else if (implicitBody && (upper.StartsWith("END_FUNCTION", StringComparison.Ordinal) || upper.StartsWith("END_ORGANIZATION", StringComparison.Ordinal) || upper.StartsWith("END_DATA_BLOCK", StringComparison.Ordinal))) { inNetwork = false; implicitBody = false; }

                int? network = inNetwork ? networkIndex : (int?)null;
                foreach (Match m in regex.Matches(comment))
                    rows.Add(new AnnotationRow { File = filePath, BlockName = blockName, Network = network, Line = i + 1, Marker = m.Groups[1].Value, Text = Clip(CollapseWhitespace(comment)), Kind = "sourceComment" });

                foreach (Match mlc in MlcRegex.Matches(code))
                {
                    if (!resources.TryGetValue(mlc.Value, out var resolved)) continue;
                    var kind = code.IndexOf("S7_NetworkTitle", StringComparison.Ordinal) >= 0 ? "networkTitle"
                        : code.IndexOf("S7_NetworkComment", StringComparison.Ordinal) >= 0 ? "networkComment"
                        : code.IndexOf("S7_BlockTitle", StringComparison.Ordinal) >= 0 ? "blockTitle"
                        : code.IndexOf("S7_BlockComment", StringComparison.Ordinal) >= 0 ? "blockComment" : "resourceText";
                    var pendingNetwork = kind.StartsWith("network", StringComparison.Ordinal) && !inNetwork ? networkIndex + 1 : network;
                    foreach (Match m in regex.Matches(resolved))
                        rows.Add(new AnnotationRow { File = filePath, BlockName = blockName, Network = pendingNetwork, Line = i + 1, Marker = m.Groups[1].Value, Text = Clip(CollapseWhitespace(resolved)), Kind = kind });
                }
            }
            return rows;
        }

        private static string Clip(string text) => text.Length > MaxAnnotationText ? text.Substring(0, MaxAnnotationText) + "…" : text;

        public static JsonObject AnnotationJson(AnnotationRow r) => new JsonObject
        {
            ["file"] = r.File, ["blockName"] = r.BlockName, ["network"] = r.Network.HasValue ? r.Network.Value : (JsonNode?)null,
            ["line"] = r.Line, ["marker"] = r.Marker, ["text"] = r.Text, ["kind"] = r.Kind,
        };

        public static string ToCsv(IEnumerable<AnnotationRow> rows)
        {
            var sb = new StringBuilder();
            sb.AppendLine("file,blockName,network,line,marker,kind,text");
            foreach (var r in rows)
                sb.AppendLine(string.Join(",", new[] { r.File, r.BlockName, r.Network?.ToString(CultureInfo.InvariantCulture) ?? "", r.Line.ToString(CultureInfo.InvariantCulture), r.Marker, r.Kind, r.Text }.Select(CsvField)));
            return sb.ToString();
        }

        private static string CsvField(string value)
        {
            var v = value ?? "";
            return v.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
        }

        // ------------------------------------------------------------------ metrics

        public static JsonObject Metrics(BlockDocument doc)
        {
            var sections = new JsonObject();
            var total = 0;
            foreach (var g in doc.Members.GroupBy(m => m.Section).OrderBy(g => g.Key, StringComparer.Ordinal)) { sections[g.Key] = g.Count(); total += g.Count(); }
            sections["total"] = total;

            var calls = new JsonArray();
            foreach (var g in doc.BlockCalls.GroupBy(c => c, StringComparer.Ordinal).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal))
            {
                var colon = g.Key.IndexOf(':');
                calls.Add(new JsonObject { ["kind"] = colon > 0 ? g.Key.Substring(0, colon) : "", ["name"] = colon > 0 ? g.Key.Substring(colon + 1) : g.Key, ["count"] = g.Count() });
            }
            var instructions = new JsonArray();
            var instructionGroups = doc.Instructions.GroupBy(c => c, StringComparer.Ordinal).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).ToList();
            foreach (var g in instructionGroups.Take(25)) instructions.Add(new JsonObject { ["name"] = g.Key, ["count"] = g.Count() });

            var languages = new JsonObject();
            foreach (var g in doc.Networks.GroupBy(n => n.Language.Length == 0 ? "(unknown)" : n.Language).OrderBy(g => g.Key, StringComparer.Ordinal)) languages[g.Key] = g.Count();

            var sclCode = doc.Format == "SCL" || doc.Networks.Any(n => n.Language.Equals("SCL", StringComparison.OrdinalIgnoreCase));
            return new JsonObject
            {
                ["file"] = doc.SourcePath, ["format"] = doc.Format, ["blockName"] = doc.BlockName, ["blockType"] = doc.BlockType, ["language"] = doc.Language,
                ["networkCount"] = doc.Networks.Count,
                ["networksWithTitle"] = doc.Networks.Count(n => n.Title.Length > 0),
                ["networksWithComment"] = doc.Networks.Count(n => n.Comment.Length > 0),
                ["networkLanguages"] = languages,
                ["interfaceMembers"] = sections,
                ["blockCalls"] = calls,
                ["blockCallCount"] = doc.BlockCalls.Count,
                ["instructions"] = instructions,
                ["instructionCount"] = doc.Instructions.Count,
                ["distinctInstructions"] = instructionGroups.Count,
                ["instructionsTruncated"] = instructionGroups.Count > 25,
                ["sourceLines"] = doc.SourceLines,
                ["commentLines"] = doc.CommentLines,
                ["commentRatio"] = doc.SourceLines > 0 ? Math.Round((double)doc.CommentLines / doc.SourceLines, 3) : (JsonNode?)null,
                ["maxNesting"] = doc.MaxNesting.HasValue ? doc.MaxNesting.Value : (JsonNode?)null,
                ["nestingDerivable"] = sclCode && doc.MaxNesting.HasValue,
                ["canonicalLines"] = doc.Canonical.Count,
                ["warnings"] = new JsonArray(doc.Warnings.Select(w => (JsonNode)w).ToArray()),
            };
        }
    }
}
