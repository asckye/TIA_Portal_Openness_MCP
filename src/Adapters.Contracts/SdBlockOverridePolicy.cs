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

        public string? InstanceType { get; private set; }

        private static bool BlockKeyword(string word) =>
            word is "ORGANIZATION_BLOCK" or "FUNCTION" or "FUNCTION_BLOCK" or "DATA_BLOCK" or "TYPE";

        public static SdBlockDeclaration Scan(string text)
        {
            // An alternate line separator can end a comment or REGION in one reader and conceal the next declaration in another.
            foreach (char c in text)
                if (c is '\u2028' or '\u2029' || char.IsControl(c) && c is not ('\t' or '\r' or '\n'))
                    throw Invalid("Unsupported SD control character or line separator.");
            bool nestedComment = false;
            var result = ScanTokens(Tokens(text, true, () => nestedComment = true));
            if (nestedComment && !CommentHeaders(Tokens(text, true)).SequenceEqual(CommentHeaders(Tokens(text, false))))
                throw Invalid("Nested and non-nested SD comments expose different declarations.");
            return result;
        }

        private static SdBlockDeclaration ScanTokens(IEnumerable<(Token Kind, string Value, int Line)> source)
        {
            var result = new SdBlockDeclaration();
            using var tokens = source.GetEnumerator();
            string? block = null;
            int braces = 0, previousLine = -1;
            bool previousQualifier = false, afterDeclarationName = false;
            bool awaitingDbType = false, possibleNestedDeclaration = false, afterTypeName = false;
            while (tokens.MoveNext())
            {
                var (token, value, line) = tokens.Current;
                bool newLine = line != previousLine;
                previousLine = line;
                string word = token == Token.Word ? value.ToUpperInvariant() : "";
                bool qualified = previousQualifier && !newLine;
                previousQualifier = token == Token.Symbol && value is "#" or "." or "%";
                // A bare keyword followed by a name could start a second block whose predecessor has no END_.
                if (possibleNestedDeclaration && token is (Token.Word or Token.Identifier))
                    throw Invalid("Ambiguous declaration inside " + block + " before its END_ keyword.");
                possibleNestedDeclaration = false;

                if (afterDeclarationName && !CanFollowName(token, value, newLine))
                    throw Invalid("The declared block name must be one exact identifier.");
                afterDeclarationName = false;
                if (token == Token.Region)
                {
                    if (braces != 0) throw Invalid("Unexpected REGION inside an SD attribute list.");
                    continue;
                }
                if (token == Token.Symbol && value == "{") { braces++; continue; }
                if (token == Token.Symbol && value == "}")
                {
                    if (--braces < 0) throw Invalid("Unmatched SD closing brace.");
                    continue;
                }
                if (!qualified) result.TextualInterface |= word is "S7_TEXTUALINTERFACE" or "S7_TEXTUAL" or "TEXTUALINTERFACE";
                if (braces > 0)
                {
                    bool language = word is "S7_LANGUAGE" or "S7_PREFERREDLANGUAGE" or "S7_EDITORMODE";
                    bool headerPragma = result.Declarations.Count == 0 && word is ("S7_BLOCKNUMBER" or "S7_VERSION" or "S7_OPTIMIZED");
                    if (qualified || !(language || headerPragma)) continue;
                    if (!tokens.MoveNext() || tokens.Current is not (Token.Symbol, ":=", _) || !tokens.MoveNext()
                        || tokens.Current.Kind is not (Token.Identifier or Token.Word)) throw Invalid("Invalid block pragma " + value + ".");
                    previousLine = tokens.Current.Line;
                    if (language) result.ExpandedLanguage |= tokens.Current.Value.ToUpperInvariant() is "SCL" or "FBD";
                    else
                    {
                        string key = word switch { "S7_BLOCKNUMBER" => "S7_BlockNumber", "S7_VERSION" => "S7_Version", _ => "S7_Optimized" };
                        if (result.Pragmas.ContainsKey(key)) throw Invalid("Duplicate block pragma " + key + ".");
                        result.Pragmas.Add(key, tokens.Current.Value);
                    }
                    continue;
                }
                if (awaitingDbType)
                {
                    if (token == Token.Symbol && value == ":") continue;
                    if (word is "RETAIN" or "NON_RETAIN") continue;
                    if (token == Token.Identifier || token == Token.Word && word is not ("VAR" or "STRUCT" or "BEGIN" or "END_DATA_BLOCK")
                        && !word.StartsWith("VAR_", StringComparison.Ordinal))
                    {
                        result.InstanceType = value;
                        afterTypeName = true;
                    }
                    awaitingDbType = false;
                }
                else if (afterTypeName)
                {
                    if (token == Token.Symbol && value == ".")
                    {
                        if (!tokens.MoveNext() || tokens.Current.Kind is not (Token.Identifier or Token.Word))
                            throw Invalid("Missing identifier in qualified DB instance type.");
                        result.InstanceType += "." + tokens.Current.Value;
                        previousQualifier = false;
                        previousLine = tokens.Current.Line;
                        continue;
                    }
                    afterTypeName = false;
                }
                if (qualified || token != Token.Word) continue;
                result.ExpandedLanguage |= word is "SCL" or "FBD";
                if (word.StartsWith("END_", StringComparison.Ordinal) && BlockKeyword(word.Substring(4)))
                {
                    if (word != "END_" + block) throw Invalid("Unexpected " + word + ".");
                    block = null;
                }
                else if (BlockKeyword(word))
                {
                    if (block != null) { possibleNestedDeclaration = true; continue; }
                    if (!tokens.MoveNext() || tokens.Current.Kind is not (Token.Identifier or Token.Word)) throw Invalid("Missing declaration name.");
                    previousLine = tokens.Current.Line;
                    result.Declarations.Add((word, tokens.Current.Value));
                    block = word;
                    afterDeclarationName = true;
                    awaitingDbType = word == "DATA_BLOCK";
                }
            }
            if (braces != 0) throw Invalid("Unclosed SD attribute list.");
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

        private enum Token { Word, Symbol, Identifier, String, Region }

        private static bool CanFollowName(Token token, string value, bool newLine) =>
            token != Token.Symbol || value is ":" or "{" or ";" || newLine && value is ("#" or "%");

        // Compare viable header spellings independently of body syntax in the two readings.
        // For example, "DATA_BLOCK X *)" is comment prose: '*' cannot follow a declaration name.
        private static IEnumerable<(string Kind, string Name)> CommentHeaders(IEnumerable<(Token Kind, string Value, int Line)> tokens)
        {
            (Token Kind, string Value, int Line) previous = (Token.Symbol, "", -1);
            string? keyword = null;
            bool qualified = false;
            int braces = 0;
            foreach (var token in tokens.Concat(new[] { (Kind: Token.Symbol, Value: ";", Line: -1) }))
            {
                if (keyword != null && previous.Kind is Token.Word or Token.Identifier && CanFollowName(token.Kind, token.Value, previous.Line != token.Line))
                    yield return (keyword, previous.Value);
                keyword = null;
                if (braces == 0 && !qualified && previous.Kind == Token.Word && BlockKeyword(previous.Value.ToUpperInvariant()))
                    keyword = previous.Value.ToUpperInvariant();
                qualified = previous.Line == token.Line && previous.Kind == Token.Symbol && previous.Value is ("#" or "." or "%");
                if (token.Kind == Token.Symbol && token.Value == "{") braces++;
                if (token.Kind == Token.Symbol && token.Value == "}" && braces > 0) braces--;
                previous = token;
            }
        }

        private static IEnumerable<(Token Kind, string Value, int Line)> Tokens(string text, bool nested, Action? sawNested = null)
        {
            int i = 0, lineOffset = 0, previousLine = -1;
            bool afterBranch = false, afterQualifier = false;
            while (i < text.Length)
            {
                char c = text[i++];
                if (c is '\r' or '\n') { lineOffset = i; continue; }
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
                        if (next is '\r' or '\n') lineOffset = i;
                        if (nested && next == c && i < text.Length && text[i] == '*') { sawNested?.Invoke(); depth++; i++; }
                        else if (next == '*' && i < text.Length && text[i] == end) { depth--; i++; }
                    }
                    if (nested && depth != 0) throw Invalid("Unclosed SD comment.");
                    continue;
                }
                bool lineStart = previousLine != lineOffset;
                bool regionStart = lineStart || afterBranch;
                bool qualifiedWord = !lineStart && afterQualifier;
                afterBranch = false;
                afterQualifier = c is '#' or '.' or '%';
                previousLine = lineOffset;
                if (c is '"' or '\'')
                {
                    var value = new StringBuilder();
                    while (true)
                    {
                        if (i == text.Length || text[i] is '\r' or '\n')
                        {
                            if (nested) throw Invalid("Unclosed SD quoted token before end of line.");
                            break;
                        }
                        char next = text[i++];
                        if (next == c && (i == text.Length || text[i] != c)) break;
                        if ((next == c || next == '$') && i < text.Length)
                        {
                            // Preserve the spelling. Escaped identifiers must never alias the inspected basename.
                            if (text[i] is '\r' or '\n')
                            {
                                if (nested) throw Invalid("Unclosed SD escape before end of line.");
                                break;
                            }
                            value.Append(next);
                            next = text[i++];
                        }
                        value.Append(next);
                    }
                    // Keep quoted tokens distinct from punctuation and keywords.
                    yield return (c == '"' ? Token.Identifier : Token.String, value.ToString(), lineOffset);
                    continue;
                }
                if (char.IsLetterOrDigit(c) || c == '_')
                {
                    int start = i - 1;
                    while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_')) i++;
                    string word = text.Substring(start, i - start);
                    afterBranch = !qualifiedWord && (word.Equals("THEN", StringComparison.OrdinalIgnoreCase)
                        || word.Equals("ELSE", StringComparison.OrdinalIgnoreCase));
                    if (word.Equals("REGION", StringComparison.OrdinalIgnoreCase) && regionStart)
                    {
                        while (i < text.Length && text[i] is not ('\r' or '\n')) i++;
                        yield return (Token.Region, word, lineOffset);
                    }
                    else yield return (Token.Word, word, lineOffset);
                }
                else if (c == ':' && i < text.Length && text[i] == '=') { i++; yield return (Token.Symbol, ":=", lineOffset); }
                else yield return (Token.Symbol, c.ToString(), lineOffset);
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
        public static bool RequiredAttribute(string key, string type) =>
            key is "Type" or "Number" or "AutoNumber" or "ProgrammingLanguage" or "IsKnowHowProtected" or "IsWriteProtected" or "IsSafetyBlock" or "IsFailsafe"
            || type == "OB" && key == "SecondaryType" || (type == "GlobalDB" ? DbDefaults.ContainsKey(key) : key == "IsIECCheckEnabled");

        public static bool Volatile(string key) => key is "CodeModifiedDate" or "CompileDate" or "CreationDate" or "DownloadDate"
            or "InterfaceModifiedDate" or "ModifiedDate" or "ParameterModified" or "StructureModified" or "IsConsistent"
            or "LoadMemoryLength" or "WorkMemoryLength";

        public static string? Refusal(string name, string kind, bool replace, bool elsewhere, IReadOnlyDictionary<string, object?>? before,
            string? instanceType = null)
        {
            string Refuse(string property, object? current, object? expected) => $"Block '{name}': {property} = {current ?? "<absent>"}; " +
                $"SIMATIC SD import: {expected}. Use a SimaticML export with ImportPlcBlock, ImportPlcBlockVerified or ImportPlcBlocksFromDirectory.";
            if (kind == "ORGANIZATION_BLOCK" && before == null) return Refuse("SecondaryType", null, "ProgramCycle");
            if (replace && elsewhere) return Refuse("group", "another group", "same-name overwrite outside the requested group");
            if (before == null) return null;
            object? Get(string key) => before.TryGetValue(key, out var value) ? value : null;
            string type = Get("Type") as string ?? "";
            if (type == "GlobalDB" && instanceType != null) return Refuse("Type", type, "InstanceDB of " + instanceType);
            if (!replace) return null;
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
            => Compare(before, after, declaration, null);

        public static List<Dictionary<string, object?>> Compare(IReadOnlyDictionary<string, object?>? before,
            IReadOnlyDictionary<string, object?> after, SdBlockDeclaration declaration, ISet<string>? notCompared)
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
                if (presentBefore && presentAfter && notCompared?.Contains(key) == true) continue;
                if (key == "AutoNumber" && old is true && actual is false && declaration.Pragmas.ContainsKey("S7_BlockNumber")) continue;
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
