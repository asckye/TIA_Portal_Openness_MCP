using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using TiaMcp.BuildCommon;

namespace TiaMcp.ReleaseTool;

internal static class DeadToolReferences
{
    private static readonly JsonNode Policy = JsonNode.Parse(typeof(DeadToolReferences).Assembly.GetManifestResourceStream("TiaMcp.ReleaseTool.dead-reference-policy.json")!)!;
    internal static HashSet<string> Allowed { get; } = Policy["ALLOWED"]!.AsObject().Select(p => p.Key).ToHashSet(StringComparer.Ordinal);
    private static readonly Regex Verb = new(@"\A(Get|Set|Add|Import|Export|Create|Delete|Compile|Download|Sync|Analyze|Build|Write|Read|Ensure|Find|List|Describe|Invoke|Generate|Apply|Bind|Move|Rename|Save|Open|Close|Connect|Run|Check|Validate|Preview|Preflight|Scaffold|Attach)[A-Z]");
    private static readonly Regex Mention = new(@"(?<![.\w])([A-Z][A-Za-z0-9]{3,})\b");
    private static readonly Regex Directed = new(@"\b(?:[Uu]se|[Cc]all|[Ii]nvoke|[Rr]un|[Tt]ry|[Ss]ee|via)\s+(?:the\s+)?([A-Z][A-Za-z0-9]+)");
    internal static HashSet<string> ContractNames(string root, string folder) => Directory.EnumerateFiles(Path.Combine(root, folder), "*.json").SelectMany(p => JsonNode.Parse(File.ReadAllText(p))!["tools"]!.AsArray().Select(t => t!["name"]!.GetValue<string>())).ToHashSet(StringComparer.Ordinal);
    internal static IEnumerable<(int Start, string Text, bool Description)> GuidanceLiterals(string source)
    {
        IEnumerable<(int, string, bool)> Visit(IReadOnlyList<Token> tokens, bool inherited)
        {
            var ranges = McpText.SinkRanges(tokens, MatchingPairs.Find(tokens));
            var i = 0;
            while (i < tokens.Count)
            {
                var token = tokens[i];
                if (token.Kind != "literal") { i++; continue; }
                var description = inherited || ranges.Any(r => r.Lo < i && i < r.Hi && r.Kind == "description");
                var text = McpText.LiteralParts(token).Text;
                if (token.Expressions.Count > 0) foreach (var nested in Visit(token.Expressions, description)) yield return nested;
                while (i + 2 < tokens.Count && tokens[i + 1].Value == "+" && tokens[i + 2].Kind == "literal")
                {
                    i += 2; text += McpText.LiteralParts(tokens[i]).Text;
                    if (tokens[i].Expressions.Count > 0) foreach (var nested in Visit(tokens[i].Expressions, description)) yield return nested;
                }
                if (description || !Regex.IsMatch(text, @"\A[A-Za-z_]\w*\z")) yield return (token.OffsetStart, text, description);
                i++;
            }
        }
        return Visit(new CSharpLexer(source).Scan().Tokens, false);
    }
    internal static (HashSet<string> Names, Dictionary<string, List<string>> Bad) Scan(IReadOnlyDictionary<string, string> sources, ISet<string> historical, string? extra = null)
    {
        var names = new HashSet<string>(); var prompts = new HashSet<string>();
        foreach (var source in sources.Values)
        {
            foreach (Match m in Regex.Matches(source, "McpServerTool\\(Name\\s*=\\s*\"([A-Za-z0-9_]+)\"")) names.Add(m.Groups[1].Value);
            foreach (Match m in Regex.Matches(source, "McpServerPrompt\\(Name\\s*=\\s*\"([A-Za-z0-9_]+)\"")) prompts.Add(m.Groups[1].Value);
        }
        var bad = new Dictionary<string, List<string>>();
        var items = sources.ToList(); if (extra is not null) items.Add(new("<sentinel>", extra));
        void Add(string name, string location) { if (!bad.TryGetValue(name, out var locations)) bad[name] = locations = []; locations.Add(location); }
        foreach (var (path, source) in items)
            foreach (var (start, text, description) in GuidanceLiterals(source))
            {
                var location = Path.GetFileName(path) + ":" + SourceCheck.Line(source, start);
                var mentioned = Mention.Matches(text).Select(m => m.Groups[1].Value).ToHashSet();
                var candidates = description ? mentioned : mentioned.Intersect(historical).Union(Directed.Matches(text).Select(m => m.Groups[1].Value)).ToHashSet();
                foreach (var name in candidates)
                    if (!names.Contains(name) && !prompts.Contains(name) && !Allowed.Contains(name) && (historical.Contains(name) || Verb.IsMatch(name))) Add(name, location);
                foreach (var (tool, parameter) in new[] { ("CallTool", "argumentsJson"), ("PreviewToolCall", "argumentsJson"), ("RunReadOnlyToolBatch", "operationsJson"), ("PreviewToolBatch", "operationsJson") })
                    if (Regex.IsMatch(text, @"\b" + tool + @"\s*\([^)]*\b" + parameter + @"\b")) Add(tool + "." + parameter, location);
            }
        return (names, bad);
    }
    internal static Dictionary<string, List<(string Name, string Location)>> Duplicates(IReadOnlyDictionary<string, string> sources, string? extra = null)
    {
        var items = sources.ToList(); if (extra is not null) items.Add(new("<sentinel>", extra));
        return items.SelectMany(p => Regex.Matches(p.Value, "McpServerTool\\(Name\\s*=\\s*\"([A-Za-z0-9_]+)\"").Select(m => (Name: m.Groups[1].Value, Location: Path.GetFileName(p.Key) + ":" + SourceCheck.Line(p.Value, m.Index))))
            .GroupBy(p => p.Name.ToLowerInvariant()).Where(g => g.Count() > 1).ToDictionary(g => g.Key, g => g.ToList());
    }
    internal static Dictionary<string, string> MigrationNames(string root)
    {
        var document = Repository.ReadSource(Path.Combine(root, "docs/development/phase6-review.md"));
        var a = document.IndexOf("A. ", StringComparison.Ordinal); if (a >= 0) document = document[(a + 3)..];
        var b = document.IndexOf("B. ", StringComparison.Ordinal); if (b >= 0) document = document[..b];
        return Regex.Matches(document, @"^\| `([^`]+)` \| `([^`]+)` \|", RegexOptions.Multiline).ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value);
    }
    internal static bool SkipHistory(string path) => DeliveryRules.Strings(Policy["HISTORY_SKIP_FILES"]).Contains(path) || DeliveryRules.Strings(Policy["HISTORY_SKIP_DIRS"]).Any(p => path.StartsWith(p, StringComparison.Ordinal));
    internal static IEnumerable<(int Line, string Text)> MarkdownLines(string path, string source)
    {
        var section = Policy["HISTORY_SKIP_SECTIONS"]![path]?.AsArray();
        var skipping = false; var fence = false; var line = 0;
        foreach (var text in source.Split('\n'))
        {
            line++;
            if (section is not null && text.StartsWith(section[0]!.GetValue<string>(), StringComparison.Ordinal)) skipping = true;
            if (skipping && section is not null && text.StartsWith(section[1]!.GetValue<string>(), StringComparison.Ordinal)) { skipping = false; continue; }
            if (skipping) continue;
            if (Regex.IsMatch(text, @"^\s*(```|~~~)")) { fence = !fence; continue; }
            if (fence || text.TrimStart().StartsWith('|')) { yield return (line, text); continue; }
            var invocation = Regex.IsMatch(text, "\\b[A-Z][A-Za-z0-9]+\\s*\\(|[\"'](?:name|toolName)[\"']\\s*:");
            if (DeliveryRules.Strings(Policy["ARCHITECTURE_MEMBER_RECORDS"]).Contains(path)) { if (invocation) yield return (line, text); continue; }
            foreach (Match code in Regex.Matches(text, @"`+([^`]+)`+")) yield return (line, code.Groups[1].Value);
            if (invocation) yield return (line, text);
        }
    }
    internal static Dictionary<string, List<string>> ScanMarkdown(IReadOnlyDictionary<string, string> documents, IReadOnlyDictionary<string, string> retired)
    {
        var bad = new Dictionary<string, List<string>>();
        if (retired.Count == 0) return bad;
        var pattern = new Regex(@"(?<![.\w])(" + string.Join("|", retired.Keys.OrderByDescending(n => n.Length).Select(Regex.Escape)) + @")(?!\w)");
        var allowed = Allowed.Concat(Policy["CLI_COMMANDS"]!.AsObject().Select(p => p.Key)).ToHashSet();
        foreach (var (path, source) in documents)
        {
            if (SkipHistory(path)) continue;
            foreach (var (line, text) in MarkdownLines(path, source))
                foreach (Match match in pattern.Matches(text))
                {
                    var name = match.Groups[1].Value;
                    if (allowed.Contains(name) || path.StartsWith("reference/siemens-openness/", StringComparison.Ordinal) && Policy["OPENNESS_REFERENCE_NAMES"]!.AsObject().ContainsKey(name)) continue;
                    var location = path + ":" + line;
                    if (!bad.TryGetValue(name, out var locations)) bad[name] = locations = [];
                    if (!locations.Contains(location)) locations.Add(location);
                }
        }
        return bad;
    }
    internal sealed record EditEvent(int Start, string Old, string New, bool Changed);
    internal static (string Source, List<EditEvent> Events) RewriteGuidance(string source, IReadOnlyDictionary<string, string> renames, ISet<string> registered)
    {
        var active = renames.Where(p => p.Key != p.Value && !registered.Contains(p.Key) && registered.Contains(p.Value) && !Allowed.Contains(p.Key)).ToDictionary(p => p.Key, p => p.Value);
        if (active.Count == 0) return (source, []);
        var pattern = new Regex(@"(?<![.\w])(?:" + string.Join("|", active.Keys.OrderByDescending(n => n.Length).Select(Regex.Escape)) + @")\b");
        var eligible = new List<(int Lo, int Hi, IReadOnlyList<Token> Nested)>();
        void Visit(IReadOnlyList<Token> tokens, bool inherited)
        {
            var ranges = McpText.SinkRanges(tokens, MatchingPairs.Find(tokens));
            for (var i = 0; i < tokens.Count; i++)
            {
                var token = tokens[i]; if (token.Kind != "literal") continue;
                var decoded = McpText.LiteralParts(token).Text;
                var sink = inherited || ranges.Any(r => r.Lo < i && i < r.Hi && r.Kind is "description" or "exception" or "message" or "meta");
                var directed = Regex.IsMatch(decoded, @"\b(?:[Uu]se|[Cc]all|[Ii]nvoke|[Rr]un|[Tt]ry|[Ss]ee|via)\s+");
                var prose = Regex.IsMatch(decoded, @"\s") && pattern.IsMatch(decoded);
                if (decoded.TrimStart().StartsWith('{') || decoded.TrimStart().StartsWith('[')) try { JsonNode.Parse(decoded); prose = false; } catch (System.Text.Json.JsonException) { }
                if ((sink || directed || prose) && !Regex.IsMatch(decoded, @"\A\s*[A-Za-z_]\w*\s*\z")) eligible.Add((token.OffsetStart, token.OffsetEnd, token.Expressions));
                if (token.Expressions.Count > 0) Visit(token.Expressions, sink);
            }
        }
        Visit(new CSharpLexer(source).Scan().Tokens, false);
        var events = new List<EditEvent>(); var edits = new List<(int Start, int End, string Value)>();
        foreach (Match match in pattern.Matches(source))
        {
            var start = match.Index; var end = start + match.Length;
            var change = eligible.Any(r => r.Lo <= start && end <= r.Hi && !r.Nested.Any(t => t.OffsetStart <= start && start < t.OffsetEnd));
            events.Add(new(start, match.Value, active[match.Value], change));
            if (change) edits.Add((start, end, active[match.Value]));
        }
        foreach (var (start, decoded, _) in GuidanceLiterals(source))
            foreach (Match match in pattern.Matches(decoded))
                if (!events.Any(e => e.Start >= start && SourceCheck.Line(source, e.Start) == SourceCheck.Line(source, start) && e.Old == match.Value)) events.Add(new(start, match.Value, active[match.Value], false));
        foreach (var edit in edits.AsEnumerable().Reverse()) source = source[..edit.Start] + edit.Value + source[edit.End..];
        return (source, events);
    }
    internal static int Run(string root, bool fix)
    {
        var sources = new Dictionary<string, string>();
        foreach (var folder in new[] { "src/Engine", "src/Logic", "src/Shared" }) foreach (var path in SourceCheck.Files(Path.Combine(root, folder)).Where(p => p.EndsWith(".cs", StringComparison.Ordinal))) sources[path] = Repository.ReadSource(path);
        if (sources.Count == 0) return 2;
        var historical = ContractNames(root, "manifest/history/contracts-v3/baseline");
        var current = ContractNames(root, "manifest/contracts/v4/baseline");
        var renames = MigrationNames(root);
        if (fix)
        {
            var registered = Scan(sources, historical).Names;
            foreach (var (path, source) in sources.OrderBy(p => p.Key, StringComparer.Ordinal).ToArray())
            {
                var result = RewriteGuidance(source, renames, registered);
                foreach (var edit in result.Events) Console.WriteLine($"{(edit.Changed ? "REWRITE" : "KEEP")} {path}:{SourceCheck.Line(source, edit.Start)}: {edit.Old} -> {edit.New}");
                if (result.Source != source) { File.WriteAllText(path, result.Source, new UTF8Encoding(false)); sources[path] = result.Source; }
            }
        }
        var names = Scan(sources, historical).Names;
        var sentinel = names.Order(StringComparer.Ordinal).First().ToUpperInvariant();
        if (!Duplicates(sources, $"[McpServerTool(Name = \"{sentinel}\"), Description(\"sentinel\")]").ContainsKey(sentinel.ToLowerInvariant()) || !Scan(sources, historical, "[McpServerTool(Name = \"SentinelTool\"), Description(\"Use GetNonexistentSentinelTool first.\")]").Bad.ContainsKey("GetNonexistentSentinelTool")) throw new ReleaseException("Dead reference sentinel failed", 2);
        var errors = Duplicates(sources).Select(p => "Duplicate tool name: " + p.Key).ToList();
        errors.AddRange(Scan(sources, historical).Bad.Select(p => p.Key + ": " + string.Join(", ", p.Value.Distinct().Take(4))));
        var listing = ProcessRunner.Run("git", ["ls-files", "--cached", "--others", "--exclude-standard", "-z", "--", "*.md"], root);
        var documents = (listing.ExitCode == 0 ? listing.StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries) : Directory.EnumerateFiles(root, "*.md", SearchOption.AllDirectories).Select(p => Path.GetRelativePath(root, p).Replace('\\', '/'))).Where(p => File.Exists(Path.Combine(root, p))).Distinct().ToDictionary(p => p, p => Repository.ReadSource(Path.Combine(root, p)));
        var retired = renames.Where(p => historical.Contains(p.Key) && !current.Contains(p.Key) && p.Key != p.Value).ToDictionary(p => p.Key, p => p.Value);
        errors.AddRange(ScanMarkdown(documents, retired).Select(p => p.Key + ": " + string.Join(", ", p.Value.Take(4))));
        return SourceCheck.Report("Dead tool references", errors, $"{names.Count} registered tools; {sources.Count} source files; sentinels passed.");
    }
}
