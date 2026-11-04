using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace TiaMcpServer.ModelContextProtocol
{
    public static partial class LadTextRenderer
    {

        // ---- StructuredText (SCL/STL) ----
        //
        // The StructuredText body is a flat token stream, but an <Access> is a TREE: a call carries its
        // parameter list (<CallInfo><Parameter>… with nested Token/Blank/Access), an array component carries
        // its index <Access>, and named constants have no ConstantValue at all. Rendering must recurse, or
        // every call, named constant and absolute address silently vanishes from the output.

        private static string RenderStructuredText(XElement unit)
        {
            var st = unit.Descendants("StructuredText").FirstOrDefault();
            if (st == null) return "";
            var sb = new StringBuilder();
            RenderStNodes(st.Elements(), sb);
            return sb.ToString();
        }

        private static void RenderStNodes(IEnumerable<XElement> nodes, StringBuilder sb)
        {
            foreach (var node in nodes)
            {
                switch (node.Name.LocalName)
                {
                    case "Text": sb.Append(node.Value); break;
                    case "Token":
                    case "StlToken": sb.Append(node.Attribute("Text")?.Value ?? ""); break;
                    case "Blank": sb.Append(' ', ParseNum(node, 1)); break;
                    case "NewLine": sb.Append('\n'); break;
                    case "Access": sb.Append(RenderStAccess(node)); break;
                    case "CallInfo":
                    case "Instruction": RenderStCallInfo(node, sb); break;
                    case "Parameter": RenderStParameter(node, sb); break;
                    case "Comment":
                    {
                        var ct = CommentText(node);
                        if (ct.Length > 0) sb.Append("(*").Append(ct).Append("*)");
                        break;
                    }
                    case "LineComment":
                    {
                        var ct = CommentText(node);
                        if (ct.Length > 0) sb.Append("//").Append(ct);
                        break;
                    }
                    case "StlStatement":
                        RenderStNodes(node.Elements(), sb);
                        if (sb.Length > 0 && sb[sb.Length - 1] != '\n') sb.Append('\n');
                        break;
                    // Informative metadata carried inside Parameter/CallInfo/Access (NumBLs, template values …):
                    // not source text, must not leak into the listing.
                    case "IntegerAttribute": case "BooleanAttribute": case "StringAttribute": case "DateAttribute": case "TemplateValue":
                        break;
                    default:
                        RenderStNodes(node.Elements(), sb);  // unknown wrapper: never drop what is inside it
                        break;
                }
            }
        }

        private static string CommentText(XElement node)
            => string.Concat(node.Descendants("Text").Select(t => t.Value));

        // Access_T (SW.PlcBlocks.Access_v5.xsd) is a choice of Label | Constant | CallInfo | Instruction | Indirect |
        // Statusword | PredefinedVariable | Expression | Symbol | Address | DataType | Reference, plus an optional comment.
        private static string RenderStAccess(XElement acc)
        {
            var scope = acc.Attribute("Scope")?.Value ?? "";
            var sb = new StringBuilder();
            foreach (var child in acc.Elements())
            {
                switch (child.Name.LocalName)
                {
                    case "CallInfo":
                    case "Instruction": RenderStCallInfo(child, sb); break;
                    case "Symbol":
                    case "Instance": sb.Append(RenderStSymbol(child, child.Attribute("Scope")?.Value ?? scope)); break;
                    case "Constant":
                    {
                        // literal / typed: <Constant><ConstantValue>…   named (LocalConstant/GlobalConstant): <Constant Name="…"/>
                        var named = child.Attribute("Name")?.Value;
                        sb.Append(!string.IsNullOrEmpty(named) ? Qualify(scope, named!) : child.Element("ConstantValue")?.Value?.Trim() ?? "?");
                        break;
                    }
                    case "Address": sb.Append(AddressText(child)); break;
                    case "PredefinedVariable": sb.Append(child.Attribute("Name")?.Value ?? "ENO"); break;   // SCL only: ENO
                    case "Label": sb.Append(child.Attribute("Name")?.Value ?? ""); RenderStNodes(child.Elements(), sb); break;
                    case "Statusword": sb.Append(child.Attribute("Combination")?.Value ?? ""); break;
                    case "DataType": sb.Append(child.Value.Trim()); break;
                    default: RenderStNodes(new[] { child }, sb); break;   // Expression, Reference, Indirect, Comment, Token …
                }
            }
            return sb.ToString();
        }

        // Symbol_T / Instance_T are ordered sequences of Component | Address | AbsoluteOffset | Token | Access.
        // SCL exports carry the punctuation as Tokens ('.', '%X15', '[', ']'); FlgNet exports have bare Components,
        // so the separators are synthesised only when no Token is present. The scope prefix ('#' local, quotes
        // around the first component for globals) is never a token and is always added here.
        private static string RenderStSymbol(XElement symbol, string scope)
        {
            var sb = new StringBuilder();
            bool hasTokens = symbol.Elements("Token").Any();
            bool quotedByTokens = symbol.Elements("Token").Any(t => t.Attribute("Text")?.Value == "\"");
            bool local = scope.StartsWith("Local", StringComparison.OrdinalIgnoreCase);
            bool global = scope.StartsWith("Global", StringComparison.OrdinalIgnoreCase) && !quotedByTokens;
            bool first = true, previousWasComponent = false;
            foreach (var child in symbol.Elements())
            {
                switch (child.Name.LocalName)
                {
                    case "Component":
                        if (!hasTokens && previousWasComponent) sb.Append('.');
                        if (first && local) sb.Append('#');
                        sb.Append(ComponentText(child, hasTokens, first && global, quotedByTokens));
                        first = false; previousWasComponent = true;
                        break;
                    case "Token": sb.Append(child.Attribute("Text")?.Value ?? ""); previousWasComponent = false; break;
                    case "Access": sb.Append(RenderStAccess(child)); previousWasComponent = false; break;
                    case "Address": sb.Append(AddressText(child)); previousWasComponent = false; break;
                    case "AbsoluteOffset": break;   // informative
                    default: RenderStNodes(new[] { child }, sb); previousWasComponent = false; break;
                }
            }
            return sb.ToString();
        }

        // Named constant: #NAME locally, "NAME" globally.
        private static string Qualify(string scope, string name)
        {
            if (scope.StartsWith("Local", StringComparison.OrdinalIgnoreCase)) return "#" + SymbolName(name);
            if (scope.StartsWith("Global", StringComparison.OrdinalIgnoreCase)) return SymbolName(name, true);
            return SymbolName(name);
        }

        // Adapted from upstream cd0eed6 (MIT): delimit each symbol component, never the whole path.
        private static string SymbolName(string name, bool forceQuotes = false)
            => forceQuotes || !Regex.IsMatch(name, @"^[\p{L}_][\p{L}\p{Nd}_]*$") ? "\"" + name + "\"" : name;

        // Component_T: Name plus optional Token / index-Access / Comment children. With tokens the children are
        // rendered verbatim (arr[#i]); without them the index brackets and a SliceAccessModifier (.%X0) are synthesised.
        private static string ComponentText(XElement component, bool symbolHasTokens, bool forceQuotes = false, bool quotedByTokens = false)
        {
            var name = component.Attribute("Name")?.Value ?? "";
            var explicitlyQuoted = component.Elements("BooleanAttribute").Any(a => a.Attribute("Name")?.Value == "HasQuotes" && a.Value == "true");
            var sb = new StringBuilder(quotedByTokens ? name : SymbolName(name, forceQuotes || explicitlyQuoted));
            if (component.Elements("Token").Any()) { RenderStNodes(component.Elements(), sb); return sb.ToString(); }
            var indexes = component.Elements("Access").Select(RenderStAccess).ToList();
            if (indexes.Count > 0) sb.Append('[').Append(string.Join(", ", indexes)).Append(']');
            var slice = component.Attribute("SliceAccessModifier")?.Value;
            if (!symbolHasTokens && !string.IsNullOrEmpty(slice) && slice != "undef") sb.Append(".%").Append(slice);
            return sb.ToString();
        }

        // FC / instruction:  Name(params)     FB via instance:  #inst⟨FbName⟩(params)  /  "DB"⟨FbName⟩(params)
        // The FB type is not textually present in SCL for instance calls; the ⟨…⟩ annotation keeps it visible.
        private static void RenderStCallInfo(XElement callInfo, StringBuilder sb)
        {
            var name = callInfo.Attribute("Name")?.Value;
            var instance = callInfo.Element("Instance");
            if (instance != null)
            {
                var inst = RenderStSymbol(instance, instance.Attribute("Scope")?.Value ?? "");
                sb.Append(string.IsNullOrEmpty(inst) ? name ?? "?" : inst + (string.IsNullOrEmpty(name) ? "" : "⟨" + name + "⟩"));
            }
            else if (!string.IsNullOrEmpty(name)) sb.Append(SymbolName(name!));
            RenderStNodes(callInfo.Elements().Where(e => e.Name.LocalName != "Instance"), sb);
        }

        // <Parameter Name="IN"><Token ":="/>…</Parameter> -> IN := …
        // A positional argument (conversion/math functions) has no ':='/'=>' token, so its Name is not printed.
        private static void RenderStParameter(XElement parameter, StringBuilder sb)
        {
            var name = parameter.Attribute("Name")?.Value;
            // Informative *Attribute elements may precede the tokens; they are metadata, not the first source token.
            var first = parameter.Elements()
                .FirstOrDefault(e => e.Name.LocalName != "Blank" && !e.Name.LocalName.EndsWith("Attribute", StringComparison.Ordinal));
            var firstText = first?.Name.LocalName == "Token" ? first.Attribute("Text")?.Value : null;
            if (!string.IsNullOrEmpty(name) && (firstText == ":=" || firstText == "=>"))
            {
                sb.Append(name);
                if (parameter.Elements().FirstOrDefault(e => !e.Name.LocalName.EndsWith("Attribute", StringComparison.Ordinal))?.Name.LocalName != "Blank") sb
                    .Append(' ');
            }
            RenderStNodes(parameter.Elements(), sb);
        }

        private static int ParseNum(XElement e, int def)
            => int.TryParse(e.Attribute("Num")?.Value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var n)
                ? n : def;

        private static string FirstMultilingual(XElement unit, string composition)
        {
            var mt = unit.Elements("ObjectList").Elements("MultilingualText")
                         .FirstOrDefault(m => m.Attribute("CompositionName")?.Value == composition);
            var txt = mt?.Descendants("Text").FirstOrDefault(t => !string.IsNullOrWhiteSpace(t.Value))?.Value;
            return txt?.Trim() ?? "";
        }

        private static string IndentBlock(string text, string indent)
        {
            var lines = text.Replace("\r\n", "\n").Split('\n');
            return string.Join("\n", lines.Select(l => indent + l)) + "\n";
        }
    }
}
