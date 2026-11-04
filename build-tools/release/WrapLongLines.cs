using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace TiaMcp.ReleaseTool;

internal static class WrapLongLines
{
    internal static readonly string[] ExcludedFiles =
    [
        // CatalogData and Snapshot-ToolContracts.py read one registration per line.
        "src/FoundationHost/FoundationTools.cs", "src/FoundationHost/Offline*Tools.cs",
        // CatalogData and ToolsListChecks read one classification per line.
        "src/Logic/ModelContextProtocol/ToolMetadata*.cs",
        // ToolsListChecks.Classifier splits single-line conditions on ||.
        "src/Logic/ModelContextProtocol/ToolTaxonomy.cs",
        // BuildCommon/PortedFamilies and ported_families.py read the source table.
        "src/Adapters.Contracts/PortedFamilies.cs",
        // BundleLayoutChecks reads the resource table.
        "src/Shared/BundleLayout.cs"
    ];

    internal static readonly string[] NoLiteralSplitFiles =
    [
        // OptionalEngineeringPolicyParityTests compares the literal tokens.
        "src/Logic/Siemens/StartdriveLogic.cs", "src/Logic/Siemens/SivarcLogic.cs", "src/Logic/Siemens/DccLogic.cs",
        "src/Adapters/Native/Optional/StartdrivePolicy.cs", "src/Adapters/Native/Optional/SivarcPolicy.cs",
        "src/Adapters/Native/Optional/DccPolicy.cs",
        // AnalyzerWarnings baseline members include Description argument tokens.
        "src/Shared/HardwareNetworkTools.cs",
        // AnalyzerWarnings anchors the ArgumentException in ImportStagingStore.Validate.
        "src/Logic/ModelContextProtocol/ImportStagingStore.cs"
    ];

    internal static readonly string[] PinnedFragments =
    [
        // SwallowedExceptions.Scan requires the marker on the catch line or first in its block.
        "swallow(",
        // ReadOnlySourceContracts uses exact signatures, call expressions and native call counts.
        "public OnlineStateResult GoOnline(", "connection.Projects.Retrieve",
        "ProjectDeviceWalk.", "HardwarePrimitives.CreateWithItem(",
        "DeviceCreationRules.TiaRefusal(", "DeviceCreationRules.ExactCandidate(",
        // DocumentImportSafetySources pins the primitive declaration and batch boundary expressions.
        "public static DocumentImportResultForBlocks Import(",
        "PlcExchangeContract.RunAsync(\"ImportPlcBlocksDocuments\"",
        "result == null || !TiaMcp.Adapters.Contracts.NativeResultStates.Succeeded",
        // PlcSoftwareAuditWiring tracks the exact native import mode and mutation order.
        "overwrite ? ImportOptions.Override : ImportOptions.None", "ImportOptions.None);",
        // UnifiedScriptSyntaxCheckTests.Declaration checks the exact default-argument suffix.
        "Description(\"syntaxCheck:"
    ];

    private static bool Matches(string path, string[] patterns) => patterns.Any(pattern =>
        Regex.IsMatch(path.Replace('\\', '/'), "^" + Regex.Escape(pattern).Replace("\\*", "[^/]*") + "$"));

    internal static SyntaxToken[] Lex(string source, bool includeEnd = false) => SyntaxFactory.ParseTokens(
        Regex.Replace(source, @"(?m)^[ \t]*#[^\r\n]*", m => new string(' ', m.Length)))
        .Where(t => includeEnd || !t.IsKind(SyntaxKind.EndOfFileToken)).ToArray();

    private static bool Plain(SyntaxToken token) => token.IsKind(SyntaxKind.StringLiteralToken)
        && token.Text.StartsWith('"') && !token.Text.StartsWith("\"\"\"", StringComparison.Ordinal);

