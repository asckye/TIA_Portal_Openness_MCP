using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace TiaMcp.ReleaseTool;

internal static class CheckLoosening
{
    internal static void AddJobs(MergeCheck.Plan plan, string baseRef, string dotnet, string output)
    {
        if (plan.DocsOnly) return;
        plan.Checks.Add(new("check-loosening", dotnet,
            [typeof(Program).Assembly.Location, "check-loosening", "-Base", baseRef]));
        plan.Checks.Add(new("complexity-report", dotnet,
            [typeof(Program).Assembly.Location, "complexity-report", "-Base", baseRef, "-Output", Path.Combine(output, "complexity-report.md")]));
    }

    internal static string Tokens(SyntaxNode node) => string.Join(" ", node.DescendantTokens().Select(token => token.Text));
    internal static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    internal static SyntaxNode Parse(string source) => CSharpSyntaxTree.ParseText(
        System.Text.RegularExpressions.Regex.Replace(source, @"(?m)^[ \t]*#(?:if|elif|else|endif|define|undef)\b[^\r\n]*",
            match => new string(' ', match.Length))).GetRoot();

    internal static List<ReviewFinding> Scan(IEnumerable<CheckChange> changes, Func<string, bool>? testSource = null)
    {
        var findings = new List<ReviewFinding>();
        testSource ??= path => path.StartsWith("tests/", StringComparison.Ordinal) &&
            Path.GetExtension(path) is ".cs" or ".py" || path.StartsWith("scripts/checks/", StringComparison.Ordinal) &&
            Path.GetExtension(path) is ".cs" or ".py";
        bool TestFile(CheckChange change) => change.OldPath.StartsWith("tests/", StringComparison.Ordinal) ||
            change.OldPath.StartsWith("scripts/checks/", StringComparison.Ordinal) ||
            change.OldPath.EndsWith(".cs", StringComparison.Ordinal) && Parse(change.Before ?? "").DescendantNodes()
                .OfType<MethodDeclarationSyntax>().Any(IsTest);
        var rows = changes.Where(change => !(ReviewChangeSet.Regenerated(change.Path) && ReviewChangeSet.Regenerated(change.OldPath)))
            .Select(change => change.Before != null && change.After != null &&
                change.Path != change.OldPath && Path.GetExtension(change.OldPath) is ".cs" or ".py" && TestFile(change) &&
                (Path.GetExtension(change.Path) != Path.GetExtension(change.OldPath) || !testSource(change.Path))
            ? new CheckChange(change.OldPath, change.OldPath, change.Before, null) : change).ToArray();
        var sourceRows = rows.Where(change => change.Path.EndsWith(".cs", StringComparison.Ordinal)).ToArray();
        CSharp(sourceRows, findings);
        TopLevel(sourceRows, findings);
        TheoryData(sourceRows, findings);
        PythonCheckChanges.Scan(rows.Where(change => change.Path.EndsWith(".py", StringComparison.Ordinal)), findings);
        foreach (var change in rows)
        {
            if (change.After == null && Path.GetExtension(change.OldPath) is not (".csproj" or ".props") &&
                (change.Path.StartsWith("tests/", StringComparison.Ordinal) ||
                change.Path.StartsWith("scripts/checks/", StringComparison.Ordinal)))
            {
                findings.Add(new(change.OldPath, "check-file-deleted", "file:" + Hash(change.Before ?? ""), "Check/test file deleted"));
                continue;
            }
            CheckLimitChanges.Scan(change, findings);
            TestExclusions.Scan(change, findings);
        }
        return findings.OrderBy(row => row.Path, StringComparer.Ordinal).ThenBy(row => row.Kind, StringComparer.Ordinal)
            .ThenBy(row => row.Identifier, StringComparer.Ordinal).ToList();
    }

    private static string Member(MethodDeclarationSyntax method)
    {
        var namespaces = method.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().Reverse().Select(ns => ns.Name.ToString());
        var types = method.Ancestors().OfType<TypeDeclarationSyntax>().Reverse().Select(type => type.Identifier.Text);
        return string.Join(".", namespaces.Concat(types)) + "/" + method.Identifier.Text + Tokens(method.ParameterList);
    }

