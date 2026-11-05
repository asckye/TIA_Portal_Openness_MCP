using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace TiaMcp.ReleaseTool;

internal sealed record ScorecardSourceCounts(int Methods, int Long, int Complex, int Deep, int Parameters,
    int Public, int Undocumented, ScorecardTypeIndex[] Maintainability, string[] Lines);

internal static class ScorecardSource
{
    internal static ScorecardSourceCounts Measure(string path, string source)
    {
        var tree = CheckLoosening.Parse(source);
        var methods = tree.DescendantNodes().Where(node =>
            node is BaseMethodDeclarationSyntax or LocalFunctionStatementSyntax or AccessorDeclarationSyntax).ToArray();
        var complexity = ComplexityReport.Measure(path, source, false).Where(row => row.Kind == "method").ToArray();
        int deep = methods.Count(method => method.DescendantNodes(child => ReferenceEquals(child, method) ||
                child is not (LocalFunctionStatementSyntax or AnonymousFunctionExpressionSyntax))
            .Any(node => NestingControl(node) && node.AncestorsAndSelf().TakeWhile(parent => parent != method).Count(NestingControl) > 3));
        int parameters = methods.Count(node => (node switch
        {
            BaseMethodDeclarationSyntax method => method.ParameterList.Parameters.Count,
            LocalFunctionStatementSyntax local => local.ParameterList.Parameters.Count,
            _ => 0
        }) > 5);
        var declarations = tree.DescendantNodes().OfType<MemberDeclarationSyntax>().Where(ExternallyVisible).ToArray();
        int undocumented = declarations.Count(node => !node.GetLeadingTrivia().Any(trivia =>
            trivia.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia) || trivia.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia)));
        var indices = tree.DescendantNodes().OfType<TypeDeclarationSyntax>().Select(type =>
        {
            var owners = type.Ancestors().Reverse().Select(node => node switch
            {
                BaseNamespaceDeclarationSyntax space => space.Name.ToString(),
                TypeDeclarationSyntax owner => owner.Identifier.ValueText,
                _ => ""
            }).Where(name => name.Length > 0);
            string name = string.Join('.', owners.Append(type.Identifier.ValueText + type.TypeParameterList));
            return new ScorecardTypeIndex(path, name, Index(type));
        }).ToArray();
        var lines = tree.DescendantTokens().Where(token => !token.IsKind(SyntaxKind.EndOfFileToken))
            .GroupBy(token => token.GetLocation().GetLineSpan().StartLinePosition.Line)
            .Select(group => string.Join(' ', group.Select(token => token.Text))).ToArray();
        return new(methods.Length, complexity.Count(row => row.Lines > 60), complexity.Count(row => row.Complexity > 15),
            deep, parameters, declarations.Length, undocumented, indices, lines);
    }

    private static bool Control(SyntaxNode node) => node is IfStatementSyntax or ForStatementSyntax or ForEachStatementSyntax or
        ForEachVariableStatementSyntax or WhileStatementSyntax or DoStatementSyntax or SwitchStatementSyntax or
        SwitchExpressionSyntax or TryStatementSyntax or ConditionalExpressionSyntax;

    private static bool NestingControl(SyntaxNode node) => Control(node) && node is not IfStatementSyntax { Parent: ElseClauseSyntax };

    private static bool ExternallyVisible(MemberDeclarationSyntax node)
    {
        if (node is NamespaceDeclarationSyntax or FileScopedNamespaceDeclarationSyntax) return false;
        bool Visible(MemberDeclarationSyntax member) => member.Modifiers.Any(SyntaxKind.PublicKeyword) ||
            member.Modifiers.Any(SyntaxKind.ProtectedKeyword) || member.Parent is InterfaceDeclarationSyntax;
        if (!Visible(node)) return false;
        return node.Ancestors().OfType<TypeDeclarationSyntax>().All(Visible);
    }

    internal static double Index(TypeDeclarationSyntax type)
    {
        var tokens = type.DescendantTokens(descendIntoTrivia: false).ToArray();
        int vocabulary = tokens.Select(token => token.Text).Distinct().Count();
        double volume = tokens.Length * Math.Log2(Math.Max(1, vocabulary));
        int complexity = 1 + type.DescendantNodes().Count(node => Control(node) || node is CatchClauseSyntax or
            CaseSwitchLabelSyntax or CasePatternSwitchLabelSyntax || node.IsKind(SyntaxKind.LogicalAndExpression) ||
            node.IsKind(SyntaxKind.LogicalOrExpression) || node.IsKind(SyntaxKind.CoalesceExpression));
        int lines = tokens.Select(token => token.GetLocation().GetLineSpan().StartLinePosition.Line).Distinct().Count();
        return Math.Max(0, (171 - 5.2 * Math.Log(Math.Max(1, volume)) - .23 * complexity -
            16.2 * Math.Log(Math.Max(1, lines))) * 100 / 171);
    }

    internal static int DuplicateBlocks(IReadOnlyDictionary<string, string[]> files)
    {
        var windows = new Dictionary<string, List<(string Path, int Line)>>(StringComparer.Ordinal);
        foreach (var (path, lines) in files)
        for (int line = 0; line + 10 <= lines.Length; line++)
        {
            string block = string.Join('\n', lines.Skip(line).Take(10));
            if (!windows.TryGetValue(block, out var positions)) windows[block] = positions = [];
            positions.Add((path, line));
        }
        int count = 0;
        foreach (var positions in windows.Values.Where(positions => positions.Select(position => position.Path).Distinct().Skip(1).Any()))
        {
            for (int left = 0; left < positions.Count; left++)
            for (int right = left + 1; right < positions.Count; right++)
            {
                var first = positions[left]; var second = positions[right];
                if (first.Path == second.Path) continue;
                if (first.Line > 0 && second.Line > 0 && files[first.Path][first.Line - 1] == files[second.Path][second.Line - 1]) continue;
                count++;
            }
        }
        return count;
    }

    internal static int ProjectCycles(IReadOnlyDictionary<string, string[]> references)
    {
        var completed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var active = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int cycles = 0;
        void Visit(string path)
        {
            if (active.Contains(path)) { cycles++; return; }
            if (!completed.Add(path)) return;
            active.Add(path);
            foreach (string reference in references.GetValueOrDefault(path) ?? []) Visit(reference);
            active.Remove(path);
        }
        foreach (string path in references.Keys) Visit(path);
        return cycles;
    }

    internal static void Collect(Scorecard context, double? reviewed)
    {
        string source = "git ls-files src/**/*.cs";
        var files = new Dictionary<string, string[]>();
        var indices = new List<double>();
        int count = 0, methods = 0, large = 0, longMethods = 0, complex = 0, deep = 0, parameters = 0;
        int publicCount = 0, undocumented = 0, todo = 0, commented = 0;
        try
        {
            string[] paths = context.ReadGit(["ls-files", "-z", "--", "src/**/*.cs"]).Split('\0', StringSplitOptions.RemoveEmptyEntries);
            if (paths.Length == 0) throw new IOException("No tracked src/**/*.cs files");
            foreach (string path in paths)
            {
                string text = File.ReadAllText(Path.Combine(context.Root, path));
                var metrics = Measure(path, text);
                count++; methods += metrics.Methods; longMethods += metrics.Long; complex += metrics.Complex;
                deep += metrics.Deep; parameters += metrics.Parameters; publicCount += metrics.Public; undocumented += metrics.Undocumented;
                context.Report.TypeIndices.AddRange(metrics.Maintainability);
                indices.AddRange(metrics.Maintainability.Select(row => row.Estimate)); files[path] = metrics.Lines;
                int lines = text.Split('\n').Length - (text.EndsWith('\n') ? 1 : 0);
                if (lines > 600) large++;
                var hygiene = CommentHygiene.ScanSource(text, path, "");
                commented += hygiene.Count(row => Scorecard.Text(row["kind"]) == "commented-code");
                todo += CheckLoosening.Parse(text).DescendantTrivia().Where(trivia =>
                    trivia.IsKind(SyntaxKind.SingleLineCommentTrivia) || trivia.IsKind(SyntaxKind.MultiLineCommentTrivia) ||
                    trivia.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia))
                    .Sum(trivia => Regex.Matches(trivia.ToFullString(), @"\b(TODO|FIXME|HACK)\b", RegexOptions.IgnoreCase).Count);
            }
            void Share(string dimension, string measure, int numerator, int denominator) =>
                context.Report.Add(dimension, measure, "", (double)numerator / denominator, source, $"{numerator}/{denominator}", "share");
            if (methods == 0) throw new IOException("No methods in source inventory");
            Share("Readability", "long-methods", longMethods, methods);
            Share("Readability", "complex-methods", complex, methods);
            Share("Readability", "deep-methods", deep, methods);
            Share("Readability", "many-parameters", parameters, methods);
            Share("Readability", "large-files", large, count);
            if (publicCount > 0) Share("Comments", "missing-public-docs", undocumented, publicCount);
            else context.Report.Missing("Comments", "missing-public-docs", "", source, "No public declarations");
            context.Report.Add("Comments", "todo-comments", "", todo, source);
            context.Report.Add("Comments", "commented-code", "", commented, source);
            context.Report.Add("Maintainability", "duplicate-blocks", "", DuplicateBlocks(files), source,
                "Maximal identical normalized blocks of at least 10 nonempty code lines, counted per file pair");
            if (indices.Count > 0) context.Report.Add("Maintainability", "maintainability-index", "", indices.Min(), source,
                $"Estimate, minimum of {indices.Count} types; Microsoft normalized MI formula, syntax token volume and code LOC");
            else context.Report.Missing("Maintainability", "maintainability-index", "", source, "No types");
        }
        catch (Exception ex)
        {
            foreach (string dimension in new[] { "Readability", "Comments", "Maintainability" })
                context.Report.Missing(dimension, "source", "", source, ex.Message);
        }
        if (reviewed is { } value) context.Report.Add("Readability", "reviewed-sample", "", value, "-ReviewedSample",
            "Readability (reviewed sample)", "0–5");
        else context.Report.Missing("Readability", "reviewed-sample", "", "sample.txt", "Independent review not supplied");
        Naming(context);
        Projects(context);
        Churn(context);
        context.Report.Missing("Maintainability", "class-coupling", "", ".editorconfig", "CA1506 disabled (D3)");
    }

    internal static void Naming(Scorecard context)
    {
        string source = Path.Combine(context.RunDirectory, "analyzers");
        context.Report.Capture("Readability", "", source, () =>
        {
            string[] paths = Directory.GetFiles(source, "*.sarif");
            if (paths.Length == 0) throw new IOException("No analyzer SARIF reports");
            var results = new HashSet<string>(StringComparer.Ordinal);
            foreach (string path in paths)
            foreach (var run in Scorecard.Read(path)["runs"]!.AsArray())
            foreach (var row in run!["results"]?.AsArray() ?? [])
            {
                string rule = Scorecard.Text(row!["ruleId"]);
                if (rule.StartsWith("CA17", StringComparison.Ordinal) || rule == "IDE1006") results.Add(row.ToJsonString());
            }
            context.Report.Add("Readability", "naming-diagnostics", "", results.Count, source, "Including SARIF note-level results");
            var scope = context.Timings.Where(row => row.Output == context.RunDirectory).MaxBy(row => row.Utc);
            if (scope is not { Wide: true, DocsOnly: false }) context.Report.Rows[^1] = context.Report.Rows[^1] with
            {
                State = "partial", Detail = "Naming diagnostics from rebuilt projects only; " +
                    (scope == null ? "run scope unavailable" : "narrow or documentation-only run")
            };
        }, "naming-diagnostics");
    }

    private static void Projects(Scorecard context)
    {
        const string source = "git ls-files *.csproj";
        context.Report.Capture("Maintainability", "", source, () =>
        {
            string[] paths = context.ReadGit(["ls-files", "-z", "--", "*.csproj"]).Split('\0', StringSplitOptions.RemoveEmptyEntries);
            if (paths.Length == 0) throw new IOException("No tracked C# projects");
            var references = paths.ToDictionary(path => Path.GetFullPath(path, context.Root),
                path => ProjectReferences(Path.Combine(context.Root, path)), StringComparer.OrdinalIgnoreCase);
            context.Report.Add("Maintainability", "project-cycles", "", ProjectCycles(references), source,
                "Source reference graph, union of existing local property/condition variants; no MSBuild execution");
        }, "project-cycles");
    }

    internal static string[] ProjectReferences(string path)
    {
        var xml = XDocument.Load(path);
        var values = xml.Descendants("PropertyGroup").Elements().GroupBy(element => element.Name.LocalName)
            .ToDictionary(group => group.Key, group => group.Select(element => element.Value).Where(value => value.Length > 0).ToHashSet());
        foreach (var condition in xml.Descendants().Attributes("Condition"))
        foreach (Match match in Regex.Matches(condition.Value, @"'\$\(([^)]+)\)'\s*==\s*'([^']+)'"))
        {
            string name = match.Groups[1].Value, value = match.Groups[2].Value;
            if (!values.TryGetValue(name, out var choices)) values[name] = choices = [];
            choices.Add(value);
        }
        string directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        values["MSBuildProjectDirectory"] = [directory];
        values["MSBuildThisFileDirectory"] = [directory + Path.DirectorySeparatorChar];
        IEnumerable<string> Expand(string expression, HashSet<string> active)
        {
            var match = Regex.Match(expression, @"\$\(([^)]+)\)");
            if (!match.Success) return [expression];
            string name = match.Groups[1].Value;
            if (!active.Add(name) || !values.TryGetValue(name, out var choices))
                throw new IOException("Unresolved project reference property " + name + " in " + path);
            return choices.SelectMany(value => Expand(value, new HashSet<string>(active)))
                .SelectMany(value => Expand(expression[..match.Index] + value + expression[(match.Index + match.Length)..], []));
        }
        var references = new List<string>();
        foreach (var reference in xml.Descendants("ProjectReference").Attributes("Include"))
        {
            string[] expanded = Expand(reference.Value, []).Select(value => Path.GetFullPath(value.Replace('\\', '/'), directory))
                .Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (expanded.Length == 0) throw new IOException("No existing project for reference " + reference.Value + " in " + path);
            references.AddRange(expanded);
        }
        return references.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static void Churn(Scorecard context)
    {
        context.Report.Capture("Maintainability", "", "git log --since=30.days --name-only", () =>
        {
            var paths = context.ReadGit(["log", "--since=30.days", "--format=", "--name-only", "--", "src"])
                .Split('\n', StringSplitOptions.RemoveEmptyEntries);
            foreach (var group in paths.GroupBy(path => path.Trim()).OrderByDescending(group => group.Count())
                .ThenBy(group => group.Key, StringComparer.Ordinal).Take(10))
                context.Report.Add("Maintainability", "churn:" + group.Key, "", group.Count(), "git log --since=30.days", "Unscored");
        }, "churn");
    }

    internal static void Reviews(Scorecard context)
    {
        string directory = Path.Combine(context.Primary, "bin-build/refactor");
        context.Report.Capture("Review", "", directory, () =>
        {
            string log = context.ReadGit(["log", "master", "--since=30.days", "--format=%s"]);
            var tasks = log.Split('\n').Select(subject => Regex.Match(subject, @"\(([A-Z][A-Za-z0-9-]*?)(?: round \d+)?\)$"))
                .Where(match => match.Success).Select(match => Regex.Replace(match.Groups[1].Value, "-PORT$", "")).Distinct().ToArray();
            var counts = new List<double>();
            string[] files = Directory.GetFiles(directory, "*.result.md");
            foreach (string task in tasks)
            {
                int count = files.Count(path => Regex.IsMatch(Path.GetFileName(path), "^" + Regex.Escape(task) + @"(?:-PORT)?\..+\.result\.md$"));
                if (count == 0) context.Report.Missing("Review", "handovers:" + task, "", directory, "no record", "no record");
                else { counts.Add(count); context.Report.Add("Review", "handovers:" + task, "", count, directory, "Merged task"); }
            }
            if (counts.Count == 0) throw new IOException("No handover files for merged tasks in the last 30 days; no record");
            context.Report.Add("Review", "median-handovers", "", ScorecardEvidence.Median(counts), directory);
            context.Report.Add("Review", "maximum-handovers", "", counts.Max(), directory);
            context.Report.Missing("Review", "findings-per-round", "", directory, "No structured findings per round");
        });
    }
}