    // Merge only exact spelling-preserving literal pieces. Value equality alone would
    // accidentally bless changed escapes, comments or unrelated token substitutions.
    internal static bool Equivalent(string before, string after)
    {
        var left = Lex(before); var right = Lex(after);
        int j = 0;
        foreach (var token in left)
        {
            if (j >= right.Length) return false;
            if (token.RawKind == right[j].RawKind && token.Text == right[j].Text) { j++; continue; }
            if (!Plain(token) || !Plain(right[j])) return false;
            var interior = right[j++].Text[1..^1];
            while (interior != token.Text[1..^1] && j + 1 < right.Length
                && right[j].IsKind(SyntaxKind.PlusToken) && Plain(right[j + 1]))
            {
                interior += right[j + 1].Text[1..^1]; j += 2;
            }
            if (interior != token.Text[1..^1]) return false;
        }
        static string[] Comments(SyntaxToken[] tokens) => tokens.SelectMany(t => t.LeadingTrivia.Concat(t.TrailingTrivia))
            .Where(t => !t.IsKind(SyntaxKind.WhitespaceTrivia) && !t.IsKind(SyntaxKind.EndOfLineTrivia))
            .Select(t => t.ToFullString()).ToArray();
        return j == right.Length && Comments(Lex(before, true)).SequenceEqual(Comments(Lex(after, true)))
            && Regex.Matches(before, @"(?m)^[ \t]*#[^\r\n]*").Select(m => m.Value)
                .SequenceEqual(Regex.Matches(after, @"(?m)^[ \t]*#[^\r\n]*").Select(m => m.Value));
    }

    internal static string Format(string source, string path, int maximum = 160)
    {
        if (maximum < 1) throw new ReleaseException("-MaxLength must be positive", 64);
        if (Matches(path, ExcludedFiles)) return source;
        var lines = SourceText.From(source).Lines;
        var tokens = Lex(source, true);
        var protectedLines = new HashSet<int>();
        foreach (var span in tokens.Select(t => t.Span).Concat(tokens.SelectMany(t => t.LeadingTrivia.Concat(t.TrailingTrivia)).Select(t => t.Span)))
        {
            int first = lines.GetLineFromPosition(span.Start).LineNumber;
            int last = lines.GetLineFromPosition(Math.Max(span.Start, span.End - 1)).LineNumber;
            if (last > first) for (int i = first; i <= last; i++) protectedLines.Add(i);
        }
        var output = new StringBuilder();
        int tokenIndex = 0;
        foreach (var line in lines)
        {
            var value = line.ToString();
            bool protect = protectedLines.Contains(line.LineNumber) || value.TrimStart().StartsWith('#')
                || PinnedFragments.Any(value.Contains);
            while (tokenIndex < tokens.Length && tokens[tokenIndex].Span.End <= line.Start) tokenIndex++;
            int after = tokenIndex;
            while (after < tokens.Length && tokens[after].SpanStart < line.End) after++;
            string? preceding = tokenIndex > 0 ? tokens[tokenIndex - 1].Text : null;
            string? following = after < tokens.Length ? tokens[after].Text : null;
            output.Append(protect ? value : Wrap(value, maximum, !Matches(path, NoLiteralSplitFiles), preceding, following));
            output.Append(source.AsSpan(line.End, line.EndIncludingLineBreak - line.End));
        }
        string result = output.ToString();
        if (!Equivalent(source, result)) throw new ReleaseException("Token/comment mismatch; file left untouched: " + path);
        return result;
    }

    private sealed record Break(int Position, int Depth, bool Statement);

