using System.IO.Compression;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using System.Text.RegularExpressions;
using TiaMcp.ReleaseTool;
using Xunit;

namespace TiaMcp.ReleaseTool.Tests;

public sealed class PrepareTests
{
    [Fact]
    public void PrepareCommandIsRegistered()
    {
        Assert.Contains("prepare", CommandLine.Commands);
    }

    [Theory]
    [InlineData(" M Version.props", "master", "clean working tree")]
    [InlineData("?? new-file.txt", "master", "clean working tree")]
    [InlineData("M  Version.props", "master", "clean working tree")]
    [InlineData("", "refactor/NEW-REL-A", "requires master")]
    [InlineData("", "", "requires master")]
    public void RefusesUncommittedFilesAndNonMasterBranches(string status, string branch, string error)
        => Assert.Contains(error, Assert.Throws<ReleaseException>(() => Prepare.RequireRepository(status, branch)).Message);

    [Fact]
    public void AcceptsCleanMaster() => Prepare.RequireRepository("\n", "master\n");

    [Theory]
    [InlineData("Version.props", "<Project><PropertyGroup><TiaMcpRelease>3.0.0</TiaMcpRelease></PropertyGroup></Project>", "Version.props")]
    [InlineData(".claude-plugin/plugin.json", "{\"version\":\"3.0.0\"}", "plugin.json")]
    [InlineData(".claude-plugin/marketplace.json", "{\"plugins\":[{\"version\":\"3.0.0\"}]}", "marketplace.json")]
    [InlineData(".claude-plugin/marketplace.json", "{\"plugins\":[]}", "marketplace.json")]
    [InlineData("CHANGELOG.md", "## [Unreleased]\n## [3.0.0] - 2026-11-02\n## [4.0.0-beta.3] - 2026-11-01", "Newest CHANGELOG")]
    [InlineData("CHANGELOG.md", "## [4.0.0-beta.3] - 2026-02-30", "real ISO date")]
    [InlineData("CHANGELOG.md", "## [4.0.0-beta.3] - 2026-1-1", "real ISO date")]
    public void RefusesEachReleaseInputMismatch(string path, string contents, string error)
    {
        using var tree = VersionTree();
        tree.Write(path, contents);
        Assert.Contains(error, Assert.Throws<ReleaseException>(() => Prepare.RequireVersion(tree.Root, "4.0.0-beta.3")).Message);
    }

    [Fact]
    public void UsesNewestReleaseDateAndSkipsUnreleased()
    {
        using var tree = VersionTree();
        Assert.Equal(new DateOnly(2026, 11, 2), Prepare.RequireVersion(tree.Root, "4.0.0-beta.3"));
    }

    [Fact]
    public void RefusesMissingAndStaleNotesWithoutRewritingThem()
    {
        using var tree = VersionTree();
        Assert.Contains("is missing", Assert.Throws<ReleaseException>(() => Prepare.RequireNotes(tree.Root, "4.0.0-beta.3")).Message);
        foreach (var source in new[] { PrereleaseNotes.LedgerPath, PrereleaseNotes.ChecklistPath }) tree.Copy(source);
        var path = tree.Write(PrereleaseNotes.NotesPath("4.0.0-beta.3"), PrereleaseNotes.Begin + "\nstale\n" + PrereleaseNotes.End + "\n");
        var before = File.ReadAllText(path);
        Assert.Throws<ReleaseException>(() => Prepare.RequireNotes(tree.Root, "4.0.0-beta.3"));
        Assert.Equal(before, File.ReadAllText(path));
        File.WriteAllText(path, PrereleaseNotes.Generate(File.ReadAllText(Path.Combine(tree.Root, PrereleaseNotes.LedgerPath)),
            File.ReadAllText(Path.Combine(tree.Root, PrereleaseNotes.ChecklistPath))));
        Prepare.RequireNotes(tree.Root, "4.0.0-beta.3");
    }

