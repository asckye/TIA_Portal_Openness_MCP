using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Xml.Linq;
using TiaMcp.BuildCommon;
using TiaMcp.Engine.Tests.Shared;
using Xunit;

namespace TiaMcp.SourceContracts.Tests;

public sealed class GuiSchedulingContracts
{
    [Fact]
    public void EveryGuiInputMethodUsesTheSerialReservedLane()
    {
        string root = Repository.FindRoot(AppContext.BaseDirectory);
        string directory = Path.Combine(root, "tests/Studio/TiaOpenness.Gui.Tests");
        var checkedMethods = FindInputTests(ProjectSources(directory));
        foreach (string name in checkedMethods)
        {
            int dot = name.LastIndexOf('.');
            string cls = name[..dot];
            string method = name[(dot + 1)..];
            Assert.True(TimeSensitiveTests.Contains(cls, method), name);
            string key = "T:" + name;
            Assert.Equal(0, TimeSensitiveTests.SensitivePartition(key));
            Assert.Equal(3, TestShard.Assign(cls, key, key, 4,
                new Dictionary<string, int>(StringComparer.Ordinal), reserveSensitive: true));
        }
        Assert.NotEmpty(checkedMethods);
        Assert.Contains(checkedMethods, name => name.Contains(".WpfContextTests.", StringComparison.Ordinal));
        Assert.Contains("TiaOpenness.Gui.Tests.WorkbenchControlTests.Discrete_Wpf_input_marks_human_activity_for_1500ms", checkedMethods);
    }

