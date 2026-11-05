using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace TiaMcp.ReleaseTool;

internal sealed record ComplexityRow(string Path, string Kind, string Member, int Line, int Lines, int Complexity, bool Changed);

internal static class ComplexityReport
{
    internal static IEnumerable<ComplexityRow> Measure(string path, string source, bool changed)
    {
        var syntax = CheckLoosening.Parse(source);
        int lines = source.Split('\n').Length - (source.EndsWith('\n') ? 1 : 0);
        if (lines > 600) yield return new(path, "file", "", 1, lines, 0, changed);
        foreach (var node in syntax.DescendantNodes()
                .Where(node => node is BaseMethodDeclarationSyntax or LocalFunctionStatementSyntax or AccessorDeclarationSyntax))
        {
            var span = node.GetLocation().GetLineSpan();
            int length = span.EndLinePosition.Line - span.StartLinePosition.Line + 1;
            // Syntax estimate (not a semantic metric): each decision/short-circuit adds one path.
            var descendants = node
                .DescendantNodes(child => ReferenceEquals(child, node) || child is not (LocalFunctionStatementSyntax or AnonymousFunctionExpressionSyntax));
            int complexity = 1 + descendants.Count(child => child is IfStatementSyntax or ForStatementSyntax or ForEachStatementSyntax or
                ForEachVariableStatementSyntax or WhileStatementSyntax or DoStatementSyntax or CatchClauseSyntax or ConditionalExpressionSyntax or
                CaseSwitchLabelSyntax or CasePatternSwitchLabelSyntax || child is SwitchExpressionArmSyntax arm && arm.Pattern is not DiscardPatternSyntax ||
                child.IsKind(SyntaxKind.LogicalAndExpression) || child.IsKind(SyntaxKind.LogicalOrExpression) || child.IsKind(SyntaxKind.CoalesceExpression));
            if (length <= 60 && complexity <= 15) continue;
            string name = node switch
            {
                MethodDeclarationSyntax method => method.Identifier.Text + CheckLoosening.Tokens(method.ParameterList),
                ConstructorDeclarationSyntax constructor => constructor.Identifier.Text + CheckLoosening.Tokens(constructor.ParameterList),
                LocalFunctionStatementSyntax local => local.Identifier.Text + CheckLoosening.Tokens(local.ParameterList),
                AccessorDeclarationSyntax accessor => accessor.Keyword.Text,
                _ => node.Kind().ToString()
            };
            yield return new(path, "method", name, span.StartLinePosition.Line + 1, length, complexity, changed);
        }
    }

    internal static string Render(IEnumerable<ComplexityRow> rows)
    {
        var report =
            new StringBuilder("# Complexity report\n\nInformational only. Methods over 60 lines or estimated cyclomatic complexity over 15; files over 600 " +
            "lines. "
                +
            "Findings newly over a limit in this change appear first. C# syntax estimate, including all conditional-compilation branches; comments count " +
                "toward length.\n\n"
                +
            "| New in change | File | Member | Lines | Complexity |\n|---|---|---|---:|---:|\n");
        foreach (var row in rows.OrderByDescending(row => row.Changed).ThenBy(row => row.Path, StringComparer.Ordinal).ThenBy(row => row.Line))
        {
            string member = row.Member.Replace("|", "\\|", StringComparison.Ordinal);
            string complexity = row.Kind == "file" ? "—" : row.Complexity.ToString(System.Globalization.CultureInfo.InvariantCulture);
            report.AppendLine($"| {(row.Changed ? "yes" : "")} | {row.Path}:{row.Line} | {member} | {row.Lines} | {complexity} |");
        }
        return report.ToString();
    }

    internal static int Run(string root, Options options)
    {
        string output = options.Get("Output", Path.Combine(root, "bin-build/complexity-report.md"));
        string report;
        try
        {
            var changes = options.Get("Base") is { } basis
                ? ReviewChangeSet.Read(root, basis, options.Get("Git", "git")).ToDictionary(change => change.Path, StringComparer.Ordinal)
                : new Dictionary<string, CheckChange>(StringComparer.Ordinal);
            IEnumerable<string> paths;
            if (options.Get("Base") != null) paths = changes.Keys;
            else
            {
                var files = ReviewChangeSet.Git(options.Get("Git", "git"), ["ls-files", "-z", "--", "*.cs"], root);
                ProcessRunner.RequireSuccess(files, "Complexity source inventory");
                paths = files.StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries);
            }
            paths = paths.Where(path => path.EndsWith(".cs", StringComparison.Ordinal) &&
                !path.StartsWith("third_party/", StringComparison.Ordinal) && !path.StartsWith("reference/", StringComparison.Ordinal) &&
                !path.StartsWith("archive/", StringComparison.Ordinal)).Distinct();
            var rows = paths.Where(path => File.Exists(Path.Combine(root, path))).SelectMany(path =>
            {
                var previous = changes.TryGetValue(path, out var change)
                    ? Measure(path, change.Before ?? "", false).Select(row => (row.Kind, row.Member)).ToHashSet() : null;
                return Measure(path, File.ReadAllText(Path.Combine(root, path)), false)
                    .Select(row => row with { Changed = previous != null && !previous.Contains((row.Kind, row.Member)) });
            }).ToArray();
            report = Render(rows);
            Console.WriteLine($"complexity-report: {rows.Length} informational entries; {output}");
        }
        catch (Exception ex)
        {
            report = "# Complexity report\n\nReport unavailable: " + ex.Message + "\n";
            Console.WriteLine("complexity-report: report unavailable: " + ex.Message);
        }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
            File.WriteAllText(output, report);
        }
        catch (Exception ex)
        {
            // A report destination failure cannot become a merge gate.
            Console.WriteLine("complexity-report: could not write report: " + ex.Message);
        }
        return 0;
    }
}
