using TiaMcp.BuildCommon;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Text.RegularExpressions;
using Xunit;

namespace TiaMcp.SourceContracts.Tests;

public sealed class NoSessionLockSources
{
    [Fact]
    public void Recovery_tests_use_production_budgets()
    {
        string root = Repository.FindRoot(AppContext.BaseDirectory);
        string source = Repository.ReadSource(Path.Combine(root, "tests/FoundationHost/TiaMcp.FoundationHost.Tests/NoLockRecoveryTests.cs"));
        var methods = CSharpSyntaxTree.ParseText(source).GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Where(method => method.ToString().Contains("Worker.Call(", StringComparison.Ordinal)
                && (method.ToString().Contains("StopOwnedChild()", StringComparison.Ordinal)
                    || method.ToString().Contains("scope.Arm(", StringComparison.Ordinal))).ToArray();
        Assert.NotEmpty(methods);
        foreach (var method in methods)
            Assert.True(method.ToString().Contains("ProductionWorkerBudgets", StringComparison.Ordinal)
                || method.ToString().Contains("timeoutConfig:", StringComparison.Ordinal), method.Identifier.ValueText);
    }

    private static Dictionary<string, string> PortalSources()
    {
        string root = Repository.FindRoot(AppContext.BaseDirectory);
        return Directory.EnumerateFiles(Path.Combine(root, "src/Adapters/Native/Session"), "*.cs", SearchOption.AllDirectories)
            .ToDictionary(file => Path.GetRelativePath(root, file).Replace('\\', '/'), Repository.ReadSource);
    }

    private static void RequireKnownPortalStarts(IReadOnlyDictionary<string, string> sources, string host)
    {
        const string isolated = "project-lifecycle.ConnectIsolatedPortal";
        var sites = new Dictionary<string, string>(StringComparer.Ordinal) {
            ["src/Adapters/Native/Session/ProjectLifecycleWorker.cs:ConnectIsolatedPortal:workerStartedPortal"] = isolated,
            ["src/Adapters/Native/Session/ProjectLifecycleWorker.cs:ConnectIsolatedPortal:ownsPortal"] = isolated,
            ["src/Adapters/Native/Session/ProjectLifecycleWorker.cs:AdoptCreatedProjectLifecycleProcess:workerStartedPortal"] = isolated,
            ["src/Adapters/Native/Session/ProjectLifecycleWorker.cs:AdoptCreatedProjectLifecycleProcess:ownsPortal"] = isolated
        };
        var guard = CSharpSyntaxTree.ParseText(host).GetRoot().DescendantNodes().OfType<IfStatementSyntax>()
            .Single(statement => statement.Condition.ToString().Contains(isolated, StringComparison.Ordinal)
                && statement.Statement.ToString().Contains("ownsPortal = true", StringComparison.Ordinal));
        var operations = guard.Condition.DescendantNodes().OfType<LiteralExpressionSyntax>()
            .Where(literal => literal.IsKind(SyntaxKind.StringLiteralExpression)).Select(literal => literal.Token.ValueText).ToArray();
        Assert.Contains(isolated, operations);
        Assert.Contains("project-lifecycle.ExecuteProjectScaffoldPrimitive", operations);
        Assert.Contains("arguments[\"request\"]?[\"Kind\"] == \"connect\"", guard.Condition.ToString(), StringComparison.Ordinal);
        var found = new List<string>();
        foreach (var (file, source) in sources)
        {
            // Include ownership sites in every release branch, including branches inactive in this test build.
            string allBranches = Regex.Replace(source, @"(?m)^[ \t]*#(?:if|elif|else|endif|define|undef)\b[^\r\n]*", "");
            foreach (var assignment in CSharpSyntaxTree.ParseText(allBranches).GetRoot().DescendantNodes().OfType<AssignmentExpressionSyntax>()
                .Where(assignment => AssignedName(assignment.Left) is "workerStartedPortal" or "ownsPortal"
                    && assignment.Right.IsKind(SyntaxKind.TrueLiteralExpression)))
            {
                string method = assignment.Ancestors().OfType<MethodDeclarationSyntax>().First().Identifier.ValueText;
                string site = file + ":" + method + ":" + AssignedName(assignment.Left);
                Assert.True(sites.TryGetValue(site, out string? operation), "Unmapped portal ownership assignment: " + site);
                Assert.Contains(operation!, operations);
                found.Add(site);
            }
        }
        Assert.Equal(sites.Keys.Order(StringComparer.Ordinal), found.Order(StringComparer.Ordinal));
    }

    private static string? AssignedName(ExpressionSyntax expression) => expression switch {
        IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
        MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
        _ => null
    };

    [Fact]
    public void Portal_starting_operations_are_known_to_the_recovery_guard()
    {
        string root = Repository.FindRoot(AppContext.BaseDirectory);
        RequireKnownPortalStarts(PortalSources(), Repository.ReadSource(Path.Combine(root, "src/FoundationHost/WorkerClient.cs")));
    }

    [Fact]
    public void An_unmapped_portal_start_fails_the_recovery_guard_contract()
    {
        string root = Repository.FindRoot(AppContext.BaseDirectory);
        var sources = PortalSources();
        sources.Add("src/Adapters/Native/Session/NewPortal.cs", "class NewPortal { bool ownsPortal;\n#if TIA21\n"
            + "void ConnectThirdPortal() { this.ownsPortal = true; }\n#endif\n}");
        Assert.Throws<Xunit.Sdk.TrueException>(() => RequireKnownPortalStarts(sources,
            Repository.ReadSource(Path.Combine(root, "src/FoundationHost/WorkerClient.cs"))));
    }

    [Fact]
    public void Session_reset_required_is_never_produced()
    {
        string root = Path.Combine(Repository.FindRoot(AppContext.BaseDirectory), "src");
        foreach (string file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj")))
        {
            if (Path.GetFileName(file) == "V4Json.cs") continue;
            string source = Repository.ReadSource(file);
            Assert.DoesNotContain("new SessionResetRequiredDetails", source, StringComparison.Ordinal);
            Assert.DoesNotContain("HostBehavior.SessionReset(", source, StringComparison.Ordinal);
        }
    }
}
