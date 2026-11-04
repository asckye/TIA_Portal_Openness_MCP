using Mono.Cecil;
using Mono.Cecil.Cil;
using TiaMcp.ReleaseTool;
using TiaMcp.Tests.Shared;
using Xunit;

namespace TiaMcp.ReleaseTool.Tests;

[Trait("Category", "StaticChecks")]
public sealed class TidyToolTests
{
    [Theory]
    [InlineData("void M() { FirstLongStatement(); SecondLongStatement(); }")]
    [InlineData("if (LongConditionName()) ExecuteLongStatement();")]
    [InlineData("for (int index = 0; index < 100; index++) ExecuteLongStatement();")]
    [InlineData("foreach (var item in collection) ExecuteLongStatement(item);")]
    [InlineData("while (LongConditionName()) ExecuteLongStatement();")]
    [InlineData("using (var stream = OpenStream()) ExecuteLongStatement();")]
    [InlineData("lock (LongLockName) ExecuteLongStatement();")]
    [InlineData("else ExecuteLongStatementWithVeryLongName();")]
    [InlineData("Call(FirstLongArgumentName, SecondLongArgumentName, ThirdLongArgumentName);")]
    [InlineData("bool value = FirstLongConditionName && SecondLongConditionName || ThirdLongConditionName;")]
    [InlineData("var value = FirstLongValueName ?? SecondLongValueName;")]
    [InlineData("var value = LongConditionName ? FirstLongValueName : SecondLongValueName;")]
    [InlineData("var value = FirstLongValueName + SecondLongValueName;")]
    [InlineData("object M() => CallVeryLongMethodWithLongName();")]
    [InlineData("value = CallVeryLongMethodWithLongName();")]
    [InlineData("LongRootName.FirstLongMethodName().SecondLongMethodName();")]
    [InlineData("void M(int FirstLongParameterName, int SecondLongParameterName) {}")]
    [InlineData("var values = new[] { FirstLongValueName, SecondLongValueName };")]
    public void BreaksPreserveTokensAndAreIdempotent(string source)
    {
        string formatted = WrapLongLines.Format(source, "fixture.cs", 35);
        Assert.Contains("\n", formatted);
        Assert.True(WrapLongLines.Equivalent(source, formatted));
        Assert.Equal(formatted, WrapLongLines.Format(formatted, "fixture.cs", 35));
    }

    [Fact]
    public void SplitsPlainAsciiLiteralWithoutChangingEscapes()
    {
        const string source = "return \"alpha beta gamma delta epsilon zeta eta theta iota kappa\";";
        var result = WrapLongLines.Format(source, "fixture.cs", 35);
        Assert.Contains("\" +\n", result);
        Assert.True(WrapLongLines.Equivalent(source, result));
        Assert.Equal(result, WrapLongLines.Format(result, "fixture.cs", 35));
    }

    [Theory]
    [InlineData("var x = \"alpha beta gamma delta epsilon zeta eta theta\".Length;")]
    [InlineData("case \"alpha beta gamma delta epsilon zeta eta theta\":")]
    [InlineData("\"alpha beta gamma delta epsilon zeta eta theta\" => result,")]
    [InlineData("return $\"alpha beta gamma delta epsilon zeta eta theta\";")]
    [InlineData("return @\"alpha beta gamma delta epsilon zeta eta theta\";")]
    [InlineData("return \"中文 alpha beta gamma delta epsilon zeta eta theta\";")]
    [InlineData("return \"alpha beta gamma delta epsilon zeta eta theta\"u8;")]
    [InlineData("return \"\\u4e2d alpha beta gamma delta epsilon zeta eta theta\";")]
    public void RefusesLiteralSplits(string source)
    {
        Assert.DoesNotContain("\" +\n", WrapLongLines.Format(source, "fixture.cs", 35));
    }

    [Theory]
    [InlineData("var text = \"\"\"\nalpha beta gamma delta epsilon zeta eta theta iota\n\"\"\";")]
    [InlineData("/* alpha beta gamma delta epsilon zeta eta theta\nlong comment alpha beta gamma delta epsilon zeta */")]
    [InlineData("// alpha beta gamma delta epsilon zeta eta theta")]
    public void PreservesMultilineTokensAndComments(string source) =>
        Assert.Equal(source, WrapLongLines.Format(source, "fixture.cs", 35));