    [Theory]
    [InlineData("14sp1")]
    [InlineData("15.1")]
    [InlineData("16")]
    [InlineData("17")]
    [InlineData("18")]
    [InlineData("19")]
    [InlineData("20")]
    [InlineData("21")]
    public void EveryPublicApiRootIsRequired(string missing)
    {
        using var tree = new StaticTree();
        foreach (var key in PlcWorkerFixtures.Keys)
        {
            var path = $"src/Adapters/{PrepareBuild.AdapterFolder(key)}/Release.props";
            tree.Write(path, "<Project><ItemGroup><AdapterApi Include=\"$(SiemensEngineeringDirectory)/Siemens.Engineering.dll\" /></ItemGroup></Project>");
            if (key != missing) tree.Write("sdk/" + ReleaseCommands.PlcWorkerApi(key).Directory + "/Siemens.Engineering.dll", "fixture");
        }
        Assert.Contains("PublicAPI " + missing, Assert.Throws<ReleaseException>(() => Prepare.RequirePublicApi(tree.Root, Path.Combine(tree.Root, "sdk"))).Message);
        tree.Write("sdk/" + ReleaseCommands.PlcWorkerApi(missing).Directory + "/Siemens.Engineering.dll", "fixture");
        Prepare.RequirePublicApi(tree.Root, Path.Combine(tree.Root, "sdk"));
    }

    [Fact]
    public void CompanionPythonResolutionAndImportRefusalAreLocal()
    {
        using var tree = new StaticTree();
        var executable = Path.GetFullPath(tree.Write("TiaMcp_Output/ecosystem-python/Scripts/python.exe", "fixture"));
        var options = Options.Parse([], new HashSet<string>());
        Assert.Equal(executable, ReleaseCommands.ReleaseCompanionPython(options, tree.Root, _ => null));
        var selected = Path.GetFullPath(tree.Write("explicit/python.exe", "fixture"));
        Assert.Equal(selected, ReleaseCommands.ReleaseCompanionPython(Options.Parse(["-CompanionPython", selected], new HashSet<string>()), tree.Root, _ => executable));
        Assert.Equal(selected, ReleaseCommands.ReleaseCompanionPython(options, tree.Root, _ => selected));
        Assert.Throws<ReleaseException>(() => ReleaseCommands.ReleaseCompanionPython(
            Options.Parse(["-CompanionPython", Path.Combine(tree.Root, "missing.exe")], new HashSet<string>()), tree.Root, _ => null));
        Assert.Throws<ReleaseException>(() => ReleasePrerequisites.RequireEcosystemPython(executable,
            Path.Combine(tree.Root, "bridge.py"), executable, (exe, args, cwd, env) => new(1, "", "ModuleNotFoundError: reportlab")));
    }

    [Fact]
    public void ContentsExcludeOldBuildRecordsAndGeneratedInputs()
    {
        using var tree = new StaticTree();
        string[] files = ["README.md", "runtime/README.md", "runtime/v21/TiaMcp.FoundationHost.dll",
            "runtime/v21/Siemens.Engineering.Base.dll", "runtime/v21/debug.pdb", "runtime/verification/tool.dll",
            "manifest/configurator-build.json", "manifest/multi-version-build.json", "manifest/package-manifest.json",
            "manifest/tools-list.json", "manifest/delivery.json", "TEST-PACKAGE.txt"];
        foreach (var path in files) tree.Write(path, path);
        var contents = Prepare.Contents(tree.Root, files, DeliveryRules.Load(StaticCheckVectorTests.Root));
        Assert.Equal(new[] { "README.md", "runtime/README.md", "runtime/v21/TiaMcp.FoundationHost.dll" }, contents.Keys);
    }

