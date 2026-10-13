using System.Text.Json;
using TiaMcp.ReleaseTool;
using Xunit;

namespace TiaMcp.ReleaseTool.Tests;

[Trait("Category", "StaticChecks")]
public sealed class AnalyzerWarningTests
{
    private const string Source = "internal class C { void M() { throw new System.Exception(\"fixture\"); } }\n";
    private static AnalyzerWarning Warning(StaticTree tree, string source = Source, string rule = "CA2201")
    {
        var start = source.IndexOf("new System.Exception", StringComparison.Ordinal);
        var line = source[..start].Count(c => c == '\n') + 1;
        var column = start - source.LastIndexOf('\n', start) ;
        return AnalyzerWarnings.Describe(tree.Root, "src/C.cs", line, column, line, column + "new System.Exception".Length, rule, "Fixture warning", source);
    }

    private static AnalyzerWarningCollection Collection(params AnalyzerWarning[] entries) => new(entries, new HashSet<string> { "src/C.cs" });

    [Fact]
    public void ReportDirectoriesMayLeaveTheCheckoutButMustBeLocal()
    {
        // check-all -Clean builds in a detached checkout and writes its reports into the caller's output directory.
        using var tree = new StaticTree(); using var outside = new StaticTree();
        var reports = Path.Combine(outside.Root, "check-all", "build", "analyzer-warnings");
        Assert.Equal(Path.GetFullPath(reports), AnalyzerWarnings.LocalDirectory(reports));
        AnalyzerWarnings.RememberDirectory(tree.Root, reports);
        Assert.Equal(Path.GetFullPath(reports), AnalyzerWarnings.DefaultDirectory(tree.Root));
        var inside = Path.Combine(tree.Root, "bin-build", "analyzer-warnings", "observed");
        AnalyzerWarnings.RememberDirectory(tree.Root, inside);
        Assert.Equal("bin-build/analyzer-warnings/observed\n", File.ReadAllText(Path.Combine(tree.Root, "bin-build/analyzer-warnings/last-directory.txt")));
        // UNC network paths exist only on Windows; elsewhere the same text is an ordinary relative name.
        if (OperatingSystem.IsWindows()) Assert.Throws<ReleaseException>(() => AnalyzerWarnings.LocalDirectory(@"\\server\share\reports"));
        Assert.Throws<ReleaseException>(() => AnalyzerWarnings.LocalDirectory("https://example.invalid/reports"));
        // Source paths and reviewed data stay inside the repository.
        Assert.Throws<ReleaseException>(() => AnalyzerWarnings.RepositoryPath(tree.Root, reports));
    }

    [Fact]
    public void NewWarningFailsAndDisappearanceRequiresBaselineUpdate()
    {
        using var tree = new StaticTree(); tree.Write("src/C.cs", Source);
        var entry = Warning(tree) with { Reason = "test-fixture" };
        Assert.Empty(AnalyzerWarnings.Compare(tree.Root, Collection(entry), [entry], true));
        Assert.Contains(AnalyzerWarnings.Compare(tree.Root, Collection(entry), [], true), issue => issue.StartsWith("NEW "));
        Assert.Contains(AnalyzerWarnings.Compare(tree.Root, Collection(), [entry], true), issue => issue.StartsWith("STALE "));
    }

    [Fact]
    public void MovingLinesAndStatementsInTheSameMemberPreservesIdentity()
    {
        using var tree = new StaticTree(); tree.Write("src/C.cs", Source);
        var entry = Warning(tree) with { Reason = "test-fixture" };
        var moved = Warning(tree, Source.Replace("throw ", "\n        int x = 0;\n        throw ", StringComparison.Ordinal));
        Assert.Equal(entry.Fingerprint, moved.Fingerprint);
        Assert.NotEqual(entry.Line, moved.Line);
        Assert.Empty(AnalyzerWarnings.Compare(tree.Root, Collection(moved), [entry], true));
        var otherMember = Warning(tree, Source.Replace("M()", "N()", StringComparison.Ordinal));
        Assert.NotEqual(entry.Fingerprint, otherMember.Fingerprint);
        Assert.Contains(AnalyzerWarnings.Compare(tree.Root, Collection(otherMember), [entry], true), issue => issue.StartsWith("NEW "));
    }

    [Fact]
    public void DuplicateWarningsCannotConsumeTheSameReviewedAllowance()
    {
        using var tree = new StaticTree(); tree.Write("src/C.cs", Source);
        var entry = Warning(tree) with { Reason = "test-fixture" };
        Assert.Single(AnalyzerWarnings.Compare(tree.Root, Collection(entry, entry with { Line = 2 }), [entry], true));
    }

