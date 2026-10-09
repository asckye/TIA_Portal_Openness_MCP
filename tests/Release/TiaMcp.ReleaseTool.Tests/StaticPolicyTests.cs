using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using TiaMcp.BuildCommon;
using TiaMcp.ReleaseTool;
using Xunit;

namespace TiaMcp.ReleaseTool.Tests;

internal sealed class StaticTree : IDisposable
{
    internal string Root { get; } = Directory.CreateDirectory(Path.Combine(StaticCheckVectorTests.Root, "bin-build", "static-check-" + Guid.NewGuid().ToString("N"))).FullName;
    internal string Write(string name, string source)
    {
        var path = Path.Combine(Root, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, source);
        return path;
    }
    internal void Copy(string name)
    {
        var path = Path.Combine(Root, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.Copy(Path.Combine(StaticCheckVectorTests.Root, name), path, true);
    }
    public void Dispose() => Directory.Delete(Root, true);
}

[Trait("Category", "StaticChecks")]
public sealed class StaticPolicyTests
{
    private static string Root => StaticCheckVectorTests.Root;
    private static string Source(string name) => Repository.ReadSource(Path.Combine(Root, name));

    [Theory]
    [InlineData("Reference", "System.Xml.Linq", false)]
    [InlineData("Reference", "System.Text.Json", true)]
    [InlineData("PackageReference", "System.Text.Json", true)]
    [InlineData("Reference", "Newtonsoft.Json", true)]
    [InlineData("PackageReference", "Newtonsoft.Json", true)]
    [InlineData("ProjectReference", "TiaMcp.Logic.csproj", true)]
    [InlineData("ProjectReference", "$(Unresolved)/Other.csproj", true)]
    public void AdapterReferenceSentinels(string kind, string name, bool fails)
    {
        using var tree = new StaticTree();
        tree.Write("src/Adapters/Adapter.csproj", $"<Project><ItemGroup><{kind} Include=\"{name}\" /></ItemGroup></Project>");
        Assert.Equal(fails, AdapterBoundary.Check(tree.Root).Count > 0);
    }
    [Fact]
    public void AdapterChecksTransitiveReferencesAndSourceButIgnoresBuildHelpers()
    {
        using var tree = new StaticTree();
        tree.Write("src/Adapters/Adapter.csproj", "<Project><ItemGroup><ProjectReference Include=\"$(MSBuildThisFileDirectory)Dependency.csproj\" /></ItemGroup></Project>");
        tree.Write("src/Adapters/Dependency.csproj", "<Project><ItemGroup><PackageReference Include=\"System.Text.Json\" /></ItemGroup></Project>");
        Assert.Single(AdapterBoundary.Check(tree.Root));
        tree.Write("src/Adapters/Dependency.csproj", "<Project />");
        tree.Write("src/Adapters/build/Test-AdapterInputs.cs", "using System.Text.Json;");
        tree.Write("src/Adapters/build/Test-WorkerIsolation.cs", "using TiaMcp.Logic;");
        tree.Write("src/Adapters/obj/Generated.cs", "using System.Text.Json;");
        Assert.Empty(AdapterBoundary.Check(tree.Root));
        tree.Write("src/Adapters/Native.cs", "using System.Text.Json;");
        Assert.Single(AdapterBoundary.Check(tree.Root));
    }
    [Fact]
    public void ExternalInstallerMatchesUpstreamToml()
    {
        var expected = Directory.EnumerateFiles(Path.Combine(Root, "third_party/siemens-plc-tools"), "pyproject.toml", SearchOption.AllDirectories).SelectMany(p => ProjectDependencies.Read(Repository.ReadSource(p))).ToHashSet();
        var installer = Source("src/Engine/Cli/InstallPlcToolsCommand.cs");
        var actual = Regex.Match(installer, @"ExternalPackages\s*=\s*\{(.*?)\n\s*\};", RegexOptions.Singleline);
        Assert.True(actual.Success);
        Assert.True(expected.SetEquals(Regex.Matches(actual.Groups[1].Value, "\"([^\"\\n]+)\"").Select(m => m.Groups[1].Value)));
        foreach (var name in new[] { "pytest", "pytest-asyncio", "pytest-cov", "reportlab" }) Assert.Contains("\"" + name + "\"", installer);
    }
    [Fact]
    public void TomlDependencyArraysRespectQuotesCommentsExtrasAndEscapes()
    {
        Assert.Equal(new[] { "uvicorn[standard]>=1", "other#package", "text", "中文" }, ProjectDependencies.Read("[project]\ndependencies = [\n# reason\n'uvicorn[standard]>=1', \"other#package\", \"te\\u0078t\", 'plc-local',]\n[project.optional-dependencies]\nopcua = ['中文']\ntest = ['ignored']\n"));
        foreach (var value in new[] { "'not an array'", "[42]", "['a' 'b']", "[\"unterminated]", "['''multiline''']" }) Assert.Throws<ReleaseException>(() => ProjectDependencies.Read("[project]\ndependencies=" + value));
    }
    [Theory]
    [InlineData("third_party/TiaGitAddIn.Core/LICENSE", "TiaGitAddIn.Core-LICENSE.txt")]
    [InlineData("third_party/SiemensOpcUaModelled/LICENSE.md", "SiemensOpcUaModelled-LICENSE.md")]
    [InlineData("third_party/eido-import-planner/LICENSE", "Eido-LICENSE.txt")]
    [InlineData("third_party/tia-openness-studio/LICENSE", "TiaOpennessStudio-LICENSE.txt")]
    [InlineData("src/Studio/Gui/Fonts/JetBrainsMono-OFL.txt", "JetBrainsMono-OFL.txt")]
    [InlineData("src/Studio/Gui/Fonts/NotoSansSC-OFL.txt", "NotoSansSC-OFL.txt")]
    [InlineData("reference/siemens-code-snippets/LICENSE.md", "SiemensCodeSnippets-LICENSE.md")]
    public void ShippedLicensesMatchSources(string source, string copy) => Assert.Equal(Source(source), Source("docs/licenses/" + copy));
    [Fact]
    public void PrimerAndRetainedFontsAreRequiredByAllConsumers()
    {
        string[] names = ["Themes/Primer.xaml", "Themes/Palette.Light.xaml", "Themes/Palette.Dark.xaml", "Controls/WorkbenchLogView.cs", "Controls/WorkbenchMessageBox.cs", "Controls/ResultPresentation.cs", "Controls/LogTailView.cs", "Fonts/JetBrainsMono-Regular.ttf", "Fonts/JetBrainsMono-Medium.ttf", "Fonts/JetBrainsMono-OFL.txt", "Fonts/NotoSansSC-Regular.otf", "Fonts/NotoSansSC-Bold.otf", "Fonts/NotoSansSC-OFL.txt"];
        var policy = SourceRoots.Load(Root);
        foreach (var name in names)
        {
            Assert.True(File.Exists(Path.Combine(Root, "src/Studio/Gui", name)));
            foreach (var consumer in new[] { "bundle", "package", "repository" }) Assert.Contains("src/Studio/Gui/" + name, policy.RequiredPaths(consumer));
        }
        Assert.Equal(new[] { "JetBrainsMono-Medium.ttf", "JetBrainsMono-Regular.ttf", "NotoSansSC-Bold.otf", "NotoSansSC-Regular.otf" }, Directory.EnumerateFiles(Path.Combine(Root, "src/Studio/Gui/Fonts")).Where(p => Path.GetExtension(p) is ".ttf" or ".otf").Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.False(File.Exists(Path.Combine(Root, "docs/licenses/Manrope-OFL.txt")));
    }
    [Theory]
    [InlineData("TiaOpenness.exe", true)]
    [InlineData("runtime/v21/TiaMcp.FoundationHost.exe", true)]
    [InlineData("runtime/v21/worker/TiaMcp.Engine.V21.exe", true)]
    [InlineData("runtime/tools/TiaMcp.Updater.exe", true)]
    [InlineData("runtime/tools/TiaMcp.Updater.exe.config", true)]
    [InlineData("runtime/dotnet/LICENSE.txt", true)]
    [InlineData("runtime/dotnet/ThirdPartyNotices.txt", true)]
    [InlineData(".claude-plugin/plugin.json", true)]
    [InlineData("hooks/hooks.json", true)]
    [InlineData("runtime/tools/TiaMcp.WriteGuard.exe", true)]
    [InlineData("plugin/skill/SKILL.md", true)]
    [InlineData("third_party/siemens-plc-tools/packages/plc-code/src/plc_code/cli.py", true)]
    [InlineData("third_party/simaticml-decoder/src/simaticml_decoder/parse.py", true)]
    [InlineData("runtime/verification/NativeCallWeaver.dll", false)]
    [InlineData("runtime/verification/Mono.Cecil.dll", false)]
    [InlineData("AGENTS.md", false)]
    [InlineData("Version.props", false)]
    [InlineData("RELEASE_STATUS.txt", false)]
    [InlineData(".github/workflows/release.yml", false)]
    [InlineData("TiaMcp.Updater.exe", false)]
    [InlineData("TiaMcp.Updater.exe.config", false)]
    [InlineData("src/Engine/Program.cs", false)]
    [InlineData("docs/development/runtime-layout.md", false)]
    [InlineData("build-tools/release/TiaMcp.ReleaseTool.csproj", false)]
    [InlineData("reference/tool-examples/README.md", false)]
    [InlineData("manifest/contracts/tools.json", false)]
    [InlineData("manifest/history/old.json", false)]
    [InlineData("third_party/siemens-plc-tools/packages/plc-code/tests/test_cli.py", false)]
    [InlineData("third_party/simaticml-decoder/pyproject.toml", false)]
    [InlineData("runtime-other/file.dll", false)]
    [InlineData("templates-other/file.json", false)]
    [InlineData("README.md.bak", false)]
    [InlineData("runtime/../private.key", false)]
    [InlineData("/runtime/file.dll", false)]
    [InlineData("runtime\\file.dll", false)]
    [InlineData("scripts/operations/Update-Engine.ps1", false)]
    public void DeliverySentinels(string name, bool expected) => Assert.Equal(expected, DeliveryRules.Load(Root).Delivered(name));
    [Fact]
    public void BundleTableValidatorAndLauncherRejectUnreviewedMutations()
    {
        var layout = Source(BundleLayoutChecks.Source); var validator = Source(BundleLayoutChecks.Validator);
        var launcher = Source(BundleLayoutChecks.Launcher); var project = Source(BundleLayoutChecks.GuiProject);
        var rules = DeliveryRules.Load(Root);
        foreach (var path in BundleLayoutChecks.ResourcePaths(layout)) { Assert.Contains(path, BundleLayoutChecks.ValidatedPaths(validator)); Assert.True(rules.Resource(path)); }
        Assert.Equal(new[] { "runtime/studio/TiaOpenness.exe" }, BundleLayoutChecks.LauncherPaths(layout, launcher, project));
        foreach (var changed in new[] { launcher.Replace("\"studio\"", "\"other\""), launcher.Replace("\"TiaOpenness.exe\"", "\"Other.exe\""), launcher + "\nPath.Combine(root, \"other\", \"TiaOpenness.exe\");", launcher.Replace("File.Exists(desktop)", "File.Exists(\"other.exe\")") }) Assert.Throws<ReleaseException>(() => BundleLayoutChecks.LauncherPaths(layout, changed, project));
        Assert.Throws<ReleaseException>(() => BundleLayoutChecks.LauncherPaths(layout.Replace("\"runtime/studio\"", "\"runtime/desktop\""), launcher, project));
        Assert.Throws<ReleaseException>(() => BundleLayoutChecks.LauncherPaths(layout, launcher, project.Replace("<AssemblyName>TiaOpenness", "<AssemblyName>Other")));
        foreach (var path in new[] { "../outside", "/absolute", "C:/absolute", "manifest//file", "manifest/./file" }) Assert.Throws<ReleaseException>(() => BundleLayoutChecks.ResourcePaths(layout.Replace("manifest/package-manifest.json", path)));
        Assert.Throws<ReleaseException>(() => BundleLayoutChecks.ResourcePaths(layout.Replace("{ BundleResource.PackageManifest,", "{ BundleResource.Templates,")));
        Assert.Throws<ReleaseException>(() => BundleLayoutChecks.ResourcePaths(layout.Replace("\"manifest/package-manifest.json\"", "ComputePath()")));
        Assert.Throws<ReleaseException>(() => BundleLayoutChecks.ValidatedPaths(validator.Replace("=> BundleResourcePaths", "=> Array.Empty<string>()")));
    }
    [Fact]
    public void MissingUntrackedAndUnvalidatedResourcesFail()
    {
        using var tree = new StaticTree();
        foreach (var name in new[] { BundleLayoutChecks.Source, BundleLayoutChecks.Validator, BundleLayoutChecks.Launcher, BundleLayoutChecks.GuiProject, DeliveryRules.PolicyPath }) tree.Copy(name);
        var paths = BundleLayoutChecks.ResourcePaths(Source(BundleLayoutChecks.Source));
        var tracked = new HashSet<string>();
        foreach (var path in paths) { var name = path is "templates" or "reference/siemens-openness/skills" ? path + "/fixture.md" : path; tree.Write(name, "fixture"); tracked.Add(name); }
        Assert.Empty(BundleLayoutChecks.Check(tree.Root, tracked).Errors);
        File.Delete(Path.Combine(tree.Root, "manifest/delivery.json"));
        Assert.Contains("Missing resource: manifest/delivery.json", BundleLayoutChecks.Check(tree.Root, tracked).Errors);
        tracked.Remove("templates/fixture.md");
        Assert.Contains("Resource is not in the Git file set: templates", BundleLayoutChecks.Check(tree.Root, tracked).Errors);
        Assert.DoesNotContain(BundleLayoutChecks.Check(tree.Root, null).Errors, e => e.Contains("Git file set"));
        tree.Write(BundleLayoutChecks.Validator, Source(BundleLayoutChecks.Validator).Replace("\"templates\"", "\"unrelated\""));
        Assert.Contains("Resource is not checked by the C# bundle validator: templates", BundleLayoutChecks.Check(tree.Root, tracked).Errors);
        tracked.Add("runtime/source.cs");
        Assert.Contains("Shipped runtime/hook tree contains source: runtime/source.cs", BundleLayoutChecks.Check(tree.Root, tracked).Errors);
    }
    [Fact]
    public void RepositoryProductArchiveChangelogAndLinkSentinels()
    {
        using var tree = new StaticTree();
        var oldEngine = "TiaMcp" + "Server"; var old = oldEngine + ".exe"; var launcher = "TiaMcp" + "Configurator.exe";
        var cases = new Dictionary<string, string> { ["scripts/start.ps1"] = old, ["src/find.cs"] = old.ToUpperInvariant(), ["docs/current.md"] = launcher, ["manifest/current.json"] = old, ["docs/releases/v3.md"] = old, ["manifest/contracts/v4/responses/21.json"] = old, ["src/namespace.cs"] = "namespace " + oldEngine + "; namespace TiaMcp" + "Configurator;", ["scripts/current.ps1"] = "TiaMcp.Engine.V20.exe TiaMcp.Engine.V21.exe TiaMcp.FoundationHost.exe TiaOpenness.exe", ["scripts/process.ps1"] = "Get-Process -Name \"" + oldEngine + "\"", ["src/identity.csproj"] = "<AssemblyName>" + oldEngine + "</AssemblyName>", ["scripts/dumps.ps1"] = "-match '^(Portal|" + oldEngine + ")\\.'", ["scripts/build/Build-Release.ps1"] = old + "; runtime/v21/" + old, [launcher] = "retired launcher filename" };
        foreach (var pair in cases) tree.Write(pair.Key, pair.Value);
        Assert.Equal(9, RepositoryChecks.ProductNames(tree.Root, cases.Keys).Count);
        Assert.Single(RepositoryChecks.ProductNames(tree.Root, ["runtime/v21/" + old]));
        Assert.Equal(4, RepositoryChecks.ForbiddenScripts(["scripts/old.ps1", "hooks/old.PSM1", "scripts/old.bat", "scripts/old.cmd", "scripts/current.py"]).Count);
        Assert.Empty(RepositoryChecks.ForbiddenScripts(["scripts/current.py"]));
        tree.Write("Version.props", "<Project><TiaMcpRelease>3.3.0</TiaMcpRelease></Project>");
        tree.Write("CHANGELOG.md", "# Changes\n\n## [3.3.0] - 2026-10-03\n");
        Assert.Empty(RepositoryChecks.ChangelogErrors(tree.Root));
        tree.Write("CHANGELOG.md", "## [4.0.0] - draft\n## [3.3.0]\n");
        Assert.Single(RepositoryChecks.ChangelogErrors(tree.Root));
        var document = tree.Write("docs/README.md", "fixture"); tree.Write("README.md", "fixture");
        Assert.Null(RepositoryChecks.LocalTarget(tree.Root, document, "../README.md#anchor"));
        Assert.Null(RepositoryChecks.LocalTarget(tree.Root, document, "https://example.invalid"));
        Assert.NotNull(RepositoryChecks.LocalTarget(tree.Root, document, "../__missing__.md"));
        Assert.NotNull(RepositoryChecks.LocalTarget(tree.Root, document, "../../../__outside__.md"));
        foreach (var path in Directory.EnumerateFiles(Path.Combine(Root, "manifest/history/contracts-v3"), "*", SearchOption.AllDirectories)) tree.Copy(Path.GetRelativePath(Root, path));
        Assert.Empty(RepositoryChecks.ArchiveErrors(tree.Root));
        var archived = "manifest/history/contracts-v3/baseline/21.json";
        tree.Write(archived, Source(archived) + "\n"); Assert.NotEmpty(RepositoryChecks.ArchiveErrors(tree.Root)); tree.Copy(archived);
        tree.Write("manifest/history/contracts-v3/extra.json", "{}"); Assert.NotEmpty(RepositoryChecks.ArchiveErrors(tree.Root)); File.Delete(Path.Combine(tree.Root, "manifest/history/contracts-v3/extra.json"));
        tree.Write("manifest/contracts/baseline/21.json", "{}"); Assert.NotEmpty(RepositoryChecks.ArchiveErrors(tree.Root));
        tree.Write("manifest/history/contracts-v3/provenance.json", "{}"); Assert.NotEmpty(RepositoryChecks.ArchiveErrors(tree.Root));
    }
    [Theory]
    [InlineData("CallTool(\"missing\", new {});", 1)]
    [InlineData("await client.CallToolAsync(\"missing\", args);", 1)]
    [InlineData("new { tool = \"missing\", args = new {} };", 1)]
    [InlineData("new JsonObject { [\"name\"] = \"missing\", [\"arguments\"] = new JsonObject() };", 1)]
    [InlineData("journal.Call(\"native.member\", () => Openness());", 0)]
    [InlineData("Call(\"missing.member\", args);", 1)]
    [InlineData("Call(\"missing\", () => F());", 1)]
    [InlineData("Call(\"tools/list\", args);", 0)]
    [InlineData("Call(\"{\\\"jsonrpc\\\":\\\"2.0\\\"}\", args);", 0)]
    [InlineData("// Call(\"missing\", args);\nCall(\"known\", args);", 0)]
    public void CSharpScriptCallsRejectLiteralUnknownTools(string source, int count) => Assert.Equal(count, ScriptToolCalls.Check("scripts/example.cs", source, new HashSet<string> { "known" }).Count);
    [Fact]
    public void ScriptJsonPlansAndHistoricalAllowlist()
    {
        const string source = "{\"steps\":[{\"tool\":\"missing\",\"args\":{}},{\"name\":\"known\",\"arguments\":{}}]}";
        Assert.Single(ScriptToolCalls.Check("scripts/plans/example.json", source, new HashSet<string> { "known" }));
        Assert.Empty(ScriptToolCalls.Check(ScriptToolCalls.DataAllowlist[0], source, new HashSet<string>()));
    }
    [Fact]
    public void PackageModeHonorsOldTagDeliveryWithoutRequiringCurrentAssets()
    {
        using var tree = new StaticTree();
        tree.Write("scripts/operations/delivery-files.json", "{\"schemaVersion\":1,\"include\":{\"files\":[\"README.md\"],\"prefixes\":[\"scripts/operations/\",\"manifest/\",\"templates/\",\"runtime/\"]},\"exclude\":{\"files\":[],\"prefixes\":[]},\"legacyCleanup\":{\"files\":[],\"prefixes\":[]},\"requiredFiles\":[\"README.md\"]}");
        tree.Write("README.md", "Old package");
        tree.Write("scripts/operations/Start-Legacy.cmd", "legacy entrypoint");
        var package = new JsonObject { ["entrypoints"] = new JsonObject { ["mcpServer"] = "runtime/v21/TiaMcp" + "Server.exe", ["mcpServerArgs"] = new JsonArray() }, ["cli"] = new JsonObject { ["exe"] = "runtime/v21/TiaMcp" + "Server.exe" }, ["capabilities"] = new JsonObject { ["mcpToolCount"] = 0 } };
        tree.Write("manifest/package-manifest.json", package.ToJsonString());
        tree.Write("manifest/tools-list.json", "{\"toolCount\":0,\"tools\":[]}");
        tree.Write("templates/project-blueprints/full_plc_hmi_project.json", "{\"requiredBundleFiles\":[]}");
        Assert.Empty(RepositoryChecks.Check(tree.Root, true, true).Errors);
        File.Delete(Path.Combine(tree.Root, "README.md"));
        Assert.NotEmpty(RepositoryChecks.Check(tree.Root, true, true).Errors);
        tree.Write("README.md", "Old package");
        package["capabilities"]!["mcpToolCount"] = 1;
        tree.Write("manifest/package-manifest.json", package.ToJsonString());
        Assert.Contains("Tool inventory count/uniqueness differs from package metadata", RepositoryChecks.Check(tree.Root, true, true).Errors);
        package["capabilities"]!["mcpToolCount"] = 0;
        package["entrypoints"]!["mcpServer"] = "runtime/v21/TiaMcp.Engine.V21.exe";
        tree.Write("manifest/package-manifest.json", package.ToJsonString());
        Assert.Contains("package entry mcpServerArgs: expected the default release key", RepositoryChecks.Check(tree.Root, true, true).Errors);
        Assert.Contains(RepositoryChecks.Check(tree.Root, true, true).Errors, e => e.StartsWith("bundle resource:"));
        Assert.Contains("Forbidden tracked script file: scripts/operations/Start-Legacy.cmd", RepositoryChecks.Check(tree.Root, true, true).Errors);
    }
    [Fact]
    public void SelfTestCommandsAcceptCSharpAndRejectInvalidDispatch()
    {
        ReleaseCheckPolicy Policy(ReleaseSelfTest test) => new(["review"], ["review"], [new("docs/", ["review"])], [test]);
        Policy(new("review", "build-tools/release/RepositoryChecks.cs", Command: ["check-repository", "-SelfTest"])).Validate();
        Policy(new("review", "build-tools/release/DotnetSuites.cs", Command: ["test-suites", "-SelfTest"])).Validate();
        Policy(new("review", "scripts/checks/Check-Repository.py", ["--self-test"])).Validate();
        foreach (var command in new[] { System.Array.Empty<string>(), new[] { "release" }, new[] { "check-unknown" }, new[] { "dotnet" } }) Assert.Throws<ReleaseException>(() => Policy(new("review", "build-tools/release/RepositoryChecks.cs", Command: command)).Validate());
        Assert.Throws<ReleaseException>(() => Policy(new("review", "../outside.cs", Command: ["check-repository"])).Validate());
        using var tree = new StaticTree();
        var calls = new List<string>();
        ReleaseCommands.RunCandidateChecks(new("quick", ["repository-self-test", "dotnetsuites-self-test"], [], []),
            tree.Root, tree.Root, tree.Root, tree.Root, "dotnet-test-double", "python-test-double", 1,
            (name, action) => { if (name != "11-product-smoke") action(); },
            (_, _) => throw new InvalidOperationException("C# check dispatched to Python"),
            (_, _) => throw new InvalidOperationException("Unexpected product check"),
            (name, executable, arguments, defaults, cwd) =>
            {
                calls.Add(name);
                Assert.False(defaults);
                if (name == "repository-self-test")
                {
                    Assert.Equal("dotnet-test-double", executable);
                    Assert.Equal([typeof(ReleaseCommands).Assembly.Location, "check-repository", "-SelfTest"], arguments);
                }
                else
                {
                    Assert.Equal("test-suites", executable);
                    Assert.Equal(["-SelfTest"], arguments);
                }
                return new CommandResult(0, "", "");
            });
        Assert.Equal(["dotnetsuites-self-test", "repository-self-test"], calls.Order(StringComparer.Ordinal));
    }
}