    private static bool IsTest(MethodDeclarationSyntax method) => method.AttributeLists.SelectMany(list => list.Attributes)
        .Any(attribute => attribute.Name.ToString().Split('.')
                .Last() is "Fact" or "Theory" or "TestMethod" or "FactAttribute" or "TheoryAttribute" or "TestMethodAttribute");

    private static IEnumerable<string> Skips(MethodDeclarationSyntax method) => method.AttributeLists.SelectMany(list => list.Attributes)
        .SelectMany(attribute => attribute.ArgumentList?.Arguments ?? default)
        .Where(argument => argument.NameEquals?.Name.Identifier.Text is "Skip" or "SkipWhen" or "SkipUnless")
        .Select(Tokens);

    private static bool ConsoleAssertions(string path) => path.StartsWith("tests/", StringComparison.Ordinal) ||
        path.StartsWith("scripts/", StringComparison.Ordinal);

    private static IEnumerable<SyntaxNode> Assertions(SyntaxNode syntax, bool console) => syntax.DescendantNodes().Where(node => node switch
    {
        InvocationExpressionSyntax invocation =>
            console && (invocation.Expression is IdentifierNameSyntax name && name.Identifier.Text is "Check" or "check" ||
                invocation.Expression is MemberAccessExpressionSyntax helper && helper.Name.Identifier.Text is "Check" or "check") ||
            invocation.Expression is MemberAccessExpressionSyntax access &&
                access.Expression.ToString().Split('.').Last() is "Assert" or "CollectionAssert" or "StringAssert" || IsTheoryDataAdd(invocation),
        AttributeSyntax attribute => attribute.Name.ToString().Split('.').Last() is
            "InlineData" or "MemberData" or "TheoryData" or "InlineDataAttribute" or "MemberDataAttribute" or "TheoryDataAttribute",
        _ => false
    });