    [Fact]
    public void BuildCollectionsKeepDistinctMessagesAndMergeIdenticalLocations()
    {
        using var tree = new StaticTree(); var entry = Warning(tree) with { Contexts = ["release"] };
        var same = entry with { Contexts = ["debug"] };
        var other = Warning(tree, rule: "CA1416") with { Contexts = ["release"] };
        var combined = AnalyzerWarnings.Combine([Collection(entry), Collection(same, other)]);
        Assert.Equal(2, combined.Entries.Length);
        Assert.Equal(new[] { "debug", "release" }, combined.Entries.Single(e => e.Rule == entry.Rule).Contexts);
    }

    [Fact]
    public void CompleteVariantBuildDoesNotClaimAnOrdinaryCompilerMessageDisappeared()
    {
        using var tree = new StaticTree(); tree.Write("src/C.cs", Source);
        var entry = Warning(tree) with { Reason = "test-fixture", Contexts = ["ordinary|C"] };
        var variants = new AnalyzerWarningCollection([], new HashSet<string> { "src/C.cs" },
            new Dictionary<string, HashSet<string>> { ["src/C.cs"] = ["variants|C"] });
        Assert.Empty(AnalyzerWarnings.Compare(tree.Root, variants, [entry], true));
        var ordinary = variants with { Coverage = new() { ["src/C.cs"] = ["ordinary|C"] } };
        Assert.Contains(AnalyzerWarnings.Compare(tree.Root, ordinary, [entry], true), issue => issue.StartsWith("STALE "));
    }

    [Theory]
    [InlineData("../outside.cs")]
    [InlineData("src/../../outside.cs")]
    [InlineData("https://example.invalid/source.cs")]
    public void ExternalPathsAreRefused(string path)
    {
        using var tree = new StaticTree();
        Assert.Throws<ReleaseException>(() => AnalyzerWarnings.RepositoryPath(tree.Root, path));
    }

    [Fact]
    public void PartialBuildChecksCoveredFilesAndDeletionIsAlwaysStale()
    {
        using var tree = new StaticTree(); var path = tree.Write("src/C.cs", Source);
        var entry = Warning(tree) with { Reason = "pending-port", Retirement = "B9" };
        var partial = new AnalyzerWarningCollection([], []);
        Assert.Empty(AnalyzerWarnings.Compare(tree.Root, partial, [entry], false));
        Assert.NotEmpty(AnalyzerWarnings.Compare(tree.Root, partial, [entry], true));
        File.Delete(path);
        Assert.Contains(AnalyzerWarnings.Compare(tree.Root, partial, [entry], false), issue => issue.StartsWith("STALE "));
    }

    private static string Reports(StaticTree tree, bool warning = true, string suffix = "a")
    {
        var source = tree.Write("src/C.cs", Source);
        var entry = Warning(tree);
        var result = new { ruleId = entry.Rule, level = "warning", message = new { text = entry.Message },
            locations = new[] { new { physicalLocation = new { artifactLocation = new { uri = new Uri(source).AbsoluteUri },
                region = new { startLine = entry.Line, startColumn = entry.Column, endLine = entry.EndLine, endColumn = entry.EndColumn } } } } };
        tree.Write("reports/" + suffix + ".sarif", JsonSerializer.Serialize(new { version = "2.1.0", runs = new[] { new { results = warning ? new[] { result } : [] } } }));
        tree.Write("reports/" + suffix + ".inputs", "context|" + Path.Combine(tree.Root, "C.csproj") + "|net10.0|NET\n" + source + "|" + ReleaseRecords.HashFile(source) + "\n");
        return Path.Combine(tree.Root, "reports");
    }

    [Fact]
    public void FrameworkAndVariantReportsDeduplicateButChangedInputsFail()
    {
        using var tree = new StaticTree(); var reports = Reports(tree); Reports(tree, suffix: "b");
        Assert.Single(AnalyzerWarnings.Collect(tree.Root, reports).Entries);
        tree.Write("src/C.cs", "\n" + Source);
        Assert.Throws<ReleaseException>(() => AnalyzerWarnings.Collect(tree.Root, reports));
    }

    [Theory]
    [InlineData("accepted", 0)]
    [InlineData("rejected", 1)]
    public void OnlyAcceptedSuppressionsAreAbsentFromCompilerWarnings(string status, int expected)
    {
        using var tree = new StaticTree(); var reports = Reports(tree);
        var path = Path.Combine(reports, "a.sarif");
        var data = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
        data["runs"]![0]!["results"]![0]!["suppressions"] = new System.Text.Json.Nodes.JsonArray(
            new System.Text.Json.Nodes.JsonObject { ["kind"] = "inSource", ["status"] = status });
        File.WriteAllText(path, data.ToJsonString());
        Assert.Equal(expected, AnalyzerWarnings.Collect(tree.Root, reports).Entries.Length);
    }

