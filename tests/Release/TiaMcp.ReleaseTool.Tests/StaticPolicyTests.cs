using System.Text;
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

    [Fact]
    public void CSharpFeatureCasesDeduplicateExactConfigurationsAndKeepDifferentProperties()
    {
        using var tree = new StaticTree();
        Directory.CreateDirectory(Path.Combine(tree.Root, "src/Adapters")); Directory.CreateDirectory(Path.Combine(tree.Root, "src/Studio/Openness"));
        const string project = "tests/unit/unit.csproj";
        tree.Write(project, "<Project><PropertyGroup><DefineConstants>FIXTURE</DefineConstants></PropertyGroup></Project>");
        var suites = new JsonObject
        {
            ["offline"] = new JsonObject { ["project"] = project, ["arguments"] = new JsonArray() },
            ["kept-code"] = new JsonObject { ["project"] = project, ["arguments"] = new JsonArray("-p:DefineConstants=TIA_V20") }
        };
        tree.Write("tests/test-suites.json", suites.ToJsonString());
        var actual = TiaFeatures.ProjectCases(tree.Root);
        Assert.Equal(2, actual.Count(row => row.Project == project));
        Assert.Contains(actual, row => row.Project == project && row.Properties.Count == 0);
        Assert.Contains(actual, row => row.Project == project && row.Properties.GetValueOrDefault("DefineConstants") == "TIA_V20");
    }

    [Fact]
    public void Plugin_guard_repository_check_detects_a_legacy_only_matcher()
    {
        using var tree = new StaticTree();
        tree.Copy(".claude-plugin/plugin.json"); tree.Copy("hooks/hooks.json");
        Assert.Empty(RepositoryChecks.PluginWriteGuardErrors(tree.Root));
        var path = Path.Combine(tree.Root, "hooks/hooks.json");
        var hooks = JsonNode.Parse(File.ReadAllText(path))!;
        hooks["hooks"]!["PreToolUse"]![0]!["matcher"] = "mcp__tia-portal__.*";
        File.WriteAllText(path, hooks.ToJsonString());
        Assert.Contains(RepositoryChecks.PluginWriteGuardErrors(tree.Root), error => error.Contains("plugin_", StringComparison.Ordinal));
    }

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
        var installer = Source("src/Shared/Host/InstallPlcToolsCommand.cs");
        var actual = Regex.Match(installer, @"ExternalPackages\s*=\s*\{(.*?)\n\s*\};", RegexOptions.Singleline);
        Assert.True(actual.Success);
        Assert.True(expected.SetEquals(Regex.Matches(actual.Groups[1].Value, "\"([^\"\\n]+)\"").Select(m => m.Groups[1].Value)));
        foreach (var name in new[] { "pytest", "pytest-asyncio", "pytest-cov", "reportlab" }) Assert.Contains("\"" + name + "\"", installer);
    }
    [Fact]
    public void Package_cli_targets_the_host_subset_and_the_host_ships_the_installer()
    {
        var cli = new JsonObject();
        CatalogGenerator.WriteHostCli(cli);
        Assert.Equal("runtime/v21/TiaMcp.FoundationHost.exe", (string?)cli["exe"]);
        Assert.Equal(new[] { "doctor", "audit verify", "schema", "version", "config", "install-plc-tools", "help" },
            cli["verbs"]!.AsArray().Select(v => (string)v!));
        Assert.Equal(new[] { "0", "2", "3", "4", "5", "64", "70" }, cli["exitCodes"]!.AsObject().Select(p => p.Key));
        Assert.Equal("rejected, doctor not ready or no successful config writes", (string?)cli["exitCodes"]?["2"]);
        Assert.Contains("install-plc-tools preserves pip's exit status.", (string?)cli["description"]);
        // P8-04l: the engine projects that also linked the installer are deleted; the host keeps it.
        Assert.Contains("../Shared/Host/InstallPlcToolsCommand.cs", Source("src/FoundationHost/TiaMcp.FoundationHost.csproj"));
        foreach (var project in new[] { "src/Engine/TiaMcp.Engine.V20.csproj", "src/Engine/TiaMcp.Engine.V21.csproj" })
            Assert.False(File.Exists(Path.Combine(Root, project)));
        Assert.Contains("WriteHostCli(package[\"cli\"]!.AsObject())", Source("build-tools/release/CatalogGenerator.cs"));
    }

    [Theory]
    [InlineData("third_party/TiaGitAddIn.Core/LICENSE", "TiaGitAddIn.Core-LICENSE.txt")]
    [InlineData("third_party/SiemensOpcUaModelled/LICENSE.md", "SiemensOpcUaModelled-LICENSE.md")]
    [InlineData("third_party/eido-import-planner/LICENSE", "Eido-LICENSE.txt")]
    [InlineData("third_party/tia-openness-studio/LICENSE", "TiaOpennessStudio-LICENSE.txt")]
    [InlineData("reference/siemens-code-snippets/LICENSE.md", "SiemensCodeSnippets-LICENSE.md")]
    public void ShippedLicensesMatchSources(string source, string copy) => Assert.Equal(Source(source), Source("docs/licenses/" + copy));

    [Fact]
    public void RetiredWorkbenchFontLicenceIsAbsentAndExcluded()
    {
        const string licence = "docs/licenses/MapleMono-CN-OFL.txt";
        Assert.False(File.Exists(Path.Combine(Root, licence)));
        var rules = DeliveryRules.Load(Root);
        Assert.False(rules.Delivered(licence));
        Assert.DoesNotContain(licence, DeliveryRules.Strings(rules.Data["include"]!["files"]));
        Assert.Contains(licence, DeliveryRules.Strings(rules.Data["exclude"]!["files"]));
    }

    [Fact]
    public void SharedThemeDictionariesAreRequiredWithoutPrivateFonts()
    {
        string[] names = ["Themes/Primer.xaml", "Themes/Shell.xaml", "Themes/Approvals.xaml", "Themes/Pages.xaml", "Themes/Palette.HighContrast.xaml", "Themes/Palette.Light.xaml", "Themes/Palette.Dark.xaml", "Controls/WorkbenchLogView.cs", "Controls/WorkbenchMessageBox.cs", "Controls/ResultPresentation.cs", "Controls/LogTailView.cs"];
        foreach (var name in names)
        {
            Assert.True(File.Exists(Path.Combine(Root, "src/Studio/Gui", name)));
        }
        Assert.False(Directory.Exists(Path.Combine(Root, "src/Studio/Gui/Fonts"))
            && Directory.EnumerateFiles(Path.Combine(Root, "src/Studio/Gui/Fonts")).Any());
        Assert.False(File.Exists(Path.Combine(Root, "docs/licenses/MapleMono-CN-OFL.txt")));
        Assert.False(File.Exists(Path.Combine(Root, "docs/licenses/Manrope-OFL.txt")));
    }

    [Theory]
    [InlineData("TiaOpenness.exe", true)]
    [InlineData("runtime/v21/TiaMcp.FoundationHost.exe", true)]
    [InlineData("runtime/v21/worker/TiaMcp.PlcWorker.21.exe", true)]
    [InlineData("runtime/v21/worker/TiaMcp.Engine.V21.exe", false)]
    [InlineData("runtime/tools/TiaMcp.Updater.exe", true)]
    [InlineData("runtime/tools/TiaMcp.Updater.exe.config", true)]
    [InlineData("runtime/dotnet/LICENSE.txt", true)]
    [InlineData("runtime/dotnet/ThirdPartyNotices.txt", true)]
    [InlineData(".claude-plugin/plugin.json", true)]
    [InlineData("hooks/hooks.json", true)]
    [InlineData("reference/schemas/standards/v1/package.schema.json", true)]
    [InlineData("schemas/standards/v1/package.schema.json", false)]
    [InlineData("runtime/README.md", true)]
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
    [InlineData("docs/standards/design.md", false)]
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

    [Theory]
    [InlineData("call('RemovedTool', {})")]
    [InlineData("call('removedtool', {})")]
    [InlineData("call('Removed-Tool', {})")]
    [InlineData("S('RemovedTool', x=1)")]
    [InlineData("p.rpc('tools/call', {'name': 'known', 'arguments': {'name': 'RemovedTool', 'arguments': {}}})")]
    [InlineData("cases = [('RemovedTool', 'probe', {})]")]
    [InlineData("cases = [('probe', 'RemovedTool', {}, 'ok')]")]
    [InlineData("calls = {'RemovedTool': {}}")]
    [InlineData("TOOLS = ['RemovedTool']\nfor name in TOOLS: call(name, {})")]
    [InlineData("TOOLS = ['RemovedTool']\nfor name in TOOLS:\n    client.call(name, {})")]
    [InlineData("call('Removed\\u0054ool', {})")]
    [InlineData("call('Removed' 'Tool', {})")]
    public void PythonScriptCallsRejectUnknownToolsAcrossHelpersRequestsAndTables(string source) =>
        Assert.Contains(ScriptToolCalls.Check("scripts/example.py", source, new HashSet<string> { "known" }), call => call.Name.Contains("ool", StringComparison.Ordinal));

    [Theory]
    [InlineData("call('known', {'methodName': 'GetCrossReferences'}) # call('RemovedTool')")]
    [InlineData("call('\\x6bno\\167n', {})")]
    [InlineData("call('Siemens::Read()')\ncall('{\"fixture\":true}')")]
    [InlineData("'''call('RemovedTool')'''\n# call('RemovedTool')\ncall('tools/list', {})")]
    [InlineData("TOOLS = ['RemovedTool']\nfor name in TOOLS:\n    print(name)\ncall('known', {})")]
    [InlineData("call(f'{name}', {})\ncall(b'RemovedTool', {})")]
    [InlineData("call('System.Void ' + name + '::.ctor()', 2, 'System.Void', 'newobj')")]
    [InlineData("OPEN = '('\nDATA = ['Label', 'RemovedTool', '{']\ncall('known', {})")]
    [InlineData("{'name': prefix + 'RemovedTool', 'arguments': {}}")]
    public void PythonScriptCallsIgnorePayloadsCommentsAndUndispatchedTables(string source) =>
        Assert.Empty(ScriptToolCalls.Check("scripts/example.py", source, new HashSet<string> { "known" }));

    [Theory]
    [InlineData("Call 'RemovedTool' @{calls=@(@{name='RemovedNested';arguments=@{}})}", 2)]
    [InlineData("client.exe call RemovedTool", 1)]
    [InlineData("$client bridge RemovedTool", 1)]
    [InlineData("python probe.py --tool RemovedTool", 1)]
    [InlineData("$url + '?name=' + [Uri]::EscapeDataString($name)", 0)]
    public void PowerShellScriptCallsRejectUnknownTools(string source, int count) =>
        Assert.Equal(count, ScriptToolCalls.Check("scripts/example.ps1", source, new HashSet<string>()).Count);

    [Theory]
    [InlineData("py", "call('RemovedTool', {})")]
    [InlineData("ps1", "Call 'RemovedTool' @{}")]
    public void ScriptCallCommandScansPythonAndPowerShellFiles(string extension, string source)
    {
        using var tree = new StaticTree();
        tree.Copy("src/Logic/ModelContextProtocol/ToolProfiles.resx");
        tree.Write("scripts/probe." + extension, source);
        Assert.Equal(1, ScriptToolCalls.Run(tree.Root));
        tree.Write("scripts/probe." + extension, source.Replace("RemovedTool", "GetSessionState", StringComparison.Ordinal));
        Assert.Equal(0, ScriptToolCalls.Run(tree.Root));
    }

    [Fact]
    public void ScriptJsonPlansAndHistoricalAllowlist()
    {
        const string source = "{\"steps\":[{\"tool\":\"missing\",\"args\":{}},{\"name\":\"known\",\"arguments\":{}}]}";
        Assert.Single(ScriptToolCalls.Check("scripts/plans/example.json", source, new HashSet<string> { "known" }));
        Assert.Empty(ScriptToolCalls.Check(ScriptToolCalls.DataAllowlist[0], source, new HashSet<string>()));
    }

}

