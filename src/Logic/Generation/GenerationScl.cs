using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace TiaMcp.Logic.Generation
{
    internal sealed class SclDeclaration
    {
        internal string Name = "";
        internal string Kind = "";
        internal string Content = "";
        internal string[] References = Array.Empty<string>();
    }

    internal sealed class SclCall
    {
        internal string Instance = "";
        internal string Target = "";
        internal LibraryPartTypesItem Type = new LibraryPartTypesItem();
        internal Dictionary<string, string> Bindings = new Dictionary<string, string>(StringComparer.Ordinal);
    }

    internal static class GenerationScl
    {
        internal static IReadOnlyList<SclDeclaration> Declarations(byte[] bytes)
        {
            string source;
            try { source = new UTF8Encoding(false, true).GetString(bytes); }
            catch (DecoderFallbackException) { throw CanonicalJson.Failure("/sources", "encoding", "SCL must be UTF-8."); }
            source = source.Replace("\r\n", "\n").Replace("\r", "\n").TrimStart('\ufeff');
            // Mask comments and SCL string literals while keeping offsets and newlines.
            var code = Regex.Replace(source, @"//[^\n]*|/\*[\s\S]*?\*/|'(?:[^']|'')*'", m => new string(m.Value.Select(c => c == '\n' ? '\n' : ' ').ToArray()), RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
            var headers = Regex.Matches(code, @"(?im)^\s*(TYPE|FUNCTION_BLOCK|FUNCTION|DATA_BLOCK|ORGANIZATION_BLOCK)\s+(?:""([^""\n]+)""|([A-Za-z_][A-Za-z0-9_]*))", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
            if (headers.Count == 0) throw CanonicalJson.Failure("/sources", "scl-declaration", "SCL source has no named declaration.");
            var declarations = new List<SclDeclaration>();
            for (var i = 0; i < headers.Count; i++)
            {
                var header = headers[i];
                var keyword = header.Groups[1].Value.ToUpperInvariant();
                var endPattern = keyword == "TYPE" ? "END_TYPE" : "END_" + keyword;
                var end = Regex.Match(code.Substring(header.Index + header.Length), @"(?im)^\s*" + endPattern + @"\b[^\n]*", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
                if (!end.Success || i + 1 < headers.Count && header.Index + header.Length + end.Index >= headers[i + 1].Index)
                    throw CanonicalJson.Failure("/sources", "scl-declaration", "Missing declaration terminator: " + endPattern);
                var length = header.Length + end.Index + end.Length;
                var content = source.Substring(header.Index, length).Trim() + "\n";
                var declarationCode = code.Substring(header.Index, length);
                var references = Regex.Matches(declarationCode, @"""([^""\n]+)""", RegexOptions.CultureInvariant)
                    .Cast<Match>().Where(m => !declarationCode.Substring(m.Index + m.Length).TrimStart().StartsWith(":", StringComparison.Ordinal)
                        && !declarationCode.Substring(0, m.Index).TrimEnd().EndsWith(".", StringComparison.Ordinal))
                    .Select(m => m.Groups[1].Value).Distinct(StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal).ToArray();
                declarations.Add(new SclDeclaration { Name = header.Groups[2].Success ? header.Groups[2].Value : header.Groups[3].Value,
                    Kind = keyword switch { "TYPE" => "UDT", "FUNCTION_BLOCK" => "FB", "FUNCTION" => "FC", "DATA_BLOCK" => "DB", _ => "OB" }, Content = content, References = references });
            }
            return declarations;
        }

        internal static string Instance(string name, string type) => "DATA_BLOCK " + Quote(name) + "\n{ S7_Optimized_Access := 'TRUE' }\nVERSION : 1.0\n"
            + Quote(type) + "\nBEGIN\nEND_DATA_BLOCK\n";

        internal static string Calls(string name, IEnumerable<SclCall> calls)
        {
            var result = new StringBuilder("FUNCTION " + Quote(name) + " : Void\n{ S7_Optimized_Access := 'TRUE' }\nVERSION : 1.0\nBEGIN\n");
            foreach (var call in calls.OrderBy(c => c.Instance, StringComparer.Ordinal))
            {
                var bindings = new List<string>();
                Add(call.Type.Interface.In, ":="); Add(call.Type.Interface.InOut, ":="); Add(call.Type.Interface.Out, "=>");
                result.Append("    ").Append(Quote(call.Instance)).Append('(');
                if (bindings.Count > 0) result.Append('\n').Append(string.Join(",\n", bindings)).Append('\n').Append("    ");
                result.Append(");\n");
                void Add(List<InterfaceMember>? members, string assignment)
                {
                    foreach (var member in (members ?? new List<InterfaceMember>()).OrderBy(m => m.Name, StringComparer.Ordinal))
                        if (call.Bindings.TryGetValue(member.Role, out var value)) bindings.Add("        " + Quote(member.Name) + " " + assignment + " " + Binding(value));
                }
            }
            result.Append("END_FUNCTION\n");
            return result.ToString();
        }

        internal static string Binding(string value)
        {
            if (Regex.IsMatch(value, @"\A(?:TRUE|FALSE|-?[0-9]+(?:\.[0-9]+)?|%[IQ](?:[0-9]+\.[0-7]|[BWDL][0-9]+))\z", RegexOptions.CultureInvariant)) return value;
            var path = Regex.Matches(value, @"(?:\A|\.)(?:""([^""\n]+)""|([A-Za-z_][A-Za-z0-9_-]*))", RegexOptions.CultureInvariant).Cast<Match>().ToArray();
            if (path.Length == 0 || string.Concat(path.Select(m => m.Value)) != value) throw CanonicalJson.Failure("/bind", "scl-binding", "Binding must be a literal or symbolic object/member path.");
            return string.Join(".", path.Select(m => Quote(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value)));
        }

        internal static string BindingRoot(string value)
        {
            var expression = Binding(value);
            var root = Regex.Match(expression, @"\A""([^""\n]+)""", RegexOptions.CultureInvariant);
            return root.Success ? root.Groups[1].Value : "";
        }

        internal static string[] BindingPath(string value) => Regex.Matches(Binding(value), @"""([^""\n]+)""", RegexOptions.CultureInvariant)
            .Cast<Match>().Select(m => m.Groups[1].Value).ToArray();

        internal static void CheckLiteralType(string value, string type)
        {
            var expression = Binding(value);
            if (IoAddress.IsIo(expression))
            {
                if (IoAddress.Parse(expression).Width == GenerationAllocator.Width(type)) return;
            }
            else if (expression == "TRUE" || expression == "FALSE")
            {
                if (string.Equals(type, "Bool", StringComparison.OrdinalIgnoreCase)) return;
            }
            else if (new[] { "SINT", "USINT", "INT", "UINT", "DINT", "UDINT", "LINT", "ULINT", "REAL", "LREAL", "BYTE", "WORD", "DWORD", "LWORD" }.Contains(type.ToUpperInvariant())) return;
            throw CanonicalJson.Failure("/bind/" + value, "type", "Literal/address type differs from its interface parameter.");
        }

        internal static Dictionary<string, string> Members(string content)
        {
            var code = Regex.Replace(content, @"//[^\n]*|/\*[\s\S]*?\*/|'(?:[^']|'')*'", m => new string(m.Value.Select(c => c == '\n' ? '\n' : ' ').ToArray()), RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
            var members = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match field in Regex.Matches(code, @"(?m)^\s*(?:""([^""\n]+)""|([A-Za-z_][A-Za-z0-9_]*))\s*:\s*(?:""([^""\n]+)""|([A-Za-z_][A-Za-z0-9_]*))\s*(?:;|:=)", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)))
            {
                var name = field.Groups[1].Success ? field.Groups[1].Value : field.Groups[2].Value;
                if (name == "VERSION") continue;
                if (members.ContainsKey(name)) throw CanonicalJson.Failure("/sources/members", "duplicate-key", "Duplicate or unsupported inline-structure member: " + name);
                members.Add(name, field.Groups[3].Success ? field.Groups[3].Value : field.Groups[4].Value);
            }
            return members;
        }

        internal static string Quote(string name)
        {
            if (name.Length == 0 || name.Any(c => char.IsControl(c) || c == '"')) throw CanonicalJson.Failure("/names", "scl-name", "Invalid quoted SCL name.");
            return "\"" + name + "\"";
        }
    }
}