    private static string Wrap(string line, int maximum, bool literals, string? preceding = null, string? following = null,
        int originalIndent = -1, int initialDepth = 0)
    {
        if (line.Length <= maximum) return line;
        var tokens = Lex(line);
        if (tokens.Length == 0 || tokens.Any(t => t.IsKind(SyntaxKind.InterpolatedStringStartToken)
            || t.IsKind(SyntaxKind.InterpolatedVerbatimStringStartToken))) return line;
        int indent = line.Length - line.TrimStart().Length;
        if (originalIndent < 0) originalIndent = indent;
        var candidates = new List<Break>();
        var stack = new Stack<(string Text, bool Header)>();
        int depth = initialDepth;
        for (int i = 0; i < tokens.Length; i++)
        {
            var t = tokens[i]; string text = t.Text;
            string prev = i > 0 ? tokens[i - 1].Text : "";
            bool header = false;
            if (text is ")" or "]" or "}")
            {
                if (stack.Count > 0) header = stack.Pop().Header;
                depth = Math.Max(0, depth - 1);
            }
            if (text == "}" && i > 0) candidates.Add(new(t.SpanStart, depth, true));
            if (text is "&&" or "||" or "??" or "?" or ":" or "+" || text == ".")
                candidates.Add(new(t.SpanStart, depth, false));
            if (text is "," or "=>" or "=" || text == "else" || text == ")" && header)
                candidates.Add(new(t.Span.End, depth, false));
            if (text == ";" && stack.All(entry => entry.Text == "{") || text == "{")
                candidates.Add(new(t.Span.End, depth, true));
            if (text is "(" or "[" or "{")
            {
                stack.Push((text, text == "(" && prev is "if" or "for" or "foreach" or "while" or "using" or "lock"));
                depth++;
            }
        }
        // Keep comments on the final fragment, following the same last token.
        var usable = candidates.Where(c => c.Position > indent && c.Position < tokens[^1].Span.End
            && line[..c.Position].TrimEnd().Length < line.TrimEnd().Length).ToArray();
        if (usable.Length > 0)
        {
            int outer = usable.Min(c => c.Depth);
            var level = usable.Where(c => c.Depth == outer).ToArray();
            var cut = level.Where(c => c.Position <= maximum).MaxBy(c => c.Position) ?? level.MinBy(c => c.Position)!;
            string head = line[..cut.Position].TrimEnd();
            bool opensBlock = head.EndsWith('{');
            bool closesBlock = line[cut.Position..].TrimStart().StartsWith('}');
            int continuation = originalIndent + 4 * (cut.Statement ? cut.Depth + (opensBlock ? 1 : 0) : cut.Depth + 1);
            string tail = new string(' ', continuation) + line[cut.Position..].TrimStart();
            if (head.TrimStart().Length < line.TrimStart().Length && tail.TrimStart().Length < line.TrimStart().Length)
            {
                string? beforeTail = tokens.LastOrDefault(t => t.Span.End <= cut.Position).Text ?? preceding;
                string? afterHead = tokens.FirstOrDefault(t => t.SpanStart >= cut.Position).Text ?? following;
                return Wrap(head, maximum, literals, preceding, afterHead, originalIndent, initialDepth) + "\n"
                    + Wrap(tail, maximum, literals, beforeTail, following, originalIndent,
                        cut.Depth + (opensBlock || closesBlock ? 1 : 0));
            }
        }
        if (literals)
        {
            for (int i = 0; i < tokens.Length; i++)
            {
                var t = tokens[i];
                if (!Plain(t) || t.Text.Any(c => c > 127) || t.ValueText.Any(c => c > 127)
                    || (i > 0 ? tokens[i - 1].Text : preceding) is not ("(" or "," or "=" or "=>" or "return" or "??" or "?" or ":" or "{" or "[" or "+")
                    || (i + 1 < tokens.Length ? tokens[i + 1].Text : following) is not (")" or "," or ";" or "]" or "}" or "+" or ":")) continue;
                int room = maximum - t.SpanStart - 4;
                int at = -1;
                for (int k = 1; k < t.Text.Length - 2 && k <= room; k++)
                    if (t.Text[k] == ' ' && t.Text[k - 1] != '\\') at = k;
                if (at < 0) continue;
                string head = line[..t.SpanStart] + t.Text[..(at + 1)] + "\" +";
                string tail = new string(' ', originalIndent + 4 * (initialDepth + 1)) + "\"" + t.Text[(at + 1)..] + line[t.Span.End..];
                if (tail.Length < line.Length)
                    return head + "\n" + Wrap(tail, maximum, literals, "+", following, originalIndent, initialDepth);
            }
        }
        return line;
    }