    [Fact]
    public void ManifestsAreGeneratedOnlyInStageAndInventoryMatchesAllOtherFiles()
    {
        using var tree = ManifestTree();
        var context = Context(tree.Root);
        var tracked = new[] { "manifest/package-manifest.json", "manifest/tools-list.json" }
            .ToDictionary(path => path, path => File.ReadAllBytes(Path.Combine(tree.Root, path)));
        Prepare.GenerateManifests(context);
        Assert.All(Prepare.GeneratedPaths, path => Assert.True(File.Exists(Path.Combine(context.StageDirectory, path)), path));
        Assert.All(tracked, row => Assert.Equal(row.Value, File.ReadAllBytes(Path.Combine(tree.Root, row.Key))));
        Assert.False(File.Exists(Path.Combine(tree.Root, Prepare.InventoryPath)));
        Assert.False(File.Exists(Path.Combine(tree.Root, "manifest/delivery.json")));
        var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(context.StageDirectory, Prepare.GeneratedPaths[0])))!;
        Assert.Equal(context.Version, (string?)manifest["bundleVersion"]);
        Assert.Equal(context.PackageName, (string?)manifest["packageName"]);
        Assert.Null(manifest["checkStatus"]);
        Assert.Null(manifest["validationRecords"]?["engine"]);
        var delivery = JsonNode.Parse(File.ReadAllText(Path.Combine(context.StageDirectory, Prepare.GeneratedPaths[1])))!;
        Assert.Equal(new[] { "release", "package" }, delivery.AsObject().Select(row => row.Key));
        Assert.Empty(Prepare.InventoryErrors(context.StageDirectory));
        Assert.DoesNotContain(Prepare.InventoryPath, Prepare.Inventory(context.StageDirectory).Keys);
    }

    [Fact]
    public void StageProvidesTheHostBundleMarkerBeforeGeneratingTheCatalog()
    {
        using var tree = VersionTree();
        tree.Copy(DeliveryRules.PolicyPath);
        foreach (var path in new[] { "manifest/package-manifest.json", "manifest/tools-list.json", "src/Logic/ModelContextProtocol/ToolProfiles.resx" }) tree.Copy(path);
        var context = Context(tree.Root);
        var original = File.ReadAllBytes(Path.Combine(tree.Root, "manifest/package-manifest.json"));
        bool invoked = false;
        var temp = Path.Combine(Path.GetTempPath(), "tmr-" + Guid.NewGuid().ToString("N")[..8]);
        var buildEnvironment = Prepare.CreateBuildEnvironment(context, temp);
        bool matrixInvoked = false;
        Prepare.Stage(context, ["manifest/package-manifest.json"], buildEnvironment, catalogProducer: (executable, arguments, workingDirectory, environment) =>
        {
            invoked = true;
            Assert.Equal(context.StageDirectory, workingDirectory);
            Assert.True(File.Exists(Path.Combine(workingDirectory, "manifest/package-manifest.json")));
            Assert.Contains("--offline", arguments);
            Assert.Contains("--bundle-root", arguments);
            File.Copy(Path.Combine(tree.Root, "manifest/tools-list.json"), Path.Combine(workingDirectory, "manifest/tools-list.json"));
            return new(0, "catalog generated", "");
        }, matrixGenerator: (executable, arguments, workingDirectory, environment) =>
        {
            matrixInvoked = true;
            Assert.Equal(tree.Root, workingDirectory);
            Assert.Equal(temp, environment!["TEMP"]);
            Assert.Equal(temp, environment["DOTNET_CLI_HOME"]);
            Assert.Equal(buildEnvironment["RestoreConfigFile"], environment["RestoreConfigFile"]);
            var args = arguments.ToArray();
            Assert.EndsWith("Generate-ToolCapabilityMatrix.cs", args[1]);
            Assert.Equal(Path.Combine(context.StageDirectory, "manifest/tools-list.json"), args[Array.IndexOf(args, "--tools-list") + 1]);
            File.WriteAllText(args[Array.IndexOf(args, "--out-file") + 1], "generated matrix");
            return new(0, "matrix generated", "");
        });
        Assert.True(invoked);
        Assert.True(matrixInvoked);
        Assert.Equal("generated matrix", File.ReadAllText(Path.Combine(context.StageDirectory, "docs/reference/tool-matrix.md")));
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(tree.Root, "manifest/package-manifest.json")));
        Assert.Equal(context.Version, (string?)JsonNode.Parse(File.ReadAllText(Path.Combine(context.StageDirectory, "manifest/package-manifest.json")))!["bundleVersion"]);
        Assert.Empty(Prepare.InventoryErrors(context.StageDirectory));
    }

    [Theory]
    [InlineData("changed")]
    [InlineData("added")]
    [InlineData("removed")]
    [InlineData("duplicate")]
    [InlineData("unsafe")]
    public void InventoryRejectsEveryKindOfDrift(string drift)
    {
        using var tree = ManifestTree();
        var context = Context(tree.Root);
        Prepare.GenerateManifests(context);
        var path = Path.Combine(context.StageDirectory, "manifest/tools-list.json");
        if (drift == "changed") File.AppendAllText(path, " ");
        if (drift == "removed") File.Delete(path);
        if (drift == "added") File.WriteAllText(Path.Combine(context.StageDirectory, "extra.txt"), "extra");
        if (drift is "duplicate" or "unsafe")
        {
            var inventory = JsonNode.Parse(File.ReadAllText(Path.Combine(context.StageDirectory, Prepare.InventoryPath)))!;
            var row = inventory["files"]![0]!.DeepClone();
            if (drift == "unsafe") row["path"] = "../outside";
            inventory["files"]!.AsArray().Add(row);
            Prepare.WriteJson(Path.Combine(context.StageDirectory, Prepare.InventoryPath), inventory);
        }
        Assert.NotEmpty(Prepare.InventoryErrors(context.StageDirectory));
        Assert.Throws<ReleaseException>(() => Prepare.Archive(context));
        Assert.False(File.Exists(Path.Combine(context.OutputDirectory, context.PackageName + ".zip")));
    }

    [Fact]
    public void ArchiveContainsExactlyTheInventoriedBytesAndCannotBeOverwritten()
    {
        using var tree = ManifestTree();
        var context = Context(tree.Root);
        Prepare.GenerateManifests(context);
        var result = Prepare.Archive(context);
        Assert.Equal(ReleaseRecords.HashFile(result.ZipPath), result.Sha256);
        Assert.Equal(new FileInfo(result.ZipPath).Length, result.Size);
        Assert.Equal(context.StageDirectory, result.StageDirectory);
        Assert.Equal(result.Sha256 + "  " + Path.GetFileName(result.ZipPath) + "\n", File.ReadAllText(Path.ChangeExtension(result.ZipPath, ".sha256")));
        using var zip = ZipFile.OpenRead(result.ZipPath);
        Assert.Equal(result.FileCount, zip.Entries.Count);
        var expectedPaths = Directory.EnumerateFiles(context.StageDirectory, "*", SearchOption.AllDirectories)
            .Select(path => context.PackageName + "/" + Path.GetRelativePath(context.StageDirectory, path).Replace('\\', '/')).Order(StringComparer.Ordinal);
        Assert.Equal(expectedPaths, zip.Entries.Select(entry => entry.FullName).Order(StringComparer.Ordinal));
        Assert.All(zip.Entries, entry => Assert.StartsWith(context.PackageName + "/", entry.FullName));
        Assert.Contains(zip.Entries, entry => entry.FullName == context.PackageName + "/" + Prepare.InventoryPath);
        foreach (var entry in zip.Entries)
        {
            using var input = entry.Open();
            using var actual = new MemoryStream();
            input.CopyTo(actual);
            Assert.Equal(File.ReadAllBytes(Path.Combine(context.StageDirectory, entry.FullName[(context.PackageName.Length + 1)..])), actual.ToArray());
        }
        Assert.DoesNotContain(zip.Entries, entry => entry.Name == "TEST-PACKAGE.txt");
        Assert.Throws<IOException>(() => Prepare.Archive(context));
    }

    [Fact]
    public void DeliveryCheckRejectsTestPolicyPublicApiAndTestPackageMarker()
    {
        using var tree = ManifestTree();
        var context = Context(tree.Root);
        Prepare.GenerateManifests(context);
        tree.Write(Path.GetRelativePath(tree.Root, Path.Combine(context.StageDirectory, "templates/project-blueprints/full_plc_hmi_project.json")),
            File.ReadAllText(Path.Combine(StaticCheckVectorTests.Root, "templates/project-blueprints/full_plc_hmi_project.json")));
        tree.Write(Path.GetRelativePath(tree.Root, Path.Combine(context.StageDirectory, "runtime/test.dll")), "TiaMcpTestPolicy");
        tree.Write(Path.GetRelativePath(tree.Root, Path.Combine(context.StageDirectory, "runtime/Siemens.Engineering.dll")), "SDK");
        tree.Write(Path.GetRelativePath(tree.Root, Path.Combine(context.StageDirectory, "TEST-PACKAGE.txt")), "test");
        var errors = Prepare.DeliveryErrors(context.StageDirectory, DeliveryRules.Load(StaticCheckVectorTests.Root));
        Assert.Contains(errors, error => error.Contains("Test-only behavior policy", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("Non-delivery file in stage: runtime/Siemens.Engineering.dll", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("Non-delivery file in stage: TEST-PACKAGE.txt", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("Missing bundle resource:", StringComparison.Ordinal));
    }

    [Fact]
    public void CleanRuntimePreservesReadmeAndRejectsOutsidePaths()
    {
        using var tree = new StaticTree();
        var readme = Path.GetFullPath(tree.Write("runtime/README.md", "tracked readme"));
        tree.Write("runtime/v21/old.dll", "old");
        tree.Write("runtime/old.log", "old");
        PrepareBuild.CleanRuntime(tree.Root);
        Assert.Equal("tracked readme", File.ReadAllText(readme));
        Assert.Equal(new[] { readme }, Directory.GetFiles(Path.Combine(tree.Root, "runtime"), "*", SearchOption.AllDirectories));
        Assert.Throws<ReleaseException>(() => PrepareBuild.RequireOwnedTree(tree.Root, Path.Combine(tree.Root, "..", "outside")));
    }

    [Fact]
    public void VersionBuildTableRunsEightPropertyIsolatedItemsInRealMsbuild()
    {
        using var tree = new StaticTree();
        var fixture = tree.Write("fixture.proj", new XDocument(new XElement("Project", new XElement("Target", new XAttribute("Name", "Rebuild"),
            new XElement("Error", new XAttribute("Condition", "'$(BuildProjectReferences)' != 'false'"), new XAttribute("Text", "Shared projects must already be built")),
            new XElement("WriteLinesToFile", new XAttribute("File", "$(MSBuildThisFileDirectory)$(TiaReleaseKey).txt"),
                new XAttribute("Lines", "$(SiemensEngineeringDirectory)"), new XAttribute("Overwrite", "true"))))).ToString());
        var context = Context(tree.Root);
        var projects = PrepareBuild.VersionProjects(context).ToArray();
        Assert.Equal(24, projects.Length);
        Assert.All(PrepareBuild.VersionProjects(context with { Root = StaticCheckVectorTests.Root }), project => Assert.True(File.Exists(project.Path), project.Path));
        var workers = projects.Where(row => row.Kind == "Worker").Select(row => row with { Path = fixture }).ToArray();
        var table = tree.Write("table.proj", PrepareBuild.BuildTableDocument(workers).ToString());
        var result = ProcessRunner.Run(Environment.GetEnvironmentVariable("DOTNET_EXE") ?? "dotnet",
            ["msbuild", table, "-t:Build", "-m:6", "-nodeReuse:false", "-nologo"], tree.Root, timeoutMilliseconds: 60_000);
        Assert.True(result.ExitCode == 0, $"MSBuild exited {result.ExitCode}.\n{result.StandardOutput}{result.StandardError}");
        foreach (var key in PlcWorkerFixtures.Keys)
            Assert.Equal(Path.Combine(context.PublicApiRoot, ReleaseCommands.PlcWorkerApi(key).Directory), File.ReadAllText(Path.Combine(tree.Root, key + ".txt")).Trim());
    }

    [Theory]
    [InlineData("dirty", "clean working tree")]
    [InlineData("branch", "requires master")]
    [InlineData("version", "Version.props")]
    [InlineData("notes", "is missing")]
    [InlineData("api", "PublicAPI 21")]
    [InlineData("python", "Decoder Python is missing")]
    public void CreateContextEnforcesEveryPrerequisite(string refusal, string message)
    {
        using var tree = ContextTree();
        if (refusal == "version") tree.Write("Version.props", "<Project><PropertyGroup><TiaMcpRelease>3.0.0</TiaMcpRelease></PropertyGroup></Project>");
        if (refusal == "notes") File.Delete(Path.Combine(tree.Root, PrereleaseNotes.NotesPath("4.0.0-beta.3")));
        if (refusal == "api") File.Delete(Path.Combine(tree.Root, "sdk", ReleaseCommands.PlcWorkerApi("21").Directory, "Siemens.Engineering.dll"));
        SealRepository(tree);
        if (refusal == "dirty") tree.Write("untracked.txt", "dirty");
        if (refusal == "branch")
        {
            Git(tree, ["update-ref", "refs/heads/topic", "HEAD"]);
            Git(tree, ["symbolic-ref", "HEAD", "refs/heads/topic"]);
        }
        var options = ContextOptions(tree.Root, refusal == "python" ? "missing.exe" : "python.exe");
        Assert.Contains(message, Assert.Throws<ReleaseException>(() => Prepare.CreateContext(tree.Root, options, SuccessfulProbe)).Message);
    }

    [Fact]
    public void CreateContextAcceptsTheSmallCleanRepository()
    {
        using var tree = ContextTree();
        SealRepository(tree);
        tree.Write("bin-build/prepared/4.0.0-beta.3/previous.zip", "previous run");
        var context = Prepare.CreateContext(tree.Root, ContextOptions(tree.Root, "python.exe"), SuccessfulProbe);
        Assert.Equal(tree.Root, context.Root);
        Assert.Equal("4.0.0-beta.3", context.Version);
    }

    [Fact]
    public void RunEnvironmentIsShortExternalAndRemovedAfterEachRun()
    {
        using var tree = new StaticTree();
        var context = Context(tree.Root);
        string? firstTemp = null;
        Prepare.WithRunEnvironment(context, () => { }, environment =>
        {
            firstTemp = environment["TEMP"]!;
            Assert.Matches(@"^tmr-[a-f0-9]{8}$", Path.GetFileName(firstTemp));
            Assert.False(firstTemp.StartsWith(tree.Root, StringComparison.OrdinalIgnoreCase));
            foreach (var key in new[] { "TMP", "TMPDIR", "DOTNET_CLI_HOME" }) Assert.Equal(firstTemp, environment[key]);
            File.WriteAllText(Path.Combine(context.OutputDirectory, "old.zip"), "old");
            return 0;
        });
        Assert.False(Directory.Exists(firstTemp));
        Prepare.WithRunEnvironment(context, () => { }, environment =>
        {
            Assert.False(File.Exists(Path.Combine(context.OutputDirectory, "old.zip")));
            File.WriteAllText(Path.Combine(context.OutputDirectory, "new.zip"), "new");
            return 0;
        });
        Assert.True(File.Exists(Path.Combine(context.OutputDirectory, "new.zip")));
        Assert.Throws<ReleaseException>(() => Prepare.CreateBuildEnvironment(context, Path.Combine(tree.Root, "temp")));
        Assert.Throws<ReleaseException>(() => Prepare.RequireExternalTempRoot(tree.Root, tree.Root));
    }

    [Fact]
    public void OriginalBuildFailureSurvivesChangedTreeAndCleanup()
    {
        using var tree = new StaticTree();
        var failure = new InvalidOperationException("original build error");
        var stdout = Console.Out;
        var stderr = Console.Error;
        using var output = new StringWriter();
        using var errors = new StringWriter();
        try
        {
            Console.SetOut(output);
            Console.SetError(errors);
            var actual = Assert.Throws<InvalidOperationException>(() => Prepare.WithRunEnvironment<int>(Context(tree.Root),
                () => throw new ReleaseException("tracked file changed"), environment => throw failure));
            Assert.Same(failure, actual);
        }
        finally { Console.SetOut(stdout); Console.SetError(stderr); }
        Assert.Contains("WARNING tracked file changed", errors.ToString());
        Assert.Contains("prepare total:", output.ToString());
    }

    [Fact]
    public void PublicApiScanChecksRuntimeBeforeDeliveryFiltering()
    {
        using var tree = new StaticTree();
        tree.Write("runtime/README.md", "runtime");
        PrepareBuild.RequireNoPublicApi(tree.Root);
        tree.Write("runtime/v21/worker/Siemens.Engineering.Base.dll", "planted SDK");
        Assert.DoesNotContain("runtime/v21/worker/Siemens.Engineering.Base.dll",
            Prepare.Contents(tree.Root, [], DeliveryRules.Load(StaticCheckVectorTests.Root)).Keys);
        Assert.Contains("PublicAPI redistribution", Assert.Throws<ReleaseException>(() => PrepareBuild.RequireNoPublicApi(tree.Root)).Message);
    }

    [Theory]
    [InlineData(false, null)]
    [InlineData(true, null)]
    [InlineData(true, "P6-DEVICE:safe-v4")]
    public void WorkerMetadataMustMatchItsBuildFlavor(bool fixture, string? policy)
    {
        var metadata = new Dictionary<string, string?> { ["TiaReleaseKey"] = "21" };
        if (fixture) metadata["TiaMcpPlcWorkerSdkFixture"] = "true";
        if (policy != null) metadata["TiaMcpTestPolicy"] = policy;
        PrepareBuild.RequireWorkerMetadata(metadata, "21", fixture, policy, "worker.exe");
        Assert.Throws<ReleaseException>(() => PrepareBuild.RequireWorkerMetadata(metadata, "20", fixture, policy, "worker.exe"));
        Assert.Throws<ReleaseException>(() => PrepareBuild.RequireWorkerMetadata(metadata, "21", !fixture, policy, "worker.exe"));
        Assert.Throws<ReleaseException>(() => PrepareBuild.RequireWorkerMetadata(metadata, "21", fixture, policy == null ? "P6-DEVICE:safe-v4" : null, "worker.exe"));
    }

    [Fact]
    public void StageRefusesGrowthInUndocumentedParameters()
    {
        using var tree = VersionTree();
        foreach (var path in new[] { DeliveryRules.PolicyPath, "manifest/package-manifest.json", "manifest/tools-list.json",
                     "src/Logic/ModelContextProtocol/ToolProfiles.resx" }) tree.Copy(path);
        var context = Context(tree.Root);
        var roster = JsonNode.Parse(File.ReadAllText(Path.Combine(tree.Root, "manifest/tools-list.json")))!;
        int previous = (int)roster["callDiscipline"]!["parametersUndocumented"]!;
        roster["callDiscipline"]!["parametersUndocumented"] = previous + 1;
        bool matrixInvoked = false;
        Assert.Equal($"Parameters without a [Description] went up from {previous} to {previous + 1}",
            Assert.Throws<ReleaseException>(() => Prepare.Stage(context, [], catalogProducer: (exe, args, root, env) =>
            {
                File.WriteAllText(Path.Combine(root, "manifest/tools-list.json"), roster.ToJsonString());
                return new(0, "catalog", "");
            }, matrixGenerator: (exe, args, root, env) =>
            {
                matrixInvoked = true;
                return new(0, "unexpected generator", "");
            })).Message);
        Assert.False(matrixInvoked);
    }

    [Fact]
    public void GeneratedInventoryUsesTheUpdaterDataMemberNames()
    {
        using var tree = ManifestTree();
        var context = Context(tree.Root);
        Prepare.GenerateManifests(context);
        var source = File.ReadAllText(Path.Combine(StaticCheckVectorTests.Root, "src/Updater/Updater.cs"));
        var record = Regex.Match(source, @"class InventoryRecord\s*\{(?<body>.*?)\n\s*\}", RegexOptions.Singleline).Groups["body"].Value;
        string Name(string property) => Regex.Match(record, "\\[DataMember\\(Name = \"([^\"]+)\"\\)\\] public [^\\r\\n]+ " + property + " \\{").Groups[1].Value;
        var filesName = Name("Files");
        var pathName = Name("Path");
        var hashName = Name("Sha256");
        Assert.Equal("files", filesName);
        Assert.Equal("path", pathName);
        Assert.Equal("sha256", hashName);
        var inventory = JsonNode.Parse(File.ReadAllText(Path.Combine(context.StageDirectory, Prepare.InventoryPath)))!;
        Assert.NotEmpty(inventory[filesName]!.AsArray());
        Assert.All(inventory[filesName]!.AsArray(), row =>
        {
            Assert.True(File.Exists(Path.Combine(context.StageDirectory, (string)row![pathName]!)));
            Assert.Equal(ReleaseRecords.HashFile(Path.Combine(context.StageDirectory, (string)row[pathName]!)), (string?)row[hashName]);
        });
    }

    private static CommandResult SuccessfulProbe(string exe, IEnumerable<string> args, string root, IDictionary<string, string?>? environment) => new(0, "dependencies available", "");

    private static Options ContextOptions(string root, string python) => Options.Parse(["-Version", "4.0.0-beta.3",
        "-Python", Path.Combine(root, python), "-CompanionPython", Path.Combine(root, "python.exe"),
        "-NuGetConfig", Path.Combine(root, "nuget.config")], new HashSet<string>());

    private static StaticTree ContextTree()
    {
        var tree = VersionTree();
        foreach (var source in new[] { PrereleaseNotes.LedgerPath, PrereleaseNotes.ChecklistPath }) tree.Copy(source);
        tree.Write(PrereleaseNotes.NotesPath("4.0.0-beta.3"), PrereleaseNotes.Generate(
            File.ReadAllText(Path.Combine(tree.Root, PrereleaseNotes.LedgerPath)), File.ReadAllText(Path.Combine(tree.Root, PrereleaseNotes.ChecklistPath))));
        tree.Write("python.exe", "interpreter existence fixture; process probe is injected");
        tree.Write(".gitignore", "bin-build/\n");
        tree.Write("nuget.config", "<configuration><packageSources><clear /></packageSources></configuration>");
        foreach (var key in PlcWorkerFixtures.Keys)
        {
            tree.Write($"src/Adapters/{PrepareBuild.AdapterFolder(key)}/Release.props", "<Project><ItemGroup><AdapterApi Include=\"$(SiemensEngineeringDirectory)/Siemens.Engineering.dll\" /></ItemGroup></Project>");
            var file = tree.Write("sdk/" + ReleaseCommands.PlcWorkerApi(key).Directory + "/Siemens.Engineering.dll", "");
            File.Copy(typeof(Prepare).Assembly.Location, file, true);
        }
        return tree;
    }

    private static string Git(StaticTree tree, string[] arguments, string? input = null)
    {
        var result = ProcessRunner.Run("git", arguments, tree.Root, standardInput: input);
        Assert.True(result.ExitCode == 0, result.StandardError);
        return result.StandardOutput.Trim();
    }

    // A real, tiny Git repository without invoking git add/commit or touching the
    // worktree's Git metadata. Low-level objects provide its clean HEAD fixture.
    private static void SealRepository(StaticTree tree)
    {
        var files = Directory.GetFiles(tree.Root, "*", SearchOption.AllDirectories);
        Git(tree, ["init", "--quiet", "--initial-branch=master"]);
        Git(tree, ["config", "core.autocrlf", "false"]);
        foreach (var file in files)
        {
            var relative = Path.GetRelativePath(tree.Root, file).Replace('\\', '/');
            var hash = Git(tree, ["hash-object", "-w", "--", relative]);
            Git(tree, ["update-index", "--add", "--cacheinfo", "100644," + hash + "," + relative]);
        }
        var treeId = Git(tree, ["write-tree"]);
        var commit = Git(tree, ["hash-object", "-t", "commit", "-w", "--stdin"],
            $"tree {treeId}\nauthor Fixture <fixture@example.invalid> 1 +0000\ncommitter Fixture <fixture@example.invalid> 1 +0000\n\nfixture\n");
        Git(tree, ["update-ref", "refs/heads/master", commit]);
        // Git marks loose objects read-only on Windows; this fixture owns this
        // repository and must leave it removable by StaticTree's cleanup.
        PrepareBuild.RequireOwnedTree(StaticCheckVectorTests.Root, tree.Root);
        foreach (var path in Directory.EnumerateFiles(Path.Combine(tree.Root, ".git"), "*", SearchOption.AllDirectories))
            File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly);
    }

    private static StaticTree VersionTree()
    {
        var tree = new StaticTree();
        tree.Write("Version.props", "<Project><PropertyGroup><TiaMcpRelease>4.0.0</TiaMcpRelease><TiaMcpPrerelease>beta.3</TiaMcpPrerelease></PropertyGroup></Project>");
        tree.Write(".claude-plugin/plugin.json", "{\"version\":\"4.0.0-beta.3\"}");
        tree.Write(".claude-plugin/marketplace.json", "{\"plugins\":[{\"version\":\"4.0.0-beta.3\"}]}");
        tree.Write("CHANGELOG.md", "## [Unreleased]\n\n## [4.0.0-beta.3] - 2026-11-02\n\n## [3.0.0] - 2025-11-02\n");
        return tree;
    }

    private static PrepareContext Context(string root) => new(root, "4.0.0-beta.3", Path.Combine(root, "sdk"), null,
        "python.exe", "companion.exe", Path.Combine(root, "bin-build/prepared/4.0.0-beta.3"), new(2026, 11, 2));

    private static StaticTree ManifestTree()
    {
        var tree = VersionTree();
        tree.Copy("manifest/package-manifest.json");
        tree.Copy("manifest/tools-list.json");
        tree.Copy("src/Logic/ModelContextProtocol/ToolProfiles.resx");
        var context = Context(tree.Root);
        tree.Write(Path.GetRelativePath(tree.Root, Path.Combine(context.StageDirectory, "manifest/tools-list.json")),
            File.ReadAllText(Path.Combine(tree.Root, "manifest/tools-list.json")));
        return tree;
    }
}