    [Fact]
    public void WrapsEveryConditionalBranchButNeverDirectives()
    {
        const string directive = "#if A_VERY_LONG_PREPROCESSOR_SYMBOL_NAME";
        const string statement = "Call(FirstLongArgumentName, SecondLongArgumentName);";
        string source = directive + "\n" + statement + "\n#else\n" + statement + "\n#endif";
        var result = WrapLongLines.Format(source, "fixture.cs", 35);
        Assert.Contains(directive + "\n", result);
        Assert.DoesNotContain(statement, result);
        Assert.True(WrapLongLines.Equivalent(source, result));
    }

    [Fact]
    public void TrailingCommentStaysWithLastToken()
    {
        const string source = "Call(FirstLongArgumentName, SecondLongArgumentName); // trailing";
        string result = WrapLongLines.Format(source, "fixture.cs", 35);
        Assert.EndsWith("SecondLongArgumentName); // trailing", result);
    }

    [Theory]
    [InlineData("src/FoundationHost/FoundationTools.cs")]
    [InlineData("src/FoundationHost/OfflineProjectTools.cs")]
    [InlineData("src/Logic/ModelContextProtocol/ToolMetadataCore.cs")]
    [InlineData("src/Logic/ModelContextProtocol/ToolTaxonomy.cs")]
    [InlineData("src/Adapters.Contracts/PortedFamilies.cs")]
    [InlineData("src/Shared/BundleLayout.cs")]
    public void ExcludedFilesStayUntouched(string path)
    {
        const string source = "Call(FirstLongArgumentName, SecondLongArgumentName);";
        Assert.Equal(source, WrapLongLines.Format(source, path, 35));
        Assert.Equal("excluded-file", WrapLongLines.Reason(source, path));
    }

    [Fact]
    public void NoLiteralSplitFilesKeepExactLiteralTokens()
    {
        const string source = "return \"alpha beta gamma delta epsilon zeta eta theta\";";
        foreach (var file in WrapLongLines.NoLiteralSplitFiles)
            Assert.DoesNotContain("\" +\n", WrapLongLines.Format(source, file, 35));
    }

    [Fact]
    public void KeepsSwallowMarkerOnItsOriginalCatchLine()
    {
        const string source = "try { CallWithLongName(); } catch { /* swallow(optional): Keep the existing fallback behavior. */ }";
        Assert.Equal(source, WrapLongLines.Format(source, "fixture.cs", 35));
        Assert.Equal("pinned", WrapLongLines.Reason(source, "fixture.cs"));
    }

    [Fact]
    public void UsesAdjacentLineTokensWhenSplittingLiteral()
    {
        const string source = "Call(\n    \"alpha beta gamma delta epsilon zeta eta theta\"\n);";
        var result = WrapLongLines.Format(source, "fixture.cs", 35);
        Assert.Contains("\" +\n", result);
        Assert.True(WrapLongLines.Equivalent(source, result));
        Assert.Equal(result, WrapLongLines.Format(result, "fixture.cs", 35));
    }

    [Fact]
    public void ShortLeadingBreaksStillMakeProgress()
    {
        const string source = "    { Call(FirstLongArgumentName, SecondLongArgumentName); Call(FirstLongArgumentName, SecondLongArgumentName); }";
        var result = WrapLongLines.Format(source, "fixture.cs", 40);
        Assert.All(result.Split('\n'), line => Assert.True(line.Length <= 40, line));
        Assert.Equal(result, WrapLongLines.Format(result, "fixture.cs", 40));
    }

