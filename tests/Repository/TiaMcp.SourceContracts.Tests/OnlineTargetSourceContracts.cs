using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Diagnostics;
using System.Text.Json;
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
        var seen = new HashSet<(string Path, int Start)>();
        var releaseSymbols = ReleaseSymbols(root).ToArray();
        foreach (string path in Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories))
        {
            if (path.Split(Path.DirectorySeparatorChar).Any(part => part is "obj" or "bin")) continue;
            string source = File.ReadAllText(path);
            if (!Regex.IsMatch(source, @"\.\s*GoOnline")) continue;
            var directives = new Stack<string>();
            foreach (string line in source.Split('\n'))
            {
                string trimmed = line.Trim();
                UpdateDirectives(directives, trimmed);
                if (!trimmed.StartsWith("//", StringComparison.Ordinal)
                    && Regex.IsMatch(line, @"\.\s*GoOnline(?:ToPrimary|ToBackup)?\s*\(\s*\)"))
                {
                    Assert.Equal("OnlineDownloadAdapter.cs", Path.GetFileName(path));
                    Assert.Contains("#if !ONLINE_ADDRESS_OVERLOAD", directives);
                }
            }
            foreach (var symbols in SymbolCombinations(source, releaseSymbols))
            {
                bool overload = symbols.Contains("ONLINE_ADDRESS_OVERLOAD", StringComparer.Ordinal);
                var tree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(preprocessorSymbols: symbols));
                foreach (var call in ParameterlessCalls(tree))
                {
                    if (seen.Add((path, call.SpanStart))) calls++;
                    Assert.False(overload, path);
                    Assert.Equal("OnlineDownloadAdapter.cs", Path.GetFileName(path));
                    var method = call.Ancestors().OfType<MethodDeclarationSyntax>().First();
                    string receiver = Receiver(call);
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

    [Fact]
    public void Conditional_access_and_elif_calls_are_detected_without_inheriting_the_previous_branch_guard()
    {
        const string source = """
            class Fixture {
                void Connect(dynamic provider) {
            #if !ONLINE_ADDRESS_OVERLOAD
                    provider.GoOnline(address);
            #elif ONLINE_RH
                    provider?.GoOnline();
            #endif
                }
            }
            """;
        var tree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(preprocessorSymbols: new[] { "ONLINE_ADDRESS_OVERLOAD", "ONLINE_RH" }));
        var call = Assert.Single(ParameterlessCalls(tree));
        Assert.IsType<MemberBindingExpressionSyntax>(call.Expression);
        Assert.Equal("provider", Receiver(call));
        var directives = new Stack<string>();
        foreach (string line in source[..call.SpanStart].Split('\n')) UpdateDirectives(directives, line.Trim());
        Assert.DoesNotContain("#if !ONLINE_ADDRESS_OVERLOAD", directives);
        var beforeV21 = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(preprocessorSymbols: new[] { "ONLINE_RH" }));
        Assert.Empty(ParameterlessCalls(beforeV21));
    }

    [Theory]
    [InlineData("ConnectOnlinePlc", "GoOnlineV4", "ipAddress")]
    [InlineData("DownloadPlc", "DownloadToPlcV4", "targetIpAddress")]
    [InlineData("UploadDeviceParameters", "UploadDeviceParametersV4", "targetIpAddress")]
    public void Frozen_online_declarations_match_current_signatures_and_required_address_parameters(string tool, string name, string parameter)
    {
        string root = Repository.FindRoot(AppContext.BaseDirectory);
        string source = File.ReadAllText(Path.Combine(root, "src/Shared/OnlineDownloadTools.cs"));
        var method = CSharpSyntaxTree.ParseText(source).GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Single(method => method.Identifier.ValueText == name);
        using var catalog = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "reference/tool-catalog/engine-source.json")));
        var declaration = catalog.RootElement.GetProperty("registrations").GetProperty(tool);
        Assert.Equal(source[method.Modifiers.First().SpanStart..method.ParameterList.Span.End], declaration.GetProperty("signature").GetString());
        Assert.Equal(method.ParameterList.Parameters.Select(item => item.Identifier.ValueText),
            declaration.GetProperty("parameters").EnumerateObject().Select(item => item.Name));
        var address = method.ParameterList.Parameters.Single(item => item.Identifier.ValueText == parameter);
        var frozen = declaration.GetProperty("parameters").GetProperty(parameter);
        Assert.Null(address.Default);
        Assert.Equal(JsonValueKind.Null, frozen.GetProperty("default").ValueKind);
        Assert.False(frozen.TryGetProperty("value", out _));
        Assert.Equal(address.Type!.ToString(), frozen.GetProperty("type").GetString());
        string description = string.Concat(address.AttributeLists.SelectMany(list => list.Attributes)
            .Single(attribute => attribute.Name.ToString() == "Description").ArgumentList!.Arguments.Single().Expression.DescendantTokens()
            .Where(token => token.IsKind(SyntaxKind.StringLiteralToken)).Select(token => token.ValueText));
        Assert.Equal(description, frozen.GetProperty("description").GetString());
    }

    private static IEnumerable<InvocationExpressionSyntax> ParameterlessCalls(Microsoft.CodeAnalysis.SyntaxTree tree)
        => tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>().Where(call => call.ArgumentList.Arguments.Count == 0
            && (call.Expression switch {
                MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
                MemberBindingExpressionSyntax binding => binding.Name.Identifier.ValueText,
                _ => "" }) is "GoOnline" or "GoOnlineToPrimary" or "GoOnlineToBackup");

    private static string Receiver(InvocationExpressionSyntax call)
        => call.Expression is MemberAccessExpressionSyntax member ? member.Expression.ToString()
            : call.Ancestors().OfType<ConditionalAccessExpressionSyntax>().First().Expression.ToString();

    private static void UpdateDirectives(Stack<string> directives, string trimmed)
    {
        if (trimmed.StartsWith("#if ", StringComparison.Ordinal)) directives.Push(trimmed);
        else if (trimmed.StartsWith("#endif", StringComparison.Ordinal)) directives.Pop();
        else if (trimmed.StartsWith("#elif ", StringComparison.Ordinal))
        { directives.Pop(); directives.Push(trimmed); }
        else if (trimmed.StartsWith("#else", StringComparison.Ordinal))
        { string branch = directives.Pop(); directives.Push("#else " + branch); }
    }

    private static IEnumerable<string[]> SymbolCombinations(string source, string[][] releases)
    {
        var names = Regex.Matches(source, @"(?m)^\s*#(?:if|elif)\s+([^\r\n]+)")
            .SelectMany(match => Regex.Matches(match.Groups[1].Value.Split("//")[0], @"\b[A-Za-z_]\w*\b").Select(token => token.Value))
            .Where(name => name is not ("true" or "false")).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        // Exhaust each file's branch symbols as well as the eight real adapter profiles, including symbols in #elif.
        return releases.Concat(Subsets(names, 0)).DistinctBy(symbols => string.Join(";", symbols.Order(StringComparer.Ordinal)));
    }

    private static IEnumerable<string[]> Subsets(string[] names, int index)
    {
        if (index == names.Length) { yield return Array.Empty<string>(); yield break; }
        foreach (var subset in Subsets(names, index + 1))
        { yield return subset; yield return subset.Append(names[index]).ToArray(); }
    }

    private static IEnumerable<string[]> ReleaseSymbols(string root)
    {
        foreach (string release in new[] { "V14Sp1", "V15_1", "V16", "V17", "V18", "V19", "V20", "V21" })
        {
            string project = Directory.GetFiles(Path.Combine(root, "src/Adapters", release), "*.csproj").Single();
            var start = new ProcessStartInfo("dotnet") { WorkingDirectory = root, UseShellExecute = false,
                RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string argument in new[] { "msbuild", project, "-nologo", "-getProperty:DefineConstants", "-p:Configuration=Debug",
                "-m:1", "-nodeReuse:false" }) start.ArgumentList.Add(argument);
            var process = ProcessDeadline.Capture(start);
            string output = process.StandardOutput;
            Assert.True(process.ExitCode == 0, project + ": " + process.StandardError);
            yield return output.Trim().Split(';', StringSplitOptions.RemoveEmptyEntries);
        }
    }
}