    internal static string Reason(string line, string path)
    {
        if (Matches(path, ExcludedFiles)) return "excluded-file";
        if (PinnedFragments.Any(line.Contains)) return "pinned";
        var tokens = Lex(line);
        if (tokens.Any(t => t.Kind().ToString().Contains("Interpolated", StringComparison.Ordinal))) return "interpolated";
        if (tokens.Any(t => t.Text.StartsWith("@\"", StringComparison.Ordinal) || t.Text.StartsWith("\"\"\"", StringComparison.Ordinal)))
            return "verbatim-or-raw";
        if (tokens.Any(t => Plain(t) && (t.Text.Any(c => c > 127) || t.ValueText.Any(c => c > 127)))) return "non-ascii-literal";
        if (Lex(line, true).SelectMany(t => t.LeadingTrivia.Concat(t.TrailingTrivia)).Any(t =>
            t.IsKind(SyntaxKind.SingleLineCommentTrivia) || t.IsKind(SyntaxKind.MultiLineCommentTrivia)
            || t.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia))) return "comment";
        return "no-break-point";
    }

    internal static bool Rewrite(string path, Func<string, string> format, bool check)
    {
        var source = File.ReadAllText(path);
        var result = format(source);
        if (!Equivalent(source, result)) throw new ReleaseException("Token/comment mismatch; file left untouched: " + path);
        if (result == source) return false;
        if (!check) File.WriteAllText(path, result, new UTF8Encoding(false));
        return true;
    }

    internal static int Run(string root, Options options)
    {
        root = Path.GetFullPath(options.Get("Root", root));
        int maximum = int.Parse(options.Get("MaxLength", "160"), System.Globalization.CultureInfo.InvariantCulture);
        var paths = options.All("Path");
        if (paths.Count == 0) throw new ReleaseException("At least one -Path is required", 64);
        var files = paths.Select(p => Path.GetFullPath(p, root)).SelectMany(p => Directory.Exists(p)
            ? Directory.EnumerateFiles(p, "*.cs", SearchOption.AllDirectories) : File.Exists(p) ? [p] : throw new FileNotFoundException(p))
            .Where(p => p.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                && !Path.GetRelativePath(root, p).Split(Path.DirectorySeparatorChar).Any(c => c is "bin" or "obj"))
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToArray();
        int changed = 0;
        var remaining = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            string path = Path.GetRelativePath(root, file).Replace('\\', '/');
            string result = "";
            if (Rewrite(file, source => result = Format(source, path, maximum), options.Has("Check"))) changed++;
            var resultLines = SourceText.From(result).Lines;
            var multilineReasons = new Dictionary<int, string>();
            foreach (var token in Lex(result, true))
            {
                string? kind = token.Text.StartsWith("@\"", StringComparison.Ordinal)
                    || token.Text.StartsWith("\"\"\"", StringComparison.Ordinal) ? "verbatim-or-raw" : null;
                if (kind != null)
                    for (int n = resultLines.GetLineFromPosition(token.SpanStart).LineNumber;
                        n <= resultLines.GetLineFromPosition(Math.Max(token.SpanStart, token.Span.End - 1)).LineNumber; n++)
                        multilineReasons[n] = kind;
                foreach (var trivia in token.LeadingTrivia.Concat(token.TrailingTrivia).Where(t =>
                    t.IsKind(SyntaxKind.MultiLineCommentTrivia) || t.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia)))
                    for (int n = resultLines.GetLineFromPosition(trivia.SpanStart).LineNumber;
                        n <= resultLines.GetLineFromPosition(Math.Max(trivia.SpanStart, trivia.Span.End - 1)).LineNumber; n++)
                        multilineReasons[n] = "comment";
            }
            int number = 0;
            foreach (var line in resultLines)
            {
                number++;
                if (line.Span.Length <= maximum) continue;
                string reason = Reason(line.ToString(), path);
                if (reason is not ("excluded-file" or "pinned"))
                    reason = multilineReasons.GetValueOrDefault(line.LineNumber) ?? reason;
                if (!remaining.TryGetValue(reason, out var entries)) remaining[reason] = entries = [];
                entries.Add(path + ":" + number);
            }
        }
        foreach (var reason in new[] { "interpolated", "verbatim-or-raw", "non-ascii-literal", "comment", "excluded-file", "pinned", "no-break-point" })
        {
            var entries = remaining.GetValueOrDefault(reason) ?? [];
            Console.WriteLine($"{reason}: {entries.Count} lines in {entries.Select(e => e[..e.LastIndexOf(':')]).Distinct().Count()} files");
            foreach (var entry in entries) Console.WriteLine("  " + entry);
        }
        Console.WriteLine($"wrap-long-lines: {changed} changed / {files.Length} files");
        return options.Has("Check") && changed != 0 ? 1 : 0;
    }
}