// Only the upstream project's dependency string arrays are consumed. Reject
// unfamiliar array syntax instead of silently omitting a runtime dependency.
internal static class ProjectDependencies
{
    internal static string[] Read(string source)
    {
        var dependencies = new List<string>();
        var table = "";
        var index = 0;
        while (index < source.Length)
        {
            var end = source.IndexOf('\n', index);
            if (end < 0) end = source.Length;
            var line = source[index..end].Trim();
            if (line.StartsWith('[')) table = line.Split('#', 2)[0].Trim();
            var match = Regex.Match(line, @"\A(dependencies|opcua|web)\s*=\s*");
            if (match.Success && (table == "[project]" && match.Groups[1].Value == "dependencies" || table == "[project.optional-dependencies]" && match.Groups[1].Value is "opcua" or "web"))
            {
                var opening = index + source[index..end].IndexOf('=') + 1;
                var result = ReadArray(source, opening);
                dependencies.AddRange(result.Rows);
                index = result.End;
            }
            else index = end;
            if (index < source.Length && source[index] == '\n') index++;
        }
        return dependencies.Where(d => !d.StartsWith("plc-", StringComparison.Ordinal)).ToArray();
    }
    private static (List<string> Rows, int End) ReadArray(string source, int cursor)
    {
        var rows = new List<string>();
        void Space()
        {
            while (cursor < source.Length)
            {
                if (char.IsWhiteSpace(source[cursor])) cursor++;
                else if (source[cursor] == '#') { while (cursor < source.Length && source[cursor] != '\n') cursor++; }
                else break;
            }
        }
        Space();
        if (cursor >= source.Length || source[cursor++] != '[') throw new ReleaseException("Expected TOML dependency string array");
        Space();
        while (cursor < source.Length && source[cursor] != ']')
        {
            var quote = source[cursor++];
            if (quote is not ('\'' or '"') || cursor + 1 < source.Length && source[cursor] == quote && source[cursor + 1] == quote) throw new ReleaseException("Unsupported TOML dependency string syntax");
            var text = new StringBuilder();
            while (cursor < source.Length && source[cursor] != quote)
            {
                var c = source[cursor++];
                if (c is '\r' or '\n') throw new ReleaseException("Unterminated TOML dependency string");
                if (c == '\\' && quote == '"')
                {
                    if (cursor >= source.Length) throw new ReleaseException("Unterminated TOML escape");
                    var escape = source[cursor++];
                    if (escape is 'u' or 'U')
                    {
                        var width = escape == 'u' ? 4 : 8;
                        if (cursor + width > source.Length || !Regex.IsMatch(source.Substring(cursor, width), "\\A[0-9a-fA-F]+\\z")) throw new ReleaseException("Invalid TOML Unicode escape");
                        text.Append(char.ConvertFromUtf32(Convert.ToInt32(source.Substring(cursor, width), 16))); cursor += width;
                    }
                    else text.Append(escape switch { 'b' => '\b', 't' => '\t', 'n' => '\n', 'f' => '\f', 'r' => '\r', '"' => '"', '\\' => '\\', _ => throw new ReleaseException("Invalid TOML escape") });
                }
                else text.Append(c);
            }
            if (cursor >= source.Length) throw new ReleaseException("Unterminated TOML dependency string");
            cursor++; rows.Add(text.ToString()); Space();
            if (cursor < source.Length && source[cursor] == ',') { cursor++; Space(); }
            else if (cursor >= source.Length || source[cursor] != ']') throw new ReleaseException("Expected comma in TOML dependency array");
        }
        if (cursor >= source.Length) throw new ReleaseException("Unterminated TOML dependency array");
        return (rows, ++cursor);
    }
}