    internal static IEnumerable<string> ProjectSources(string directory)
    {
        var paths = Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories)
            .Where(path => !Path.GetRelativePath(directory, path).Split(Path.DirectorySeparatorChar)
                .Any(part => part is "bin" or "obj")).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var project = XDocument.Load(Path.Combine(directory, "TiaOpenness.Gui.Tests.csproj"));
        foreach (var include in project.Descendants("Compile").Attributes("Include"))
            paths.Add(Path.GetFullPath(Path.Combine(directory, include.Value)));
        return paths.OrderBy(path => path, StringComparer.Ordinal).Select(File.ReadAllText);
    }

    internal static string[] FindInputTests(IEnumerable<string> sources)
    {
        var trees = sources.Select(source => CSharpSyntaxTree.ParseText(source)).ToArray();
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Select(path => MetadataReference.CreateFromFile(path));
        var compilation = CSharpCompilation.Create("GuiInputAudit", trees, references);
        var members = trees.SelectMany(tree => tree.GetRoot().DescendantNodes())
            .Where(node => node is MethodDeclarationSyntax or ConstructorDeclarationSyntax or PropertyDeclarationSyntax)
            .ToArray();
        var symbols = members.ToDictionary(member => member,
            member => compilation.GetSemanticModel(member.SyntaxTree).GetDeclaredSymbol(member)!);
        // Resolve project helpers across files and partial classes. Candidate symbols retain
        // conservative overload coverage when product-only references are unavailable here.
        var dependencies = members.ToDictionary(member => member, member =>
        {
            var model = compilation.GetSemanticModel(member.SyntaxTree);
            return member.DescendantNodes().OfType<ExpressionSyntax>()
                .Select(node => model.GetSymbolInfo(node)).SelectMany(info => info.Symbol == null
                    ? info.CandidateSymbols.AsEnumerable() : new[] { info.Symbol })
                .Select(symbol => symbol.OriginalDefinition).ToHashSet(SymbolEqualityComparer.Default);
        });
        var sensitive = members.Where(UsesInput).ToHashSet();
        bool changed;
        do
        {
            var sensitiveSymbols = sensitive.Select(member => symbols[member]).ToHashSet(SymbolEqualityComparer.Default);
            changed = false;
            foreach (var member in members)
                if (dependencies[member].Overlaps(sensitiveSymbols)) changed |= sensitive.Add(member);
        } while (changed);
        return sensitive.OfType<MethodDeclarationSyntax>()
            .Where(method => method.AttributeLists.SelectMany(list => list.Attributes).Any(attribute =>
                attribute.Name.ToString().Split('.').Last() is
                    "Fact" or "FactAttribute" or "Theory" or "TheoryAttribute" or
                    "CheckFact" or "CheckFactAttribute" or "CheckTheory" or "CheckTheoryAttribute"))
            .Select(method => string.Join(".", method.Ancestors().Reverse().Select(node => node switch
            {
                BaseNamespaceDeclarationSyntax ns => ns.Name.ToString(),
                TypeDeclarationSyntax type => type.Identifier.ValueText,
                _ => null
            }).Where(name => name != null).Append(method.Identifier.ValueText)))
            .Distinct(StringComparer.Ordinal).OrderBy(name => name, StringComparer.Ordinal).ToArray();
    }

    private static bool UsesInput(SyntaxNode member) => member.DescendantNodes().Any(node =>
        node is InvocationExpressionSyntax invocation && invocation.Expression switch
        {
            MemberAccessExpressionSyntax access => access.Name.Identifier.ValueText is "PressSpace" or "Focus" or "Activate",
            SimpleNameSyntax name => name.Identifier.ValueText is "PressSpace" or "Focus" or "Activate",
            _ => false
        }
        || node is SimpleNameSyntax simple && simple.Identifier.ValueText is
            "IsKeyboardFocused" or "FocusedElement" or "FocusManager" or "InputManager"
        || node is MemberAccessExpressionSyntax memberAccess &&
            (memberAccess.Expression.ToString().Split('.').Last() == "Mouse"
             || memberAccess.Expression.ToString().Split('.').Last() == "Keyboard"
                && memberAccess.Name.Identifier.ValueText is "Focus" or "PrimaryDevice"));

    [Theory]
    [InlineData("PressSpace(button)")]
    [InlineData("button.Focus()")]
    [InlineData("Keyboard.Focus(button)")]
    [InlineData("button.IsKeyboardFocused")]
    [InlineData("Mouse.Captured")]
    [InlineData("Keyboard.FocusedElement")]
    [InlineData("FocusManager.GetFocusedElement(window)")]
    [InlineData("InputManager.Current")]
    [InlineData("Keyboard.PrimaryDevice")]
    [InlineData("window.Activate()")]
    public void One_line_tests_detect_each_input_API(string expression)
    {
        string source = "namespace Fixture; class Tests { [Fact] public void Input() { var value = " + expression + "; } }";
        Assert.Equal(new[] { "Fixture.Tests.Input" }, FindInputTests(new[] { source }));
    }

    [Fact]
    public void Helpers_are_followed_across_files_partial_classes_and_multiple_calls()
    {
        string[] sources = {
            "namespace Fixture; partial class Tests { [Theory] public void Input() => Helpers.First(); [Fact] public void Pure() { var text = \"Mouse.Captured FocusedElement Activate()\"; } }",
            "namespace Fixture { partial class Tests { [Fact] public void Partial() => Helpers.First(); } }",
            "namespace Fixture; class Helpers { public static void First() => Second(); private static void Second() => Keyboard.Focus(null); }"
        };
        Assert.Equal(new[] { "Fixture.Tests.Input", "Fixture.Tests.Partial" }, FindInputTests(sources));
    }

    [Fact]
    public void Helper_symbols_distinguish_pure_names_and_follow_constructors_and_method_groups()
    {
        string source = """
            using System;
            namespace Fixture;
            class InputHelpers {
                public InputHelpers() => Keyboard.Focus(null);
                public static Action Handler => Click;
                public static void Click() => Mouse.Capture(null);
            }
            class PureHelpers { public static void Click() { } }
            class Tests {
                [Xunit.FactAttribute] public void Constructor() { var helper = new InputHelpers(); }
                [Fact] public void MethodGroup() { Action action = InputHelpers.Click; action(); }
                [Fact] public void Property() => InputHelpers.Handler();
                [Fact] public void Pure() => PureHelpers.Click();
            }
            """;
        Assert.Equal(new[] { "Fixture.Tests.Constructor", "Fixture.Tests.MethodGroup", "Fixture.Tests.Property" },
            FindInputTests(new[] { source }));
    }

    [Fact]
    public void Project_sources_include_partial_files_helpers_and_linked_tests()
    {
        string root = Repository.FindRoot(AppContext.BaseDirectory);
        string directory = Path.Combine(root, "tests/Studio/TiaOpenness.Gui.Tests");
        string[] sources = ProjectSources(directory).ToArray();
        Assert.Contains(File.ReadAllText(Path.Combine(directory, "UnifiedDesktopTests.Menu.cs")), sources);
        Assert.Contains(File.ReadAllText(Path.Combine(directory, "WpfContext.cs")), sources);
        Assert.Contains(File.ReadAllText(Path.Combine(root, "tests/KeptCode/TiaMcp.KeptCode.Tests/DataLocationsTests.cs")), sources);
    }
}
