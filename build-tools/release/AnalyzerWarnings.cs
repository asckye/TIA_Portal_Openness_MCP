using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace TiaMcp.ReleaseTool;

internal sealed record AnalyzerWarning(string Rule, string Path, int Line, int Column, int EndLine, int EndColumn,
    string Message, string Member, string Anchor, string Fingerprint, string Reason = "", string Retirement = "", string[]? Contexts = null);

internal sealed record AnalyzerWarningBaseline(int Format, AnalyzerWarning[] Entries);
internal sealed record AnalyzerWarningCollection(AnalyzerWarning[] Entries, HashSet<string> Sources,
    Dictionary<string, HashSet<string>>? Coverage = null);

internal static class AnalyzerWarnings
{
    internal const string BaselinePath = "scripts/checks/analyzer-warnings-baseline.json";
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

    internal static string DefaultDirectory(string root)
    {
        var pointer = Path.Combine(root, "bin-build/analyzer-warnings/last-directory.txt");
        return File.Exists(pointer) ? Path.Combine(root, File.ReadAllText(pointer).Trim()) : Path.Combine(root, "bin-build/analyzer-warnings/observed");
    }

    internal static void RememberDirectory(string root, string directory)
    {
        var relative = RepositoryPath(root, directory);
        Directory.CreateDirectory(Path.Combine(root, "bin-build/analyzer-warnings"));
        File.WriteAllText(Path.Combine(root, "bin-build/analyzer-warnings/last-directory.txt"), relative + "\n");
    }