    private static bool IsTheoryDataAdd(InvocationExpressionSyntax invocation)
    {
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "Add" } access) return false;
        string name = access.Expression.ToString();
        var owner = invocation.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault();
        return owner != null && owner.DescendantNodes().OfType<VariableDeclaratorSyntax>().Any(variable =>
            variable.Identifier.Text == name && (variable.Parent is VariableDeclarationSyntax declaration &&
            declaration.Type.ToString().Contains("TheoryData", StringComparison.Ordinal) ||
            variable.Initializer?.Value is ObjectCreationExpressionSyntax creation &&
            creation.Type.ToString().Contains("TheoryData", StringComparison.Ordinal)));
    }

    internal static bool StricterCondition(string before, string after, bool negate = false)
    {
        if (before == after) return true;
        ExpressionSyntax ParseCondition(string text)
        {
            if (text.StartsWith("assert ", StringComparison.Ordinal)) text = text[7..];
            text = System.Text.RegularExpressions.Regex.Replace(text, @"\band\b", "&&");
            return SyntaxFactory.ParseExpression(text);
        }
        ExpressionSyntax Unwrap(ExpressionSyntax expression) => expression is ParenthesizedExpressionSyntax parentheses
            ? Unwrap(parentheses.Expression) : expression;
        IEnumerable<ExpressionSyntax> Terms(ExpressionSyntax expression)
        {
            expression = Unwrap(expression);
            return !negate && expression is BinaryExpressionSyntax binary && binary.IsKind(SyntaxKind.LogicalAndExpression)
                ? Terms(binary.Left).Concat(Terms(binary.Right)) : [expression];
        }
        bool Bound(ExpressionSyntax oldExpression, ExpressionSyntax newExpression)
        {
            oldExpression = Unwrap(oldExpression); newExpression = Unwrap(newExpression);
            if (Tokens(oldExpression) == Tokens(newExpression)) return true;
            if (oldExpression is not BinaryExpressionSyntax oldBinary || newExpression is not BinaryExpressionSyntax newBinary ||
                oldBinary.Kind() != newBinary.Kind() || Tokens(oldBinary.Left) != Tokens(newBinary.Left) ||
                oldBinary.Kind() is not (SyntaxKind.GreaterThanExpression or SyntaxKind.GreaterThanOrEqualExpression or
                    SyntaxKind.LessThanExpression or SyntaxKind.LessThanOrEqualExpression)) return false;
            if (!double.TryParse(Tokens(oldBinary.Right), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double oldNumber) ||
                !double.TryParse(Tokens(newBinary.Right), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double newNumber)) return false;
            bool minimum = oldBinary.OperatorToken.Text.StartsWith('>') != negate;
            return minimum ? newNumber >= oldNumber : newNumber <= oldNumber;
        }
        var oldCondition = ParseCondition(before); var newCondition = ParseCondition(after);
        if (oldCondition.ContainsDiagnostics || newCondition.ContainsDiagnostics) return false;
        var remaining = Terms(newCondition).ToList();
        foreach (var term in Terms(oldCondition))
        {
            int match = remaining.FindIndex(candidate => Bound(term, candidate));
            if (match < 0) return false;
            remaining.RemoveAt(match);
        }
        return true;
    }

    private static bool Stronger(SyntaxNode before, SyntaxNode after)
    {
        if (before is not InvocationExpressionSyntax oldCall || after is not InvocationExpressionSyntax newCall ||
            Tokens(oldCall.Expression) != Tokens(newCall.Expression) || oldCall.ArgumentList.Arguments.Count == 0 ||
            newCall.ArgumentList.Arguments.Count != oldCall.ArgumentList.Arguments.Count) return false;
        string name = oldCall.Expression is MemberAccessExpressionSyntax member ? member.Name.Identifier.Text : oldCall.Expression.ToString();
        if (name is not ("True" or "False" or "Check" or "check")) return false;
        bool Number(ExpressionSyntax expression, out double value)
        {
            if (expression is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.NumericLiteralExpression))
            {
                value = Convert.ToDouble(literal.Token.Value, System.Globalization.CultureInfo.InvariantCulture);
                return true;
            }
            if (expression is PrefixUnaryExpressionSyntax unary && unary.IsKind(SyntaxKind.UnaryMinusExpression) &&
                Number(unary.Operand, out value)) { value = -value; return true; }
            value = 0;
            return false;
        }
        for (int index = 0; index < oldCall.ArgumentList.Arguments.Count; index++)
        {
            var oldArgument = oldCall.ArgumentList.Arguments[index].Expression;
            var newArgument = newCall.ArgumentList.Arguments[index].Expression;
            bool oldNumeric = Number(oldArgument, out double oldValue), newNumeric = Number(newArgument, out double newValue);
            if ((oldNumeric || newNumeric) && (!oldNumeric || !newNumeric || newValue < oldValue)) return false;
        }
        var first = oldCall.ArgumentList.Arguments[0].Expression;
        return Number(first, out _) || StricterCondition(Tokens(first), Tokens(newCall.ArgumentList.Arguments[0].Expression), name == "False");
    }

    private static void TheoryData(CheckChange[] changes, List<ReviewFinding> findings)
    {
        var trees = changes.SelectMany(change => new[]
        {
            (Change: change, Before: true, Root: Parse(change.Before ?? "")),
            (Change: change, Before: false, Root: Parse(change.After ?? ""))
        }).ToArray();
        string Owner(SyntaxNode node) => string.Join(".", node.AncestorsAndSelf().OfType<BaseNamespaceDeclarationSyntax>()
            .Reverse().Select(space => space.Name.ToString()).Concat(node.AncestorsAndSelf().OfType<TypeDeclarationSyntax>()
                .Reverse().Select(type => type.Identifier.Text)));
        var providers = trees.SelectMany(tree => tree.Root.DescendantNodes().OfType<AttributeSyntax>())
            .Where(attribute => attribute.Name.ToString().Split('.').Last() is "MemberData" or "MemberDataAttribute")
            .Select(attribute => (Type: Owner(attribute), Name: attribute.ArgumentList?.Arguments.FirstOrDefault()?.Expression switch
            {
                InvocationExpressionSyntax call when call.Expression.ToString() == "nameof" => call.ArgumentList.Arguments[0].ToString().Split('.').Last(),
                LiteralExpressionSyntax literal => literal.Token.ValueText,
                _ => ""
            })).ToHashSet();
        IEnumerable<SyntaxNode> Rows(SyntaxNode root)
        {
            foreach (var node in root.DescendantNodes().OfType<InitializerExpressionSyntax>())
            {
                bool theory = node.Parent is ObjectCreationExpressionSyntax created &&
                    created.Type.ToString().Contains("TheoryData", StringComparison.Ordinal) ||
                    node.Parent is ImplicitObjectCreationExpressionSyntax && node.Ancestors().Any(declaration => declaration switch
                    {
                        VariableDeclarationSyntax variable => variable.Type.ToString().Contains("TheoryData", StringComparison.Ordinal),
                        MethodDeclarationSyntax method => method.ReturnType.ToString().Contains("TheoryData", StringComparison.Ordinal),
                        PropertyDeclarationSyntax property => property.Type.ToString().Contains("TheoryData", StringComparison.Ordinal),
                        _ => false
                    });
                string member = node.Ancestors().Select(declaration => declaration switch
                {
                    MethodDeclarationSyntax method => method.Identifier.Text,
                    PropertyDeclarationSyntax property => property.Identifier.Text,
                    VariableDeclaratorSyntax variable => variable.Identifier.Text,
                    _ => ""
                }).FirstOrDefault(name => name.Length != 0) ?? "";
                bool provider = providers.Contains((Owner(node), member)) && !node.Ancestors().OfType<InitializerExpressionSyntax>().Any() &&
                    !node.Ancestors().OfType<YieldStatementSyntax>().Any();
                if (theory || provider)
                    foreach (var row in node.Expressions) yield return row;
            }
            foreach (var statement in root.DescendantNodes().OfType<YieldStatementSyntax>())
            {
                string member = statement.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault()?.Identifier.Text ?? "";
                if (statement.Expression != null && providers.Contains((Owner(statement), member))) yield return statement.Expression;
            }
        }
        var after = trees.Where(tree => !tree.Before).SelectMany(tree => Rows(tree.Root))
            .GroupBy(Tokens).ToDictionary(group => group.Key, group => group.Count());
        var occurrences = new Dictionary<(string Path, string Text), int>();
        foreach (var tree in trees.Where(tree => tree.Before && tree.Change.After != null))
        foreach (var row in Rows(tree.Root))
        {
            string text = Tokens(row);
            var key = (tree.Change.OldPath, text);
            int occurrence = occurrences.GetValueOrDefault(key) + 1;
            occurrences[key] = occurrence;
            if (after.GetValueOrDefault(text) > 0) { after[text]--; continue; }
            findings.Add(new(tree.Change.OldPath, "assertion-removed", "data-row:" + Hash(text) + ":" + occurrence, "Test data row removed",
                row.GetLocation().GetLineSpan().StartLinePosition.Line + 1));
        }
    }

    private sealed record Method(CheckChange Change, MethodDeclarationSyntax Syntax, bool Disabled);
    private static void CSharp(CheckChange[] changes, List<ReviewFinding> findings)
    {
        Method[] Read(bool before) => changes.SelectMany(change =>
        {
            string source = (before ? change.Before : change.After) ?? "";
            return Parse(source).DescendantNodes().OfType<MethodDeclarationSyntax>()
                .Select(method => new Method(change, method, TestExclusions.DisabledByFalse(source, method.SpanStart)));
        }).ToArray();
        var before = Read(true);
        var after = Read(false);
        var available = after.ToHashSet();
        var byMember = after.GroupBy(candidate => (Member(candidate.Syntax), IsTest(candidate.Syntax)))
            .ToDictionary(group => group.Key, group => group.ToArray());
        var byTokens = after.GroupBy(candidate => Hash(Tokens(candidate.Syntax)))
            .ToDictionary(group => group.Key, group => group.ToArray());
        string Body(MethodDeclarationSyntax method) => Tokens(method.ParameterList) + ":" +
            (method.Body != null ? Tokens(method.Body) : method.ExpressionBody != null ? Tokens(method.ExpressionBody) : "");
        var byBody = after.Where(candidate => IsTest(candidate.Syntax)).GroupBy(candidate => Hash(Body(candidate.Syntax)))
            .ToDictionary(group => group.Key, group => group.ToArray());
        var previousMembers = before.GroupBy(candidate => Member(candidate.Syntax))
            .ToDictionary(group => group.Key, group => group.ToArray());
        foreach (var previous in before)
        {
            var method = previous.Syntax;
            // Fully qualified names are stable across files. An identical definition may also move to another class.
            var replacement = byMember.GetValueOrDefault((Member(method), IsTest(method)))?.FirstOrDefault(available.Contains)
                ?? byTokens.GetValueOrDefault(Hash(Tokens(method)))?.FirstOrDefault(available.Contains)
                ?? (IsTest(method) ? byBody.GetValueOrDefault(Hash(Body(method)))?.FirstOrDefault(available.Contains) : null);
            if (replacement != null) available.Remove(replacement);
            if (previous.Change.After == null)
            {
                if (replacement != null && IsTest(method)) AddSkips(replacement, method, findings);
                continue; // one row for a whole deleted file
            }
            if (replacement == null && IsTest(method))
            {
                string identifier = Member(method) + ":" + Hash(Tokens(method));
                if (previousMembers[Member(method)].Length > 1)
                    identifier += ":definition:" + (Array.IndexOf(previousMembers[Member(method)], previous) + 1);
                findings.Add(new(previous.Change.OldPath, "test-removed", identifier,
                    "Test method removed or renamed", method.GetLocation().GetLineSpan().StartLinePosition.Line + 1));
                continue;
            }
            if (replacement != null && IsTest(method))
            {
                AddSkips(replacement, method, findings);
                if (replacement.Disabled && !previous.Disabled)
                    findings.Add(new(replacement.Change.Path, "test-disabled", Member(method) + ":" + Hash(Tokens(method)),
                        "Test is inside #if false", replacement.Syntax.GetLocation().GetLineSpan().StartLinePosition.Line + 1));
            }
            CompareAssertions(previous.Change.OldPath, Member(method), Assertions(method, ConsoleAssertions(previous.Change.OldPath)),
                replacement == null ? [] : Assertions(replacement.Syntax, ConsoleAssertions(replacement.Change.Path)), findings);
        }
        foreach (var added in available.Where(candidate => IsTest(candidate.Syntax)))
        {
            AddSkips(added, null, findings);
            if (added.Disabled)
                findings.Add(new(added.Change.Path, "test-disabled", Member(added.Syntax) + ":" + Hash(Tokens(added.Syntax)), "Test is inside #if false"));
        }
    }

    private static void CompareAssertions(string path, string member, IEnumerable<SyntaxNode> before,
        IEnumerable<SyntaxNode> after, List<ReviewFinding> findings)
    {
        var available = after.ToList();
        var occurrences = new Dictionary<string, int>();
        foreach (var assertion in before)
        {
            string text = Tokens(assertion);
            int occurrence = occurrences.GetValueOrDefault(text) + 1;
            occurrences[text] = occurrence;
            int match = available.FindIndex(candidate => Tokens(candidate) == text);
            if (match < 0) match = available.FindIndex(candidate => Stronger(assertion, candidate));
            if (match >= 0) { available.RemoveAt(match); continue; }
            findings.Add(new(path, "assertion-removed", member + ":" + Hash(text) + ":" + occurrence,
                "Removed assertion: " + text, assertion.GetLocation().GetLineSpan().StartLinePosition.Line + 1));
        }
    }

    private static void TopLevel(CheckChange[] changes, List<ReviewFinding> findings)
    {
        foreach (var change in changes.Where(change => change.Before != null && change.After != null))
        {
            IEnumerable<SyntaxNode> Read(string source) => ((CompilationUnitSyntax)Parse(source)).Members
                .OfType<GlobalStatementSyntax>().SelectMany(statement => Assertions(statement, ConsoleAssertions(change.Path)));
            CompareAssertions(change.OldPath, "<top-level>", Read(change.Before!), Read(change.After!), findings);
        }
    }

    private static void AddSkips(Method current, MethodDeclarationSyntax? before, List<ReviewFinding> findings)
    {
        IEnumerable<string> Timeouts(MethodDeclarationSyntax method) => method.AttributeLists.SelectMany(list => list.Attributes)
            .Where(attribute => attribute.Name.ToString().Split('.').Last() is "Fact" or "Theory" or "FactAttribute" or "TheoryAttribute")
            .SelectMany(attribute => attribute.ArgumentList?.Arguments ?? default)
            .Where(argument => argument.NameEquals?.Name.Identifier.Text == "Timeout").Select(Tokens);
        if (before != null && Timeouts(before).Any() && !Timeouts(current.Syntax).Any())
            findings.Add(new(current.Change.Path, "gate-removed", Member(current.Syntax) + ":timeout:" + Hash(string.Join(" ", Timeouts(before))),
                "xUnit timeout removed", current.Syntax.GetLocation().GetLineSpan().StartLinePosition.Line + 1));
        foreach (var skip in Skips(current.Syntax).Except(before == null ? [] : Skips(before)))
            findings.Add(new(current.Change.Path, "test-skipped", Member(current.Syntax) + ":" + Hash(skip), "Added test skip: " + skip,
                current.Syntax.GetLocation().GetLineSpan().StartLinePosition.Line + 1));
    }

    internal static int Run(string root, Options options)
    {
        string basis = options.Get("Base") ?? throw new ReleaseException("check-loosening requires -Base <ref>", 64);
        if (options.Has("RecordApprovals") && string.IsNullOrWhiteSpace(options.Get("Reason")))
            throw new ReleaseException("-RecordApprovals requires -Reason naming the task spec or maintainer decision", 64);
        var changes = ReviewChangeSet.Read(root, basis, options.Get("Git", "git"));
        var findings = Scan(changes, ReviewTestSources.Create(root));
        var regenerated = changes.SelectMany(change => new[] { change.OldPath, change.Path })
            .Where(ReviewChangeSet.Regenerated).Distinct().Order(StringComparer.Ordinal).ToArray();
        foreach (string path in regenerated) Console.WriteLine(path + ": regenerated: review the diff");
        var audit = CheckChangeApprovals.Audit(root, basis, changes, args => ReviewChangeSet.Git(options.Get("Git", "git"), args, root));
        if (options.Has("RecordApprovals"))
        {
            CheckChangeApprovals
                .Record(root, options.Get("Task") ?? "", options
                    .Get("MaintainerDate", DateTime.UtcNow.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)),
                options.Get("Reason")!, findings, options.Get("ApprovalOutput"), audit.Unauthorized);
            Console.WriteLine("Approval draft recorded; review and commit separately with subject starting 'Record maintainer approval'.");
            return 0;
        }
        var unapproved = CheckChangeApprovals.Unapproved(findings, CheckChangeApprovals.Load(root).Except(audit.Unauthorized))
            .Concat(audit.Violations ?? []).ToArray();
        if (options.Get("Output") is { } output)
            File
                .WriteAllText(output, System.Text.Json.JsonSerializer
                    .Serialize(new { basis, findings, unapproved, addedApprovals = audit.Added,
                        unauthorizedApprovals = audit.Unauthorized, approvalViolations = audit.Violations,
                        regeneratedFiles = regenerated.Select(path => new { path, message = "regenerated: review the diff" }) },
                        CheckChangeApprovals.Json)
                    + "\n");
        foreach (var approval in audit.Added)
            Console.WriteLine($"ADDED APPROVAL {approval.Task}: {approval.Path} {approval.Kind} [{approval.Identifier}]");
        foreach (var approval in audit.Unauthorized)
            Console.Error.WriteLine("Approval lacks a separate maintainer approval commit: " + approval.Identifier);
        foreach (var row in unapproved)
            Console.Error.WriteLine($"{row.Path}:{row.Line}: {row.Kind} [{row.Identifier}] {row.Message}");
        int total = findings.Count + (audit.Violations?.Length ?? 0);
        Console.WriteLine($"check-loosening: {total} findings, {total - unapproved.Length} approved, {unapproved.Length} unapproved");
        return unapproved.Length == 0 && audit.Unauthorized.Length == 0 ? 0 : 1;
    }
}
