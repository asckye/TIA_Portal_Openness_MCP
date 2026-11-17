using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Text.RegularExpressions;
using TiaMcp.Tests.Shared;
using TiaMcp.BuildCommon;
using Xunit;

namespace TiaMcp.SourceContracts.Tests;

public sealed class OnlineTargetSourceContracts
{
    [Fact]
    public void Parameterless_online_calls_require_pre_V21_symbol_and_prior_address_application()
    {
        string root = Repository.FindRoot(AppContext.BaseDirectory);
        int calls = 0;
        foreach (string path in Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories))
        {
            if (path.Split(Path.DirectorySeparatorChar).Any(part => part is "obj" or "bin")) continue;
            string source = File.ReadAllText(path);
            if (!source.Contains(".GoOnline", StringComparison.Ordinal)) continue;
            var directives = new Stack<string>();
            foreach (string line in source.Split('\n'))
            {
                string trimmed = line.Trim();
                if (trimmed.StartsWith("#if ", StringComparison.Ordinal)) directives.Push(trimmed);
                else if (trimmed.StartsWith("#endif", StringComparison.Ordinal)) directives.Pop();
                else if (trimmed.StartsWith("#else", StringComparison.Ordinal))
                { string branch = directives.Pop(); directives.Push("#else " + branch); }
                if (!trimmed.StartsWith("//", StringComparison.Ordinal)
                    && Regex.IsMatch(line, @"\.GoOnline(?:ToPrimary|ToBackup)?\s*\(\s*\)"))
                {
                    Assert.Equal("OnlineDownloadAdapter.cs", Path.GetFileName(path));
                    Assert.Contains("#if !ONLINE_ADDRESS_OVERLOAD", directives);
                }
            }
            foreach (bool overload in new[] { false, true })
            {
                var symbols = overload ? new[] { "ONLINE_RH", "ONLINE_ADDRESS_OVERLOAD" } : new[] { "ONLINE_RH" };
                var tree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(preprocessorSymbols: symbols));
                foreach (var call in tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>().Where(call =>
                    call.ArgumentList.Arguments.Count == 0 && call.Expression is MemberAccessExpressionSyntax member
                    && member.Name.Identifier.ValueText is "GoOnline" or "GoOnlineToPrimary" or "GoOnlineToBackup"))
                {
                    calls++;
                    Assert.False(overload, path);
                    Assert.Equal("OnlineDownloadAdapter.cs", Path.GetFileName(path));
                    var method = call.Ancestors().OfType<MethodDeclarationSyntax>().First();
                    string receiver = ((MemberAccessExpressionSyntax)call.Expression).Expression.ToString();
                    var applications = method.DescendantNodes().OfType<InvocationExpressionSyntax>().Where(apply =>
                        apply.SpanStart < call.SpanStart && apply.Expression.ToString() == receiver + ".Configuration.ApplyConfiguration").ToArray();
                    var application = Assert.Single(applications);
                    Assert.Equal(receiver == "rh" ? "rhAddress" : "address", application.ArgumentList.Arguments.Single().ToString());
                    string preceding = source[application.SpanStart..call.SpanStart];
                    Assert.Contains("OnlineTransitionPolicy.RouteApplyRefusal(true,", preceding);
                    Assert.Contains("if (" + (receiver == "rh" ? "rhRefusal" : "routeRefusal") + " != null)", preceding);
                }
            }
        }
        Assert.Equal(3, calls);
    }
}