    [Fact]
    public void ReportsOutsideTheCheckoutAreCollectedButTheirSourcesStayInside()
    {
        // check-all -Clean compiles a detached checkout and collects the reports from the caller's output directory.
        using var outside = new StaticTree(); using var tree = new StaticTree();
        var external = Path.Combine(outside.Root, "check-all", "build", "analyzer-warnings");
        Directory.CreateDirectory(Path.GetDirectoryName(external)!);
        Directory.Move(Reports(tree), external);
        Assert.Single(AnalyzerWarnings.Collect(tree.Root, external).Entries);
        var receipt = Path.Combine(external, "a.inputs");
        var foreign = outside.Write("src/C.cs", Source);
        File.WriteAllText(receipt, File.ReadAllText(receipt).Replace(Path.Combine(tree.Root, "src/C.cs"), foreign));
        Assert.Throws<ReleaseException>(() => AnalyzerWarnings.Collect(tree.Root, external));
        Assert.Throws<ReleaseException>(() => AnalyzerWarnings.ReportFile(external, Path.Combine(outside.Root, "a.sarif")));
    }

    [Fact]
    public void TestSdkAuxiliaryInputCannotAuthorizeAnExternalWarning()
    {
        using var outside = new StaticTree(); using var tree = new StaticTree();
        var reports = Reports(tree);
        var template = outside.Write("microsoft.net.test.sdk/1/build/net10.0/Microsoft.NET.Test.Sdk.Program.cs",
            "// <auto-generated> SDK template </auto-generated>\n");
        File.AppendAllText(Path.Combine(reports, "a.inputs"), template + "|" + ReleaseRecords.HashFile(template) + "\n");
        Assert.Single(AnalyzerWarnings.Collect(tree.Root, reports).Entries);
        var path = Path.Combine(reports, "a.sarif");
        var data = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
        data["runs"]![0]!["results"]![0]!["locations"]![0]!["physicalLocation"]!["artifactLocation"]!["uri"] = new Uri(template).AbsoluteUri;
        File.WriteAllText(path, data.ToJsonString());
        Assert.Throws<ReleaseException>(() => AnalyzerWarnings.Collect(tree.Root, reports));
    }