    internal static string RepositoryPath(string root, string value)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            if (!uri.IsFile) throw new ReleaseException("Analyzer path is not a local file: " + value);
            value = uri.LocalPath;
        }
        var full = Path.GetFullPath(value.Replace('\\', '/'), root);
        var prefix = Path.GetFullPath(root).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new ReleaseException("Analyzer path leaves repository: " + value);
        // Do not accept a lexically internal path that resolves through an external link.
        for (var part = new FileInfo(full) as FileSystemInfo; part != null && part.FullName.Length >= prefix.Length;
            part = part is FileInfo file ? file.Directory : ((DirectoryInfo)part).Parent)
            if (part.LinkTarget != null) throw new ReleaseException("Analyzer path contains a symbolic link: " + value);
        return Path.GetRelativePath(root, full).Replace('\\', '/');
    }

    private static string Tokens(SyntaxNode node) => string.Join(" ", node.DescendantTokens().Select(token => token.Text));

    private static SyntaxNode Parse(string source)
    {
        var allBranches = Regex.Replace(source, @"(?m)^[ \t]*#(?:if|elif|else|endif|define|undef)\b[^\r\n]*",
            match => new string(' ', match.Length));
        return CSharpSyntaxTree.ParseText(allBranches).GetRoot();
    }

    internal static AnalyzerWarning Describe(string root, string path, int line, int column, int endLine, int endColumn,
        string rule, string message, string source) => DescribeCore(root, path, line, column, endLine, endColumn, rule, message, source, Parse(source));

    private static AnalyzerWarning DescribeCore(string root, string path, int line, int column, int endLine, int endColumn,
        string rule, string message, string source, SyntaxNode syntax)
    {
        path = RepositoryPath(root, path);
        var text = SourceText.From(source);
        if (line < 1 || endLine < line || endLine > text.Lines.Count || column < 1 || endColumn < 1)
            throw new ReleaseException("Invalid analyzer line span: " + path);
        var start = text.Lines[line - 1].Start + column - 1;
        var end = text.Lines[endLine - 1].Start + endColumn - 1;
        if (start > text.Lines[line - 1].End || end > text.Lines[endLine - 1].End || end <= start)
            throw new ReleaseException("Invalid analyzer column span: " + path);
        // Parse all conditional declarations so the same shared file has stable
        // identities for warnings emitted by different release variants.
        var node = syntax.FindNode(TextSpan.FromBounds(start, end), getInnermostNodeForTie: true);
        string? Name(SyntaxNode declaration) => declaration switch
        {
            BaseNamespaceDeclarationSyntax ns => Tokens(ns.Name),
            BaseTypeDeclarationSyntax type => type.Identifier.Text,
            MethodDeclarationSyntax method => method.Identifier.Text + Tokens(method.ParameterList),
            ConstructorDeclarationSyntax constructor => constructor.Identifier.Text + Tokens(constructor.ParameterList),
            DestructorDeclarationSyntax destructor => "~" + destructor.Identifier.Text,
            PropertyDeclarationSyntax property => property.Identifier.Text,
            IndexerDeclarationSyntax indexer => "this" + Tokens(indexer.ParameterList),
            BaseFieldDeclarationSyntax field => string.Join(",", field.Declaration.Variables.Select(v => v.Identifier.Text)),
            EventDeclarationSyntax ev => ev.Identifier.Text,
            EnumMemberDeclarationSyntax member => member.Identifier.Text,
            DelegateDeclarationSyntax del => del.Identifier.Text + Tokens(del.ParameterList),
            OperatorDeclarationSyntax op => "operator" + op.OperatorToken.Text + Tokens(op.ParameterList),
            ConversionOperatorDeclarationSyntax conversion => "operator" + Tokens(conversion.Type) + Tokens(conversion.ParameterList),
            AccessorDeclarationSyntax accessor => accessor.Keyword.Text,
            LocalFunctionStatementSyntax local => local.Identifier.Text + Tokens(local.ParameterList),
            _ => null
        };
        var member = string.Join("/", node.AncestorsAndSelf().Reverse().Select(Name).Where(name => name != null));
        var anchor = string.Join(" ", SyntaxFactory.ParseTokens(source[start..end]).Select(token => token.Text));
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(new[] { rule, path, member, anchor, message })))).ToLowerInvariant();
        return new(rule, path, line, column, endLine, endColumn, message, member, anchor, fingerprint);
    }

    private static bool Generated(string path) => path.Split('/').Any(part => part is "obj" or "obj-v20" or "bin" or "bin-v20");

    private static bool TestSdkTemplate(string path)
    {
        var local = path.Replace('\\', '/');
        return (OperatingSystem.IsWindows() ? Regex.IsMatch(local, @"\A[A-Za-z]:/") : local.StartsWith('/') && !local.StartsWith("//", StringComparison.Ordinal)) &&
            Regex.IsMatch(local, @"/microsoft\.net\.test\.sdk/[^/]+/build/[^/]+/Microsoft\.NET\.Test\.Sdk\.Program\.cs$", RegexOptions.IgnoreCase);
    }

    internal static AnalyzerWarningCollection Collect(string root, string directory)
    {
        RepositoryPath(root, directory);
        if (!Directory.Exists(directory)) throw new ReleaseException("Analyzer reports missing; run the existing build first: " + directory);
        var reports = Directory.GetFiles(directory, "*.sarif").Order(StringComparer.Ordinal).ToArray();
        if (reports.Length == 0) throw new ReleaseException("No compiler analyzer reports: " + directory);
        var sources = new HashSet<string>(StringComparer.Ordinal);
        var coverage = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var entries = new List<AnalyzerWarning>();
        var parsed = new Dictionary<string, (string Source, SyntaxNode Syntax)>(StringComparer.Ordinal);
        var paths = new Dictionary<string, string>(StringComparer.Ordinal);
        var hashes = new Dictionary<string, string?>(StringComparer.Ordinal);
        string Relative(string value)
        {
            if (!paths.TryGetValue(value, out var relative)) paths[value] = relative = RepositoryPath(root, value);
            return relative;
        }
        foreach (var report in reports)
        {
            Relative(report);
            var inputs = Path.ChangeExtension(report, ".inputs");
            Relative(inputs);
            if (!File.Exists(inputs)) throw new ReleaseException("Analyzer input receipt missing: " + inputs);
            var rows = File.ReadAllLines(inputs);
            var contextParts = rows.FirstOrDefault()?.Split('|');
            if (contextParts is not { Length: 4 } || contextParts[0] != "context")
                throw new ReleaseException("Analyzer compilation context missing: " + inputs);
            var project = Relative(contextParts[1]);
            project = Regex.Replace(project, @"_[^/]+_wpftmp\.csproj$", ".csproj");
            var buildKind = rows.Skip(1).Any(row => row.Replace('\\', '/').Contains("/obj/check-all/", StringComparison.Ordinal))
                ? "variants" : "ordinary";
            var context = buildKind + "|" + project + "|" + contextParts[2] + "|" + string.Join(",", contextParts[3].Split(',').Order(StringComparer.Ordinal));
            foreach (var row in rows.Skip(1))
            {
                var parts = row.Split('|');
                if (parts.Length != 2) throw new ReleaseException("Invalid analyzer input receipt: " + inputs);
                // The test SDK supplies its generated entry point from the local
                // package cache. Verify its bytes as an auxiliary input; it never
                // becomes a repository source or an allowed diagnostic location.
                if (TestSdkTemplate(parts[0]))
                {
                    if (!File.ReadAllText(parts[0]).StartsWith("// <auto-generated>", StringComparison.Ordinal) ||
                        !ReleaseRecords.HashFile(parts[0]).Equals(parts[1], StringComparison.OrdinalIgnoreCase))
                        throw new ReleaseException("Stale or invalid test SDK generated entry point");
                    continue;
                }
                var relative = Relative(parts[0]);
                if (Generated(relative)) continue;
                var path = Path.Combine(root, relative);
                if (!hashes.TryGetValue(relative, out var hash))
                    hashes[relative] = hash = File.Exists(path) ? ReleaseRecords.HashFile(path) : null;
                if (hash == null || !hash.Equals(parts[1], StringComparison.OrdinalIgnoreCase))
                    throw new ReleaseException("Stale analyzer report; rebuild changed input: " + relative);
                if (relative.EndsWith(".cs", StringComparison.Ordinal))
                {
                    sources.Add(relative);
                    if (!coverage.TryGetValue(relative, out var contexts)) coverage[relative] = contexts = [];
                    contexts.Add(context);
                }
            }
            using var document = JsonDocument.Parse(File.ReadAllText(report));
            if (document.RootElement.GetProperty("version").GetString() != "2.1.0") throw new ReleaseException("Expected SARIF 2.1: " + report);
            foreach (var run in document.RootElement.GetProperty("runs").EnumerateArray())
                foreach (var result in run.GetProperty("results").EnumerateArray())
                {
                    var rule = result.GetProperty("ruleId").GetString()!;
                    if (!Regex.IsMatch(rule, @"\A(?:CA|IDE)\d+\z") || result.GetProperty("level").GetString() != "warning") continue;
                    if (result.TryGetProperty("suppressions", out var suppressions) && suppressions.EnumerateArray().Any(suppression =>
                        suppression.TryGetProperty("status", out var status) && status.GetString() == "accepted")) continue;
                    var location = result.GetProperty("locations")[0].GetProperty("physicalLocation");
                    var artifact = location.GetProperty("artifactLocation");
                    if (artifact.TryGetProperty("uriBaseId", out _)) throw new ReleaseException("Unsupported relative SARIF URI base: " + report);
                    var relative = Relative(artifact.GetProperty("uri").GetString()!);
                    if (!sources.Contains(relative)) throw new ReleaseException("Analyzer warning has no compiled source receipt: " + relative);
                    var region = location.GetProperty("region");
                    if (!parsed.TryGetValue(relative, out var file))
                    {
                        var source = File.ReadAllText(Path.Combine(root, relative));
                        parsed[relative] = file = (source, Parse(source));
                    }
                    entries.Add(DescribeCore(root, relative, region.GetProperty("startLine").GetInt32(), region.GetProperty("startColumn").GetInt32(),
                        region.GetProperty("endLine").GetInt32(), region.GetProperty("endColumn").GetInt32(), rule,
                        result.GetProperty("message").GetProperty("text").GetString()!, file.Source, file.Syntax) with { Contexts = [context] });
                }
        }
        var deduplicated = entries.GroupBy(e => (e.Rule, e.Path, e.Line, e.Column, e.EndLine, e.EndColumn, e.Message))
            .Select(group => group.First() with { Contexts = group.SelectMany(e => e.Contexts!).Distinct().Order(StringComparer.Ordinal).ToArray() });
        return new(Order(deduplicated), sources, coverage);
    }

    private static AnalyzerWarning[] Order(IEnumerable<AnalyzerWarning> entries) => entries.OrderBy(e => e.Rule, StringComparer.Ordinal)
        .ThenBy(e => e.Path, StringComparer.Ordinal).ThenBy(e => e.Member, StringComparer.Ordinal).ThenBy(e => e.Fingerprint, StringComparer.Ordinal)
        .ThenBy(e => e.Line).ThenBy(e => e.Column).ToArray();

    internal static AnalyzerWarningCollection Combine(IEnumerable<AnalyzerWarningCollection> collections)
    {
        var all = collections.ToArray();
        var entries = all.SelectMany(collection => collection.Entries)
            .GroupBy(entry => (entry.Rule, entry.Path, entry.Line, entry.Column, entry.EndLine, entry.EndColumn, entry.Message))
            .Select(group => group.First() with { Contexts = group.SelectMany(entry => entry.Contexts ?? []).Distinct().Order(StringComparer.Ordinal).ToArray() });
        var coverage = all.SelectMany(collection => collection.Coverage ?? [])
            .GroupBy(pair => pair.Key).ToDictionary(group => group.Key, group => group.SelectMany(pair => pair.Value).ToHashSet(StringComparer.Ordinal));
        return new(Order(entries), all.SelectMany(collection => collection.Sources).ToHashSet(StringComparer.Ordinal), coverage);
    }

    internal static List<string> Compare(string root, AnalyzerWarningCollection current, AnalyzerWarning[] baseline, bool complete)
    {
        var errors = new List<string>();
        var kinds = BuildKinds(current);
        foreach (var entry in baseline)
        {
            if (RepositoryPath(root, entry.Path) != entry.Path || string.IsNullOrWhiteSpace(entry.Reason))
                throw new ReleaseException("Unreviewed or non-relative analyzer baseline entry: " + entry.Path);
            var expected = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(
                new[] { entry.Rule, entry.Path, entry.Member, entry.Anchor, entry.Message })))).ToLowerInvariant();
            if (expected != entry.Fingerprint) throw new ReleaseException("Invalid analyzer baseline fingerprint: " + entry.Path);
            if (complete && File.Exists(Path.Combine(root, entry.Path)) && !current.Sources.Contains(entry.Path))
                errors.Add("Analyzer baseline source was not built: " + entry.Path);
        }
        var reviewed = baseline.GroupBy(e => e.Fingerprint).ToDictionary(group => group.Key, group => group.ToArray());
        var observed = current.Entries.GroupBy(e => e.Fingerprint).ToDictionary(group => group.Key, group => group.ToArray());
        foreach (var group in observed)
        {
            var count = reviewed.GetValueOrDefault(group.Key)?.Length ?? 0;
            foreach (var entry in group.Value.Skip(count)) errors.Add($"NEW {entry.Rule} {entry.Path}:{entry.Line}: {entry.Message}");
        }
        foreach (var group in reviewed)
        {
            var count = observed.GetValueOrDefault(group.Key)?.Length ?? 0;
            foreach (var entry in group.Value.Skip(count))
            {
                if (!File.Exists(Path.Combine(root, entry.Path)) || current.Sources.Contains(entry.Path) &&
                    (current.Coverage == null || entry.Contexts?.Any(context => current.Coverage[entry.Path].Contains(context)) == true))
                    errors.Add($"STALE {entry.Rule} {entry.Path}:{entry.Line}: remove the reviewed entry with -UpdateBaseline");
                else if (complete && current.Sources.Contains(entry.Path) && entry.Contexts?.Any(context => kinds.Contains(context.Split('|')[0])) == true)
                    errors.Add("Analyzer baseline warning context was not built: " + entry.Path);
            }
        }
        return errors.Distinct(StringComparer.Ordinal).ToList();
    }

    private static HashSet<string> BuildKinds(AnalyzerWarningCollection collection) => collection.Coverage?.Values.SelectMany(contexts => contexts)
        .Select(context => context.Split('|')[0]).ToHashSet(StringComparer.Ordinal) ?? [];

    internal static int Run(string root, Options options)
    {
        try
        {
            var modes = new[] { "Check", "UpdateBaseline", "Initialize" }.Count(options.Has);
            if (modes > 1 || options.Get("Capture") != null && modes != 0)
                throw new ReleaseException("Analyzer check, capture, initialization and update modes are mutually exclusive", 64);
            if (options.Has("AllowGrowth")) throw new ReleaseException("Analyzer baselines can only shrink; -AllowGrowth is not supported", 64);
            if (options.Get("ReviewData") != null && !options.Has("Initialize"))
                throw new ReleaseException("-ReviewData requires -Initialize", 64);
            var directories = options.All("DiagnosticsDirectory");
            if (directories.Count == 0) directories = [Environment.GetEnvironmentVariable("TIA_MCP_ANALYZER_REPORT_DIRECTORY") ?? DefaultDirectory(root)];
            var current = Combine(directories.Select(directory => Collect(root, Path.GetFullPath(directory))));
            var path = Path.GetFullPath(options.Get("Baseline", Path.Combine(root, BaselinePath)));
            RepositoryPath(root, path);
            if (options.Get("Capture") is { } capture)
            {
                RepositoryPath(root, capture);
                if (File.Exists(capture) || Directory.Exists(capture)) throw new ReleaseException("Analyzer capture path must be fresh: " + capture, 64);
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(capture))!);
                File.WriteAllText(capture, JsonSerializer.Serialize(new AnalyzerWarningBaseline(1, current.Entries), Json) + "\n");
                Console.WriteLine($"Captured {current.Entries.Length} analyzer warnings for review.");
                return 0;
            }
            if (options.Has("Initialize"))
            {
                if (File.Exists(path)) throw new ReleaseException("Analyzer baseline already exists; it can only shrink", 64);
                var review = options.Get("ReviewData") ?? throw new ReleaseException("-Initialize requires -ReviewData", 64);
                RepositoryPath(root, review);
                var reviewed = Read(review);
                var errors = Compare(root, current, reviewed, complete: true);
                if (errors.Count != 0) return SourceCheck.Report("Analyzer warnings", errors);
                File.WriteAllText(path, JsonSerializer.Serialize(new AnalyzerWarningBaseline(1, Order(reviewed)), Json) + "\n");
                return SourceCheck.Report("Analyzer warnings", [], $"Initialized {reviewed.Length} reviewed warnings.");
            }
            var baseline = Read(path);
            var issues = Compare(root, current, baseline, options.Has("Complete"));
            if (options.Has("UpdateBaseline"))
            {
                // Growth and incomplete builds never write the baseline. Only
                // disappearance is accepted, preserving all review annotations.
                if (!options.Has("Complete")) throw new ReleaseException("-UpdateBaseline requires -Complete", 64);
                var missingKinds = baseline.SelectMany(entry => entry.Contexts ?? []).Select(context => context.Split('|')[0])
                    .Distinct().Except(BuildKinds(current)).ToArray();
                if (missingKinds.Length != 0) throw new ReleaseException("-UpdateBaseline requires reports for every reviewed build kind: " + string.Join(", ", missingKinds), 64);
                var blockers = issues.Where(issue => !issue.StartsWith("STALE ", StringComparison.Ordinal)).ToList();
                if (blockers.Count != 0) return SourceCheck.Report("Analyzer warnings", blockers);
                var counts = current.Entries.GroupBy(e => e.Fingerprint).ToDictionary(group => group.Key, group => group.Count());
                var retained = baseline.Where(entry => counts.TryGetValue(entry.Fingerprint, out var count) && count > 0 &&
                    (counts[entry.Fingerprint] = count - 1) >= 0).ToArray();
                File.WriteAllText(path, JsonSerializer.Serialize(new AnalyzerWarningBaseline(1, Order(retained)), Json) + "\n");
                Console.WriteLine($"Analyzer baseline shrank {baseline.Length} -> {retained.Length}.");
                baseline = retained;
                issues = Compare(root, current, retained, complete: true);
            }
            return SourceCheck.Report("Analyzer warnings", issues, $"{current.Entries.Length} warnings; {current.Sources.Count} compiled source files; {baseline.Length} reviewed entries.");
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException or KeyNotFoundException or ReleaseException)
        {
            if (ex is ReleaseException release && release.ExitCode == 64) throw;
            return SourceCheck.Report("Analyzer warnings", [ex.Message]);
        }
    }

    private static AnalyzerWarning[] Read(string path)
    {
        var data = JsonSerializer.Deserialize<AnalyzerWarningBaseline>(File.ReadAllText(path), Json);
        if (data is not { Format: 1, Entries: not null }) throw new ReleaseException("Invalid analyzer baseline: " + path);
        if (data.Entries.Any(entry => entry == null || entry.Rule == null || entry.Path == null || entry.Message == null || entry.Member == null || entry.Anchor == null || entry.Fingerprint == null ||
            !Regex.IsMatch(entry.Rule, @"\A(?:CA|IDE)\d+\z") || entry.Line < 1 || entry.Column < 1 || entry.EndLine < entry.Line || entry.EndColumn < 1 ||
            entry.Contexts is not { Length: > 0 } || entry.Contexts.Any(string.IsNullOrWhiteSpace)))
            throw new ReleaseException("Malformed analyzer baseline entry: " + path);
        if (data.Entries.DistinctBy(entry => (entry.Rule, entry.Path, entry.Line, entry.Column, entry.EndLine, entry.EndColumn, entry.Message)).Count() != data.Entries.Length)
            throw new ReleaseException("Duplicate analyzer baseline location: " + path);
        return data.Entries;
    }
}
