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
    public static partial class OfflineAnalysisLogic
    {

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
                    foreach (var part in source.Descendants()
                            .Where(e => Local(e) == "Part" || (Local(e) == "Instruction" && Local(source) != "StructuredText")))
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
                foreach (var attr in e.Attributes().Where(a => VolatileAttributes.Contains(a.Name.LocalName) || a.IsNamespaceDeclaration).ToList()) attr
                    .Remove();
                e.Name = XName.Get(e.Name.LocalName);
            }
        }
    }
}