    [Theory]
    [InlineData("C:\\Users\\fixture\\AppData\\Local\\Temp\\dotnet\\runfile")]
    [InlineData("/home/fixture/.local/share/dotnet/runfile")]
    public void RunfileInputsRequireTheExactSdkLayoutAndProgram(string directory)
    {
        var prefix = directory + "/Probe-" + new string('a', 64) + "/obj/release/";
        Assert.Equal(directory.Replace('\\', '/'), AnalyzerWarnings.RunfileInputDirectory(prefix + "Probe.cs.GlobalUsings.g.cs"));
        Assert.Null(AnalyzerWarnings.RunfileInputDirectory("https://example.invalid/dotnet/runfile/Probe.cs"));
        Assert.Null(AnalyzerWarnings.RunfileInputDirectory(directory.Replace("dotnet", "../dotnet", StringComparison.Ordinal) + "/Probe.cs"));
        foreach (var file in new[] { "Probe.cs.GlobalUsings.g.cs", "Probe.cs.AssemblyInfo.cs", ".NETCoreApp,Version=v10.0.AssemblyAttributes.cs" })
            Assert.True(AnalyzerWarnings.RunfileInput(prefix + file, "scripts/Probe.cs", "net10.0", directory));
        foreach (var path in new[] { prefix + "source.cs", prefix + "../Probe.cs.GlobalUsings.g.cs",
            prefix.Replace("/obj/release/", "/bin/release/") + "Probe.cs.GlobalUsings.g.cs",
            prefix.Replace(new string('a', 64), "short") + "Probe.cs.GlobalUsings.g.cs" })
            Assert.False(AnalyzerWarnings.RunfileInput(path, "scripts/Probe.cs", "net10.0", directory));
        Assert.False(AnalyzerWarnings.RunfileInput(prefix + "Probe.cs.GlobalUsings.g.cs", "scripts/Other.cs", "net10.0", directory));
        Assert.False(AnalyzerWarnings.RunfileInput(prefix + ".NETCoreApp,Version=v9.0.AssemblyAttributes.cs", "Probe.cs", "net10.0", directory));
        Assert.False(AnalyzerWarnings.RunfileInput(prefix + "Probe.cs.GlobalUsings.g.cs", "Probe.cs", "net10.0", directory + "/other"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RunfileAuxiliaryInputCannotAuthorizeAnExternalWarning(bool windowsUri)
    {
        using var tree = new StaticTree(); var reports = Reports(tree);
        var name = "AnalyzerFixture" + Guid.NewGuid().ToString("N");
        // A compiler can use a different TEMP from preflight's checking process.
        var temporary = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "analyzer-sdk-" + Guid.NewGuid().ToString("N"))).FullName;
        var directory = Path.Combine(temporary, "dotnet/runfile", name + "-" + new string('a', 64));
        var template = Path.Combine(directory, "obj/release/" + name + ".cs.GlobalUsings.g.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(template)!);
        try
        {
            File.WriteAllText(template, "// <auto-generated/>\nglobal using System;\n");
            var receipt = Path.Combine(reports, "a.inputs");
            File.WriteAllText(receipt, File.ReadAllText(receipt).Replace("C.csproj", name + ".cs.csproj", StringComparison.Ordinal) +
                template + "|" + ReleaseRecords.HashFile(template) + "\n");
            var collected = AnalyzerWarnings.Collect(tree.Root, reports);
            Assert.Single(collected.Entries);
            Assert.DoesNotContain(collected.Sources, path => path.Contains("GlobalUsings", StringComparison.Ordinal));
            var path = Path.Combine(reports, "a.sarif");
            var data = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
            var foreign = (windowsUri ? "file:///C:/Users/fixture/AppData/Local/Temp" : "file:///home/fixture/.local/share") +
                "/dotnet/runfile/" + name + "-" + new string('a', 64) + "/obj/release/" + name + ".cs.GlobalUsings.g.cs";
            data["runs"]![0]!["results"]![0]!["locations"]![0]!["physicalLocation"]!["artifactLocation"]!["uri"] = foreign;
            File.WriteAllText(path, data.ToJsonString());
            Assert.Throws<ReleaseException>(() => AnalyzerWarnings.Collect(tree.Root, reports));
            data["runs"]![0]!["results"]![0]!["locations"]![0]!["physicalLocation"]!["artifactLocation"]!["uri"] = new Uri(template).AbsoluteUri;
            File.WriteAllText(path, data.ToJsonString());
            Assert.Throws<ReleaseException>(() => AnalyzerWarnings.Collect(tree.Root, reports));
            Reports(tree, warning: false);
            File.WriteAllText(receipt, File.ReadAllText(receipt).Replace("C.csproj", name + ".cs.csproj", StringComparison.Ordinal) + template + "|" + new string('0', 64) + "\n");
            Assert.Throws<ReleaseException>(() => AnalyzerWarnings.Collect(tree.Root, reports));
        }
        finally { Directory.Delete(temporary, recursive: true); }
    }

    [Fact]
    public void FileProgramDiscoveryIncludesDirectivesAndUnprojectedStatements()
    {
        using var tree = new StaticTree();
        tree.Write("scripts/a.cs", "Console.WriteLine(1);\n");
        tree.Write("scripts/project/p.csproj", "<Project/>\n");
        tree.Write("scripts/project/p.cs", "Console.WriteLine(1);\n");
        tree.Write("scripts/project/app.cs", "#:property PublishAot=false\nConsole.WriteLine(1);\n");
        tree.Write("tests/test.cs", "#!/usr/bin/env dotnet\nConsole.WriteLine(1);\n");
        tree.Write("scripts/obj/generated.cs", "Console.WriteLine(1);\n");
        tree.Write("reference/siemens-code-snippets/sample.cs", "#:property PublishAot=false\nConsole.WriteLine(1);\n");
        Assert.Equal(new[] { "scripts/a.cs", "scripts/project/app.cs", "tests/test.cs" }, FileBasedPrograms.Discover(tree.Root));
    }

    [Fact]
    public void OrdinaryScriptRunsLeaveCollectedReportsIdenticalAndPlantedWarningsFail()
    {
        using var tree = new StaticTree();
        tree.Copy(".editorconfig"); tree.Copy("src/Shared/CodeAnalysis.props"); tree.Copy("Directory.Build.props");
        var config = tree.Write("offline.config", "<configuration><packageSources><clear /></packageSources></configuration>");
        var source = "#!/usr/bin/env dotnet\n#:property PublishAot=false\nConsole.WriteLine(1);\n";
        var program = tree.Write("scripts/fixture.cs", source);
        var reports = Path.Combine(tree.Root, "reports");
        var environment = new Dictionary<string, string?> { ["TIA_MCP_ANALYZER_REPORT_DIRECTORY"] = reports,
            ["RestoreConfigFile"] = config, ["NuGetAudit"] = "false", ["UseSharedCompilation"] = "false" };
        void Run(params string[] arguments) => ProcessRunner.RequireSuccess(ProcessRunner.Run("dotnet", arguments, tree.Root, environment), "Analyzer compilation fixture");
        void Build() => Run("build", program, "-c", "Release", "-p:TiaMcpCollectFileBasedPrograms=true");
        Build();
        var before = Directory.GetFiles(reports).ToDictionary(path => Path.GetFileName(path)!, ReleaseRecords.HashFile);
        Run("run", program, "--no-restore", "--", "fixture");
        var after = Directory.GetFiles(reports).ToDictionary(path => Path.GetFileName(path)!, ReleaseRecords.HashFile);
        Assert.Equal(before.OrderBy(pair => pair.Key), after.OrderBy(pair => pair.Key));
        Assert.Empty(AnalyzerWarnings.Collect(tree.Root, reports).Entries);
        tree.Write("scripts/fixture.cs", source + "class PlantedScriptWarning { }\n");
        Build();
        var scriptWarnings = AnalyzerWarnings.Collect(tree.Root, reports);
        Assert.Contains(AnalyzerWarnings.Compare(tree.Root, scriptWarnings, [], false), issue => issue.StartsWith("NEW IDE0040 scripts/fixture.cs", StringComparison.Ordinal));
        tree.Write("project/project.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        tree.Write("project/C.cs", "class PlantedProjectWarning { }\n");
        Run("build", Path.Combine(tree.Root, "project/project.csproj"), "-c", "Release");
        Assert.Contains(AnalyzerWarnings.Compare(tree.Root, AnalyzerWarnings.Collect(tree.Root, reports), [], false),
            issue => issue.StartsWith("NEW IDE0040 project/C.cs", StringComparison.Ordinal));
    }

    [Fact]
    public void UpdateOnlyShrinksAndRefusesGrowthWithoutWriting()
    {
        using var tree = new StaticTree(); var reports = Reports(tree);
        var entry = AnalyzerWarnings.Collect(tree.Root, reports).Entries.Single() with { Reason = "test-fixture" };
        var json = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
        var review = tree.Write("review.json", JsonSerializer.Serialize(new AnalyzerWarningBaseline(1, [entry]), json));
        var baseline = Path.Combine(tree.Root, "baseline.json");
        string[] arguments = ["-Root", tree.Root, "-DiagnosticsDirectory", reports, "-Baseline", baseline];
        int Run(params string[] args) => ReleaseCommands.RunStaticCaptured("check-analyzer-warnings", [.. arguments, .. args]).ExitCode;
        Assert.Equal(0, Run("-Initialize", "-ReviewData", review));
        Assert.Equal(0, Run("-Check", "-Complete"));
        Assert.Equal(64, Run("-Initialize", "-ReviewData", review));
        Reports(tree, warning: false);
        Assert.Equal(1, Run("-Check", "-Complete"));
        Assert.Equal(0, Run("-UpdateBaseline", "-Complete"));
        var bytes = File.ReadAllBytes(baseline);
        Reports(tree);
        Assert.Equal(1, Run("-UpdateBaseline", "-Complete"));
        Assert.Equal(bytes, File.ReadAllBytes(baseline));
        Assert.Equal(64, Run("-Check", "-UpdateBaseline", "-Complete"));
    }

    [Theory]
    [InlineData("-Check")]
    [InlineData("-UpdateBaseline")]
    [InlineData("-Initialize")]
    public void CaptureCannotBypassAGate(string mode)
    {
        using var tree = new StaticTree(); var reports = Reports(tree);
        var capture = Path.Combine(tree.Root, "capture.json");
        var result = ReleaseCommands.RunStaticCaptured("check-analyzer-warnings",
            ["-Root", tree.Root, "-DiagnosticsDirectory", reports, "-Capture", capture, mode]);
        Assert.Equal(64, result.ExitCode);
        Assert.False(File.Exists(capture));
    }

    [Fact]
    public void CaptureCannotOverwriteReviewedData()
    {
        using var tree = new StaticTree(); var reports = Reports(tree);
        var path = tree.Write("review.json", "reviewed bytes");
        var result = ReleaseCommands.RunStaticCaptured("check-analyzer-warnings",
            ["-Root", tree.Root, "-DiagnosticsDirectory", reports, "-Capture", path]);
        Assert.Equal(64, result.ExitCode);
        Assert.Equal("reviewed bytes", File.ReadAllText(path));
    }
}
