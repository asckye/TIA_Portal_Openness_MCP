using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace TiaMcp.Adapters.Contracts
{
    // The header scan is shared by offline inspection and every native SD admission.
    public sealed class SdBlockDeclaration
    {
        public List<(string Kind, string Name)> Declarations { get; } = new();
        public bool ExpandedLanguage { get; private set; }
        public bool TextualInterface { get; private set; }
        public Dictionary<string, string> Pragmas { get; } = new(StringComparer.OrdinalIgnoreCase);

        public static SdBlockDeclaration Scan(string text)
        {
            var result = new SdBlockDeclaration();
            using var tokens = Tokens(text).GetEnumerator();
            bool header = true, inAttributes = false, afterName = false;
            while (tokens.MoveNext())
            {
                var (token, value) = tokens.Current;
                if (afterName && token == Token.Symbol && value is not (":" or "{" or ";"))
                    throw Invalid("The declared block name must be one exact identifier.");
                afterName = false;
                if (token != Token.Word)
                {
                    if (token == Token.Symbol && value == "{") inAttributes = true;
                    if (token == Token.Symbol && value == "}") inAttributes = false;
                    continue;
                }
                string kind = value.ToUpperInvariant();
                result.TextualInterface |= kind is "S7_TEXTUALINTERFACE" or "S7_TEXTUAL" or "TEXTUALINTERFACE";
                result.ExpandedLanguage |= kind is "SCL" or "FBD";
                if (inAttributes && kind is ("S7_LANGUAGE" or "S7_PREFERREDLANGUAGE" or "S7_EDITORMODE"))
                {
                    if (!tokens.MoveNext() || tokens.Current != (Token.Symbol, ":=") || !tokens.MoveNext() ||
                        tokens.Current.Kind is not (Token.Identifier or Token.Word))
                        throw Invalid("Invalid language pragma " + value + ".");
                    result.ExpandedLanguage |= tokens.Current.Value.ToUpperInvariant() is "SCL" or "FBD";
                }
                else if (kind is "ORGANIZATION_BLOCK" or "FUNCTION" or "FUNCTION_BLOCK" or "DATA_BLOCK" or "TYPE")
                {
                    header = false;
                    if (!tokens.MoveNext() || tokens.Current.Kind is not (Token.Identifier or Token.Word)) throw Invalid("Missing declaration name.");
                    result.Declarations.Add((kind, tokens.Current.Value));
                    afterName = true;
                }
                else if (header && inAttributes && kind is ("S7_BLOCKNUMBER" or "S7_VERSION" or "S7_OPTIMIZED"))
                {
                    value = kind switch { "S7_BLOCKNUMBER" => "S7_BlockNumber", "S7_VERSION" => "S7_Version", _ => "S7_Optimized" };
                    if (!tokens.MoveNext() || tokens.Current != (Token.Symbol, ":=") || !tokens.MoveNext() ||
                        tokens.Current.Kind is not (Token.Identifier or Token.Word) || result.Pragmas.ContainsKey(value))
                        throw Invalid("Invalid or duplicate block pragma " + value + ".");
                    result.Pragmas.Add(value, tokens.Current.Value);
                }
            }
            return result;
        }

        public string RequireBlock(string name)
        {
            if (Declarations.Count != 1 || Declarations[0].Kind == "TYPE" ||
                !string.Equals(Declarations[0].Name, name, StringComparison.OrdinalIgnoreCase))
                throw Invalid("Expected exactly one block declaration named '" + name + "'. Found: " +
                    string.Join(", ", Declarations.Select(x => x.Kind + " " + x.Name)) + ".");
            if (name.IndexOf('$') >= 0)
                throw Invalid("Escaped SD declaration names are not supported for '" + name + "'.");
            if (Pragmas.TryGetValue("S7_BlockNumber", out string? number) &&
                !int.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out _))
                throw Invalid("Invalid S7_BlockNumber for '" + name + "': " + number + ".");
            return Declarations[0].Kind;
        }

        private static AdapterPreconditionException Invalid(string message) => new(message, "name");

        private enum Token { Word, Symbol, Identifier, String }

        private static IEnumerable<(Token Kind, string Value)> Tokens(string text)
        {
            int i = 0;
            while (i < text.Length)
            {
                char c = text[i++];
                if (char.IsWhiteSpace(c) || c == '\uFEFF') continue;
                if (c == '/' && i < text.Length && text[i] == '/')
                {
                    while (i < text.Length && text[i] != '\n' && text[i] != '\r') i++;
                    continue;
                }
                if ((c == '(' || c == '/') && i < text.Length && text[i] == '*')
                {
                    char end = c == '(' ? ')' : '/';
                    int depth = 1;
                    i++;
                    while (i < text.Length && depth > 0)
                    {
                        char next = text[i++];
                        if (next == c && i < text.Length && text[i] == '*') { depth++; i++; }
                        else if (next == '*' && i < text.Length && text[i] == end) { depth--; i++; }
                    }
                    if (depth != 0) throw Invalid("Unclosed SD comment.");
                    continue;
                }
                if (c is '"' or '\'')
                {
                    var value = new StringBuilder();
                    while (true)
                    {
                        if (i == text.Length) throw Invalid("Unclosed SD quoted token.");
                        char next = text[i++];
                        if (next == c && (i == text.Length || text[i] != c)) break;
                        if ((next == c || next == '$') && i < text.Length)
                        {
                            // Preserve the spelling. Escaped identifiers must never alias the inspected basename.
                            value.Append(next);
                            next = text[i++];
                        }
                        value.Append(next);
                    }
                    // Keep quoted tokens distinct from punctuation and keywords.
                    yield return (c == '"' ? Token.Identifier : Token.String, value.ToString());
                    continue;
                }
                if (char.IsLetterOrDigit(c) || c == '_')
                {
                    int start = i - 1;
                    while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_')) i++;
                    yield return (Token.Word, text.Substring(start, i - start));
                }
                else if (c == ':' && i < text.Length && text[i] == '=') { i++; yield return (Token.Symbol, ":="); }
                else yield return (Token.Symbol, c.ToString());
            }
        }
    }

    public static class SdBlockOverridePolicy
    {
        public const string Failure = "BlockPropertiesNotPreserved";
        public const string RejectOverwrite = "Only documented reject-existing ImportDocumentOptions.None is admitted.";
        public static readonly string[] NotCarried = { "supervisions/alarms", "SiVArc", "user-defined attributes",
            "predefined actual parameters", "call visibility", "member-level attributes" };
        private static readonly Dictionary<string, object> DbDefaults = new()
        {
            ["IsOnlyStoredInLoadMemory"] = false, ["IsWriteProtectedInAS"] = false, ["DBAccessibleFromOPCUA"] = true,
            ["DBAccessibleFromWebserver"] = true, ["DownloadWithoutReinit"] = false, ["IsRetainMemResEnabled"] = false, ["MemoryReserve"] = 100
        };
        public static bool Volatile(string key) => key is "CodeModifiedDate" or "CompileDate" or "CreationDate" or "DownloadDate"
            or "InterfaceModifiedDate" or "ModifiedDate" or "ParameterModified" or "StructureModified" or "IsConsistent"
            or "LoadMemoryLength" or "WorkMemoryLength";

        public static string? Refusal(string name, string kind, bool replace, bool elsewhere, IReadOnlyDictionary<string, object?>? before)
        {
            string Refuse(string property, object? current, object? expected) => $"Block '{name}': {property} = {current ?? "<absent>"}; " +
                $"SIMATIC SD import: {expected}. Use a SimaticML export with ImportPlcBlock, ImportPlcBlockVerified or ImportPlcBlocksFromDirectory.";
            if (kind == "ORGANIZATION_BLOCK" && before == null) return Refuse("SecondaryType", null, "ProgramCycle");
            if (replace && elsewhere) return Refuse("group", "another group", "same-name overwrite outside the requested group");
            if (!replace || before == null) return null;
            object? Get(string key) => before.TryGetValue(key, out var value) ? value : null;
            string type = Get("Type") as string ?? "";
            if (type == "OB" && !Equals(Get("SecondaryType"), "ProgramCycle")) return Refuse("SecondaryType", Get("SecondaryType"), "ProgramCycle");
            foreach (var key in new[] { "IsKnowHowProtected", "IsWriteProtected", "IsSafetyBlock", "IsFailsafe" })
                if (before.ContainsKey(key) && !Equals(Get(key), false)) return Refuse(key, Get(key), "preservation unsupported");
            if (Get("ProgrammingLanguage") is string language && language.StartsWith("F_", StringComparison.OrdinalIgnoreCase))
                return Refuse("ProgrammingLanguage", language, "safety block preservation undocumented");
            string expectedType = kind switch { "FUNCTION_BLOCK" => "FB", "FUNCTION" => "FC", "DATA_BLOCK" => "GlobalDB",
                "ORGANIZATION_BLOCK" => "OB", _ => "" };
            if (type != expectedType) return Refuse("Type", type, expectedType);
            if (type == "GlobalDB")
            {
                foreach (var pair in DbDefaults)
                    if (before.TryGetValue(pair.Key, out var value) && !Same(value, pair.Value)) return Refuse(pair.Key, value, pair.Value);
            }
            else if (before.TryGetValue("IsIECCheckEnabled", out var check) && !Equals(check, false)) return Refuse("IsIECCheckEnabled", check, false);
            return null;
        }

        private static bool Same(object? left, object? right) => Equals(left, right) ||
            left is (byte or sbyte or short or ushort or int or uint or long or ulong) &&
            right is (byte or sbyte or short or ushort or int or uint or long or ulong) &&
            Convert.ToDecimal(left, CultureInfo.InvariantCulture) == Convert.ToDecimal(right, CultureInfo.InvariantCulture);

        public static List<Dictionary<string, object?>> Compare(IReadOnlyDictionary<string, object?>? before,
            IReadOnlyDictionary<string, object?> after, SdBlockDeclaration declaration)
        {
            var differences = new List<Dictionary<string, object?>>();
            IEnumerable<string> keys = before != null ? before.Keys.Union(after.Keys) :
                declaration.Pragmas.ContainsKey("S7_BlockNumber") ? new[] { "Number" } : Array.Empty<string>();
            foreach (string key in keys.OrderBy(key => key, StringComparer.Ordinal))
            {
                if (Volatile(key) || key == "ProgrammingLanguage" || key == "HeaderVersion" && declaration.Pragmas.ContainsKey("S7_Version")
                    || key == "MemoryLayout" && declaration.Pragmas.ContainsKey("S7_Optimized")) continue;
                object? old = null;
                bool presentBefore = before?.TryGetValue(key, out old) == true;
                bool presentAfter = after.TryGetValue(key, out var actual);
                object? expected = old;
                if (key == "Number" && declaration.Pragmas.TryGetValue("S7_BlockNumber", out string? requested))
                    expected = int.Parse(requested, CultureInfo.InvariantCulture);
                if ((presentBefore || before == null) && presentAfter && Same(expected, actual)) continue;
                var difference = new Dictionary<string, object?> { ["name"] = key, ["before"] = old, ["after"] = actual, ["restored"] = false };
                if (!Equals(expected, old)) difference["expected"] = expected;
                differences.Add(difference);
            }
            return differences;
        }
    }
}