    [Fact]
    public void KeepsManagedExecutableEvenWithSiblingDll()
    {
        using var tree = new TidyTree();
        using var assembly = AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition("Fixture", new Version(1, 0)), "Fixture", ModuleKind.Console);
        string exe = Path.Combine(tree.Root, "Fixture.exe");
        assembly.Write(exe);
        File.Copy(exe, Path.ChangeExtension(exe, ".dll"));
        var options = Options.Parse(["-Output", "out", "-Assembly", exe, "-Assembly", Path.ChangeExtension(exe, ".dll")], new HashSet<string>());
        Assert.Equal(0, IlDump.Run(tree.Root, options));
        Assert.Equal(2, Directory.GetFiles(Path.Combine(tree.Root, "out")).Length);
    }

    [Theory]
    [InlineData("<>c__DisplayClass12_3", "<>c__DisplayClass#_3")]
    [InlineData("<M>b__12_3", "<M>b__#_3")]
    [InlineData("<M>d__12", "<M>d__#")]
    [InlineData("<<M>b__12_3>d__45", "<<M>b__#_3>d__#")]
    [InlineData("<>9__12_3", "<>9__#_3")]
    [InlineData("<M>g__Local|12_3", "<M>g__Local|#_3")]
    [InlineData("Call_0123456789abcdef0123", "Call_#")]
    [InlineData("__TiaMcpNativeCall_0123456789abcdef0123", "__TiaMcpNativeCall_#")]
    [InlineData("Owner/<>O::<12>__Method", "Owner/<>O::<#>__Method")]
    [InlineData("<>f__AnonymousType12", "<>f__AnonymousType#")]
    public void NormalizesOnlySpecifiedGeneratedNames(string input, string expected) => Assert.Equal(expected, IlDump.Normalize(input));

    [Fact]
    public void NormalizesWeaverSiteStringAndCoverage()
    {
        Assert.Equal("# <M>b__#_3@IL_0010", IlDump.NormalizeSiteString("0123456789abcdef0123 <M>b__12_3@IL_0010"));
        Assert.Equal("0123456789abcdef0123 ordinary", IlDump.NormalizeSiteString("0123456789abcdef0123 ordinary"));
        var first = System.Text.Encoding.UTF8.GetBytes("{\"sites\":[{\"caller\":\"<M>b__12_3\",\"member\":\"B\",\"opcode\":\"call\"},{\"caller\":\"A\",\"member\":\"C\",\"opcode\":\"newobj\"}]}");
        var second = System.Text.Encoding.UTF8.GetBytes("{\"sites\":[{\"caller\":\"A\",\"member\":\"C\",\"opcode\":\"newobj\"},{\"caller\":\"<M>b__20_3\",\"member\":\"B\",\"opcode\":\"call\"}]}");
        Assert.Equal(IlDump.NormalizeCoverage(first), IlDump.NormalizeCoverage(second));
    }

    [Fact]
    public void FailedProofAndCheckDoNotWrite()
    {
        using var tree = new TidyTree();
        string path = Path.Combine(tree.Root, "test.cs");
        const string source = "Call(FirstLongArgumentName, SecondLongArgumentName);";
        File.WriteAllText(path, source);
        Assert.Throws<ReleaseException>(() => WrapLongLines.Rewrite(path, _ => "Different();", false));
        Assert.Equal(source, File.ReadAllText(path));
        Assert.True(WrapLongLines.Rewrite(path, s => WrapLongLines.Format(s, "test.cs", 35), true));
        Assert.Equal(source, File.ReadAllText(path));
        Assert.False(WrapLongLines.Equivalent("// original", "// altered"));
    }

    [Fact]
    public void SourceFamilyNormalizesAndJoinsOnlySiblingPartsInOrder()
    {
        using var tree = new TidyTree();
        string path = Path.Combine(tree.Root, "Foo.cs");
        File.WriteAllText(path, "base\r\n", new System.Text.UTF8Encoding(true));
        File.WriteAllText(Path.Combine(tree.Root, "Foo.Z.cs"), "z\r");
        File.WriteAllText(Path.Combine(tree.Root, "Foo.A.cs"), "a\r\n");
        File.WriteAllText(Path.Combine(tree.Root, "FooBar.cs"), "other");
        Assert.Equal("base\n\na\n\nz\n", SourceFamily.Read(path));
        Assert.Throws<FileNotFoundException>(() => SourceFamily.Read(Path.Combine(tree.Root, "Missing.cs")));
    }

    [Fact]
    public void IlDumpSortsCollapsedNamesByEntireBodyAndRetainsBehavior()
    {
        using var tree = new TidyTree();
        string Build(string name, bool reverse, int constant)
        {
            string path = Path.Combine(tree.Root, name + ".dll");
            using var assembly = AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition("Fixture", new Version(1, 0)), "Fixture", ModuleKind.Dll);
            foreach (int value in reverse ? new[] { 2, 1 } : new[] { 1, 2 })
            {
                var type = new TypeDefinition("", "<>c__DisplayClass" + (reverse ? value + 10 : value) + "_0", TypeAttributes.Class);
                assembly.MainModule.Types.Add(type);
                var method = new MethodDefinition("M", MethodAttributes.Public | MethodAttributes.Static, assembly.MainModule.TypeSystem.Int32);
                type.Methods.Add(method);
                method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldc_I4, value + constant));
                method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
            }
            assembly.Write(path);
            return path;
        }
        string first = IlDump.Render(Build("a", false, 0));
        Assert.Equal(first, IlDump.Render(Build("b", true, 0)));
        Assert.NotEqual(first, IlDump.Render(Build("c", true, 1)));
    }

    private sealed class TidyTree : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "tidy-tests-" + Guid.NewGuid().ToString("N"));
        internal TidyTree() => Directory.CreateDirectory(Root);
        public void Dispose() => Directory.Delete(Root, true);
    }
}
