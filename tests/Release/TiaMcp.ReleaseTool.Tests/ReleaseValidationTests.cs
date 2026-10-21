using TiaMcp.ReleaseTool;
using TiaMcp.BuildCommon;
using System.Text.Json;
using Xunit;

namespace TiaMcp.ReleaseTool.Tests;

public sealed class ReleaseValidationTests
{
    [Fact]
    [Trait("Category", "Pipeline")]
    public void EngineCapturesKeepPrivateProductionAndSdkWorkersWithBothHostLayouts()
    {
        using var tree = new StaticTree();
        Directory.CreateDirectory(Path.Combine(tree.Root, "TIA_V20_PublicAPI/V20"));
        Directory.CreateDirectory(Path.Combine(tree.Root, "TIA_V21_PublicAPI/V21/net48"));
        var root = Repository.FindRoot();
        var captures = new List<string[]>();
        var steps = new HashSet<string> { "06-v4-contracts-capture", "07-v4-contracts-http", "07-engine-contracts-source-layout",
            "08-v4-responses-capture", "09-v4-responses-http", "09-engine-responses-source-layout" };
        ReleaseCommands.RunCandidateChecks(new("quick", ["engine-contracts", "engine-responses"], [], []),
            tree.Root, tree.Root, tree.Root, tree.Root, "dotnet-double", "python-double", 1,
            (name, action) => { if (steps.Contains(name)) action(); },
            (_, args) => Assert.Equal("compare", args[0]),
            (script, args) =>
            {
                Assert.Equal("capture", args[0]);
                Assert.DoesNotContain("--plc-worker", args);
                var release = args[Array.IndexOf(args, "--releases") + 1];
                var worker = args[Array.IndexOf(args, "--engine-worker") + 1].Split('=', 2);
                var catalog = args[Array.IndexOf(args, "--engine-catalog") + 1].Split('=', 2);
                Assert.Equal(release, worker[0]); Assert.Equal(release, catalog[0]);
                Assert.StartsWith(Path.GetFullPath(Path.Combine(root, "bin-build/engine-compat")), Path.GetFullPath(worker[1]));
                Assert.Equal(Path.Combine(Path.GetDirectoryName(worker[1])!, "tool-catalog.json"), catalog[1]);
                var output = args[Array.IndexOf(args, "--output") + 1];
                bool source = output.Contains("source-layout", StringComparison.Ordinal);
                var host = args[Array.IndexOf(args, "--engine-host") + 1];
                Assert.Equal(source ? Path.Combine(root, "src/FoundationHost/bin/Release/net10.0/TiaMcp.FoundationHost.exe")
                    : Path.Combine(Path.GetDirectoryName(ReleaseCommands.EngineCompatibilityDirectory(root, release))!, "TiaMcp.FoundationHost.exe"), host);
                if (!source && script.Contains("Contracts", StringComparison.Ordinal))
                    Assert.Equal(Path.Combine(ReleaseCommands.EngineCompatibilityDirectory(root, release), $"TiaMcp.Engine.V{release}.exe"), worker[1]);
                else Assert.Contains("candidate", worker[1]);
                Directory.CreateDirectory(output);
                File.WriteAllText(Path.Combine(output, release + ".json"), "{}");
                captures.Add(args);
            },
            (name, executable, args, _, _) =>
            {
                Assert.DoesNotContain("sdk-worker-build-plc", name);
                if (executable == "dotnet-double")
                {
                    Assert.Contains("-p:TiaMcpEngineWorkerSdkFixture=true", args);
                    Assert.Contains(args, arg => arg.StartsWith("-p:OutputPath=", StringComparison.Ordinal) && arg.Contains("engine-compat", StringComparison.Ordinal));
                }
                return new(0, "", "");
            });
        Assert.Equal(12, captures.Count);
        foreach (var key in new[] { "20", "21" })
            Assert.Equal(6, captures.Count(args => args[Array.IndexOf(args, "--releases") + 1] == key));
    }

    [Fact]
    [Trait("Category", "Pipeline")]
    public void CandidateRetainsEngineStepsAndAddsBothPlcCaptureMatrices()
    {
        using var tree = new StaticTree();
        Directory.CreateDirectory(Path.Combine(tree.Root, "TIA_V20_PublicAPI/V20"));
        Directory.CreateDirectory(Path.Combine(tree.Root, "TIA_V21_PublicAPI/V21/net48"));
        var root = Repository.FindRoot();
        var steps = new List<string>(); var builds = new List<string>(); var captures = new List<string>(); var comparisons = new List<string>();
        ReleaseCommands.RunCandidateChecks(new("quick", ["engine-contracts", "engine-responses", "plc-contracts", "plc-responses"], [], []),
            tree.Root, tree.Root, tree.Root, tree.Root, "dotnet-double", "python-double", 1,
            (name, action) => { steps.Add(name); if (name is "plc-contracts" or "plc-responses") action(); },
            (script, args) =>
            {
                Assert.Equal("compare", args[0]); Assert.Contains("--baseline", args);
                comparisons.Add(args[Array.IndexOf(args, "--current") + 1]);
            }, (_, _) => throw new InvalidOperationException("Unexpected engine capture action"),
            (name, executable, args, defaults, cwd) =>
            {
                if (executable == "dotnet-double")
                {
                    builds.Add(name);
                    if (name.StartsWith("sdk-worker-build-plc-v", StringComparison.Ordinal))
                    {
                        Assert.Contains("-p:TiaMcpPlcWorkerSdkFixture=true", args);
                        Assert.Contains(args, arg => arg.StartsWith("-p:TiaMcpPlcWorkerFixtureRoot=", StringComparison.Ordinal));
                        Assert.DoesNotContain("-p:TiaMcpEngineWorkerSdkFixture=true", args);
                    }
                }
                if (executable == "python-double")
                {
                    var arguments = args.ToArray(); Assert.Equal("capture", arguments[1]);
                    Assert.Contains("--plc-worker", arguments); Assert.DoesNotContain("--engine-worker", arguments);
                    var output = arguments[Array.IndexOf(arguments, "--output") + 1];
                    var key = arguments[Array.IndexOf(arguments, "--releases") + 1];
                    var kind = arguments[0].Contains("Contracts", StringComparison.Ordinal) ? "baseline" : "responses";
                    Directory.CreateDirectory(output);
                    File.Copy(Path.Combine(root, "manifest/contracts/v4", kind, key + ".json"), Path.Combine(output, key + ".json"));
                    captures.Add(name);
                }
                return new(0, "", "");
            });
        Assert.Equal(2, builds.Count(name => name.StartsWith("sdk-worker-build-plc-v", StringComparison.Ordinal)));
        Assert.Contains("sdk-worker-build", builds);
        Assert.Equal(16, captures.Count); Assert.Equal(16, comparisons.Count);
        Assert.Contains("07-engine-contracts-source-layout", steps);
        Assert.Contains("09-engine-responses-source-layout", steps);
        Assert.Contains("09-packaged-responses-verify", steps);
        Assert.Contains("09-packaged-responses-verify-plc", steps);
    }

    [Fact]
    public void PackagedPlcResponsesUseTheExtractedProductionWorkersAndTheUnchangedVerifier()
    {
        using var tree = new StaticTree();
        foreach (var key in new[] { "20", "21" })
            tree.Write($"source/candidate/runtime/v{key}/worker/TiaMcp.PlcWorker.{key}.exe", "packaged-production-" + key);
        var zip = Path.Combine(tree.Root, "candidate.zip");
        System.IO.Compression.ZipFile.CreateFromDirectory(Path.Combine(tree.Root, "source"), zip);
        tree.Write("package-result.json", JsonSerializer.Serialize(new { path = zip }));
        var verified = false; string? extracted = null;
        ReleaseCommands.VerifyPackagedResponses(Repository.FindRoot(), tree.Root, tree.Root, tree.Root, (_, args) =>
        {
            Assert.Contains("--packaged-no-tia", args); Assert.DoesNotContain("--engine-worker", args);
            extracted = args[Array.IndexOf(args, "--repo-root") + 1];
            // Maintainer decision 2026-10-21 (P8-04j2 D-1b): the -plc step starts each extracted host with its default worker,
            // the packaged production PlcWorker; --plc-worker would synthesize an SDK-only TIA installation.
            Assert.DoesNotContain("--plc-worker", args);
            foreach (var key in new[] { "20", "21" })
                Assert.Equal("packaged-production-" + key, File.ReadAllText(Path.Combine(extracted, $"runtime/v{key}/worker/TiaMcp.PlcWorker.{key}.exe")));
            var responses = args[Array.IndexOf(args, "--output") + 1]; Directory.CreateDirectory(responses);
            foreach (var key in new[] { "20", "21" }) File.WriteAllText(Path.Combine(responses, key + ".json"), "{}");
        }, (_, args) =>
        {
            Assert.Equal("verify", args[0]);
            Assert.Equal(8, Directory.GetFiles(args[Array.IndexOf(args, "--responses") + 1], "*.json").Length);
            verified = true;
        }, plc: true);
        Assert.True(verified); Assert.NotNull(extracted); Assert.False(Directory.Exists(extracted));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CandidateVerifiesNoTiaResponsesFromTheActualPackage(bool failVerify)
    {
        using var fixture = new TiaMcp.BuildCommon.OfflineFixtures();
        var directory = fixture.DirectoryPath;
        Directory.CreateDirectory(Path.Combine(directory, "TIA_V20_PublicAPI/V20"));
        Directory.CreateDirectory(Path.Combine(directory, "TIA_V21_PublicAPI/V21/net48"));
        var source = Path.Combine(directory, "source/candidate"); Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "branch-marker.txt"), "candidate from this branch");
        var zip = Path.Combine(directory, "candidate.zip");
        System.IO.Compression.ZipFile.CreateFromDirectory(Path.GetDirectoryName(source)!, zip);
        File.WriteAllText(Path.Combine(directory, "package-result.json"), JsonSerializer.Serialize(new { path = zip }));
        var steps = new List<string>();
        string? bundle = null;
        var verified = false;
        void Run()
        {
            ReleaseCommands.RunCandidateChecks(new("quick", ["engine-responses"], [], []), directory, directory, directory,
                directory, "dotnet-test-double", "python-test-double", 1,
                (name, action) => { steps.Add(name); if (name == "09-packaged-responses-verify") action(); },
                (script, args) =>
                {
                    Assert.Equal("scripts/checks/Snapshot-ToolResponses.py", script);
                    Assert.Equal("verify", args[0]);
                    var responses = args[Array.IndexOf(args, "--responses") + 1];
                    Assert.Equal(8, Directory.GetFiles(responses, "*.json").Length);
                    using var snapshot = JsonDocument.Parse(File.ReadAllText(Path.Combine(responses, "20.json")));
                    Assert.Equal("packaged-no-tia", snapshot.RootElement.GetProperty("captureMode").GetString());
                    verified = true;
                    if (failVerify) throw new ReleaseException("Synthetic TIA dependency");
                },
                (script, args) =>
                {
                    Assert.Equal("scripts/checks/Snapshot-ToolResponses.py", script);
                    Assert.Equal("capture", args[0]); Assert.Contains("--packaged-no-tia", args);
                    Assert.Equal(new[] { "20", "21" }, args.Skip(Array.IndexOf(args, "--releases") + 1).Take(2));
                    bundle = args[Array.IndexOf(args, "--repo-root") + 1];
                    Assert.Equal("candidate from this branch", File.ReadAllText(Path.Combine(bundle, "branch-marker.txt")));
                    Assert.Equal(Path.Combine(bundle, "runtime/dotnet"), args[Array.IndexOf(args, "--dotnet-root") + 1]);
                    Assert.DoesNotContain("--engine-worker", args);
                    var responses = args[Array.IndexOf(args, "--output") + 1]; Directory.CreateDirectory(responses);
                    foreach (var release in new[] { "20", "21" }) File.WriteAllText(Path.Combine(responses, release + ".json"), "{\"captureMode\":\"packaged-no-tia\"}");
                },
                (_, _, _, _, _) => new CommandResult(0, "", ""));
        }
        if (failVerify) Assert.Throws<ReleaseException>(Run); else Run();
        Assert.True(verified);
        Assert.NotNull(bundle); Assert.False(Directory.Exists(bundle));
        foreach (var step in new[] { "08-v4-responses-capture", "09-v4-responses-compare", "09-v4-responses-http",
            "09-engine-responses-source-layout", "09-packaged-responses-verify" }) Assert.Contains(step, steps);
    }

    [Theory]
    [Trait("Category", "Pipeline")]
    [InlineData("none", "aggregate")]
    [InlineData("none", "v20-log")]
    [InlineData("none", "v21-log")]
    [InlineData("v20", "aggregate")]
    [InlineData("v20", "v20-log")]
    [InlineData("v20", "v21-log")]
    [InlineData("both", "aggregate")]
    [InlineData("both", "v20-log")]
    [InlineData("both", "v21-log")]
    public void ParallelPipelinePreservesLegacyAggregateAndPerVersionLogs(string mode, string assertion)
    {
        var directory = Path.Combine(Path.GetTempPath(), "release-pipeline-parity-" + Guid.NewGuid().ToString("N"));
        try
        {
            var shouldFail20 = mode is "v20" or "both";
            var shouldFail21 = mode == "both";
            ReleaseException? failure = null;
            try
            {
                _ = ParallelPipeline.Run(
                    [("release-v20", () => new PipelineResult("release-v20", shouldFail20 ? 7 : 0, "V20 passed", shouldFail20 ? "Synthetic V20 failure" : "")),
                     ("release-v21", () => new PipelineResult("release-v21", shouldFail21 ? 8 : 0, "V21 passed", shouldFail21 ? "Synthetic V21 failure" : ""))], directory);
            }
            catch (ReleaseException ex) { failure = ex; }

            if (assertion == "aggregate")
            {
                if (mode == "none") Assert.Null(failure);
                else
                {
                    Assert.NotNull(failure);
                    if (shouldFail20) Assert.Contains("release-v20", failure!.Message);
                    var v21Log = File.ReadAllText(Path.Combine(directory, "release-v21.log"));
                    if (shouldFail21 && !v21Log.Contains("Not started", StringComparison.Ordinal)) Assert.Contains("release-v21", failure!.Message);
                }
            }
            else
            {
                var major = assertion == "v20-log" ? "20" : "21";
                var expectedText = "V" + major + " passed";
                var log = File.ReadAllText(Path.Combine(directory, "release-v" + major + ".log"));
                Assert.True(log.Contains(expectedText, StringComparison.Ordinal) || (major == "21" && log.Contains("Not started", StringComparison.Ordinal)));
            }
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Theory]
    [Trait("Category", "BundleParity")]
    [InlineData("3.3.0", "3.3.0", false, false, true)]
    [InlineData("4.0.0", "3.3.0", true, true, true)]
    [InlineData("4.0.0", "3.3.0", true, false, false)]
    [InlineData("4.0.0", "3.3.0", false, true, false)]
    [InlineData("3.2.0", "3.3.0", true, true, false)]
    [InlineData("3.10.0", "3.9.0", true, true, true)]
    [InlineData("invalid", "3.3.0", true, true, false)]
    public void ChangelogVersionKeepsRepositoryAndPackageRules(string newest, string released, bool repositoryOnly, bool noteExists, bool expected) =>
        Assert.Equal(expected, ReleaseValidation.ChangelogVersion(newest, released, repositoryOnly, noteExists));

    [Fact]
    [Trait("Category", "BundleParity")]
    public void GeneratedUpdaterAndWriteGuardResourcesAreAcceptedOnlyForRepositoryBuilds()
    {
        var root = Path.Combine(Path.GetTempPath(), "release-generated-resources-" + Guid.NewGuid().ToString("N"));
        try
        {
            foreach (var relative in BundleManifestRequirements.BundleResourcePaths.Where(path =>
                         path is not "runtime/tools/TiaMcp.WriteGuard.exe" and not "runtime/tools/TiaMcp.Updater.exe"))
            {
                var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
                if (Path.HasExtension(path))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    File.WriteAllText(path, "resource");
                }
                else Directory.CreateDirectory(path);
            }

            var skipped = BundleManifestRequirements.MissingBundleResources(root, package: false, noBinaries: true);
            Assert.DoesNotContain("runtime/tools/TiaMcp.WriteGuard.exe", skipped);
            Assert.DoesNotContain("runtime/tools/TiaMcp.Updater.exe", skipped);

            var updater = Path.Combine(root, "bin-build/updater/TiaMcp.Updater.exe");
            var guard = Path.Combine(root, "src/Tools/WriteGuard/bin/Release/net10.0/TiaMcp.WriteGuard.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(updater)!);
            Directory.CreateDirectory(Path.GetDirectoryName(guard)!);
            File.WriteAllText(updater, "updater");
            File.WriteAllText(guard, "write guard");
            Assert.Empty(BundleManifestRequirements.MissingBundleResources(root, package: false, noBinaries: false));

            var packageMissing = BundleManifestRequirements.MissingBundleResources(root, package: true, noBinaries: false);
            Assert.Contains("runtime/tools/TiaMcp.WriteGuard.exe", packageMissing);
            Assert.Contains("runtime/tools/TiaMcp.Updater.exe", packageMissing);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Theory]
    [Trait("Category", "ReviewerChain")]
    [InlineData("<configuration><packageSources><clear /></packageSources></configuration>", null)]
    [InlineData("<configuration><packageSources><clear /><add key='cache' value='C:/nuget' /></packageSources></configuration>", null)]
    [InlineData("<configuration><packageSources><add key='feed' value='https://example.invalid' /></packageSources></configuration>", "NuGetConfig must clear inherited package feeds")]
    [InlineData("<configuration><packageSources><clear /><add key='feed' value='https://example.invalid' /></packageSources></configuration>", "Network NuGet feed is forbidden: https://example.invalid")]
    [InlineData("<configuration><packageSources><clear /><add key='feed' value='file://server/feed' /></packageSources></configuration>", "Network NuGet feed is forbidden: file://server/feed")]
    [InlineData("<configuration><packageSources><clear /><add key='feed' value='//server/share' /></packageSources></configuration>", "Network NuGet feed is forbidden: //server/share")]
    [InlineData("<configuration><packageSources><clear /><add key='feed' value='\\\\server\\share' /></packageSources></configuration>", "Network NuGet feed is forbidden: \\\\server\\share")]
    public void OfflineNugetRejectsNetworkFeeds(string contents, string? expected)
    {
        var path = Path.Combine(Path.GetTempPath(), "nuget-" + Guid.NewGuid().ToString("N") + ".config");
        try
        {
            File.WriteAllText(path, contents);
            Assert.Equal(expected, ReleaseValidation.OfflineNuGetError(path));
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [Trait("Category", "ReviewerChain")]
    [InlineData("delivery.zip", "delivery")]
    [InlineData("delivery.v4.zip", "delivery.v4")]
    [InlineData("delivery with spaces.zip", "delivery with spaces")]
    public void BundleDirectoryDropsOnlyZipExtension(string name, string expected)
    {
        var path = Path.Combine(Path.GetTempPath(), name);
        Assert.Equal(Path.Combine(Path.GetTempPath(), expected), ReleaseValidation.GetBundleDirectory(path));
    }

    [Fact]
    [Trait("Category", "ReviewerChain")]
    public void NonZipPackageResultIsRejected() =>
        Assert.Throws<ReleaseException>(() => ReleaseValidation.GetBundleDirectory("package.tar"));

    [Fact]
    [Trait("Category", "ReviewerChain")]
    public void ReleaseReviewStepsKeepTheirOrder()
    {
        var names = new[] { "00-preflight", "01-multi-version", "02-build-release", "03-package-local", "04-validate-bundle", "05-prompt-registration", "06-v4-contracts-capture", "07-v4-contracts-compare", "08-v4-responses-capture", "09-v4-responses-compare", "10-relocated-bundle" };
        Assert.Null(ReleaseValidation.AssertStepOrder(names));
    }

    [Fact]
    [Trait("Category", "ReviewerChain")]
    public void ReorderedReleaseReviewStepsAreRejected()
    {
        var names = new[] { "00-preflight", "02-build-release", "01-multi-version", "03-package-local", "04-validate-bundle", "05-prompt-registration", "06-v4-contracts-capture", "07-v4-contracts-compare", "08-v4-responses-capture", "09-v4-responses-compare", "10-relocated-bundle" };
        Assert.NotNull(ReleaseValidation.AssertStepOrder(names));
    }

    [Theory]
    [Trait("Category", "ReviewerChain")]
    [InlineData(false, "enabled=false\ntimeoutSeconds=120\n")]
    [InlineData(true, null)]
    public void HostChecksDisableApprovalsWhileSnapshotsUseProductDefaults(bool productDefaults, string? expected) =>
        Assert.Equal(expected, ReleaseValidation.HostApprovalSettings(productDefaults));

    [Theory]
    [InlineData(" M manifest/release-build.json", true)]
    [InlineData(" M Version.props", true)]
    [InlineData(" M build-tools/release/ReleaseCommands.cs", false)]
    [InlineData("?? manifest/new.json", false)]
    [InlineData(" M manifest/history/contracts-v3/baseline/21.json", false)]
    public void ManagedReleaseChangesRemainNarrow(string status, bool expected) =>
        Assert.Equal(expected, ReleaseValidation.IsReleaseManagedChange(status));

    [Fact]
    [Trait("Category", "ReleaseParity")]
    public void SourceHashesNormalizeLineEndings()
    {
        var directory = Path.Combine(Path.GetTempPath(), "release-hash-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "source.cs");
        try
        {
            File.WriteAllText(path, "source\r\n");
            var crlf = ReleaseValidation.SourceHash(path);
            File.WriteAllText(path, "source\n");
            Assert.Equal(crlf, ReleaseValidation.SourceHash(path));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    [Trait("Category", "ReleaseParity")]
    public void FontHashesUseRawBytes()
    {
        var directory = Path.Combine(Path.GetTempPath(), "release-font-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "font.ttf");
        try
        {
            var bytes = new byte[] { 255, 0, 13, 10, 128 };
            File.WriteAllBytes(path, bytes);
            Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)), ReleaseValidation.SourceHash(path));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    [Trait("Category", "ReleaseParity")]
    public void BuildOutputDirectoriesAreExcludedFromSourceInputs()
    {
        var directory = Path.Combine(Path.GetTempPath(), "release-sources-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(directory, "src/Engine/bin"));
        try
        {
            File.WriteAllText(Path.Combine(directory, "Version.props"), "<Project />");
            File.WriteAllText(Path.Combine(directory, "src/Engine/included.cs"), "source");
            File.WriteAllText(Path.Combine(directory, "src/Engine/bin/ignored.cs"), "generated");
            var paths = ReleaseRecords.GetSources(directory, "engine").Select(row => row.Path).ToArray();
            Assert.Contains("src/Engine/included.cs", paths);
            Assert.DoesNotContain("src/Engine/bin/ignored.cs", paths);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Theory]
    [Trait("Category", "ReleaseParity")]
    [InlineData("release", "release/fileVersion changed")]
    [InlineData("source-count", "empty source or binary inventory")]
    [InlineData("source-content", "source changed: input.cs")]
    [InlineData("missing-binary", "binary missing: runtime/missing.exe")]
    [InlineData("changed-binary", "binary changed: runtime/app.exe")]
    [InlineData("duplicate-source", "invalid/duplicate source inventory")]
    [InlineData("duplicate-binary", "invalid/duplicate binary inventory")]
    [InlineData("escaped-binary", "invalid/duplicate binary inventory")]
    public void BuildReuseRejectsChangedInputsAndOutputs(string mutation, string expected)
    {
        var root = Path.Combine(Path.GetTempPath(), "release-reuse-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "runtime"));
        var sourcePath = Path.Combine(root, "input.cs");
        var binaryPath = Path.Combine(root, "runtime/app.exe");
        File.WriteAllText(sourcePath, "source");
        File.WriteAllText(binaryPath, "binary");
        try
        {
            var sources = new[] { new ReleaseArtifact("input.cs", ReleaseValidation.SourceHash(sourcePath)) };
            var sourceRows = sources.Select(row => new { path = row.Path, sha256 = row.Sha256 }).ToArray();
            var binaryRows = new[] { new { path = "runtime/app.exe", sha256 = ReleaseRecords.HashFile(binaryPath) } };
            var release = mutation == "release" ? "4.0.1" : "4.0.0";
            object[] sourceRowsForRecord = mutation == "duplicate-source" ? [sourceRows[0], sourceRows[0]] : sourceRows;
            if (mutation == "source-count") sourceRowsForRecord = [];
            if (mutation == "source-content") sourceRowsForRecord = [new { path = "input.cs", sha256 = "changed" }];
            object[] binaryRowsForRecord = mutation == "duplicate-binary" ? [binaryRows[0], binaryRows[0]] : binaryRows;
            if (mutation == "escaped-binary") binaryRowsForRecord = [new { path = "../outside.exe", sha256 = "changed" }];
            if (mutation == "missing-binary") binaryRowsForRecord = [new { path = "runtime/missing.exe", sha256 = "changed" }];
            if (mutation == "changed-binary") File.WriteAllText(binaryPath, "changed");
            using var record = JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(new
            {
                release, fileVersion = release + ".0", sourceFiles = sourceRowsForRecord,
                runtimeFiles = binaryRowsForRecord
            }));
            Assert.Equal(expected, ReleaseRecords.ReuseReason(root, record.RootElement, "4.0.0", sources, "runtimeFiles"));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    [Trait("Category", "ReleaseParity")]
    public void MatchingBuildCanBeReusedWithEitherRecordInventoryKey()
    {
        var root = Path.Combine(Path.GetTempPath(), "release-reuse-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var binaryPath = Path.Combine(root, "engine.exe");
        File.WriteAllText(binaryPath, "engine");
        try
        {
            var sources = new[] { new ReleaseArtifact("source.cs", "source-hash") };
            foreach (var property in new[] { "runtimeFiles", "files" })
            {
                using var record = JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(new
                {
                    release = "4.0.0", fileVersion = "4.0.0.0", sourceFiles = new[] { new { path = sources[0].Path, sha256 = sources[0].Sha256 } },
                    runtimeFiles = new[] { new { path = "engine.exe", sha256 = ReleaseRecords.HashFile(binaryPath) } },
                    files = new[] { new { path = "engine.exe", sha256 = ReleaseRecords.HashFile(binaryPath) } }
                }));
                Assert.Equal("", ReleaseRecords.ReuseReason(root, record.RootElement, "4.0.0", sources, property));
            }
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    [Trait("Category", "ReleaseParity")]
    public void PreviousReleaseOutputIsArchivedBesideItsOriginal()
    {
        var root = Path.Combine(Path.GetTempPath(), "release-archive-" + Guid.NewGuid().ToString("N"));
        var prior = Path.Combine(root, "bin-build/releases/v4.0.0");
        Directory.CreateDirectory(prior);
        File.WriteAllText(Path.Combine(prior, "evidence.txt"), "preserved");
        try
        {
            var archive = ReleaseRecords.MovePreviousReleaseOutput(root, "4.0.0");
            Assert.NotNull(archive);
            Assert.False(Directory.Exists(prior));
            Assert.Equal("preserved", File.ReadAllText(Path.Combine(archive!, "evidence.txt")));
            Assert.Equal(Path.GetFullPath(Path.Combine(root, "bin-build/releases")), Path.GetDirectoryName(archive));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    [Trait("Category", "ReleaseParity")]
    public void RuntimePreparationFailsClearlyWhenBuildProductsAreMissing()
    {
        var root = Path.Combine(Path.GetTempPath(), "release-prep-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var error = Assert.Throws<ReleaseException>(() => ReleaseRecords.AssertRuntimePreparation(root));
            Assert.Contains("build-multi-version -PrepareOnly -Test", error.Message);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void HostChecksAndGeneratorsHaveRecordBindings()
    {
        Assert.NotEmpty(ReleaseCommandTable.Entries);
        Assert.Equal(ReleaseCommandTable.Entries.Count, ReleaseCommandTable.Entries.Select(row => row.Name).Distinct(StringComparer.Ordinal).Count());
        Assert.All(ReleaseCommandTable.Entries, row =>
        {
            Assert.False(string.IsNullOrWhiteSpace(row.Command));
            Assert.False(string.IsNullOrWhiteSpace(row.LogFile));
            Assert.False(string.IsNullOrWhiteSpace(row.CountRule));
            Assert.False(string.IsNullOrWhiteSpace(row.RecordKey));
        });
        foreach (var job in ReleaseCommands.PlcReleaseJobs("worktree", "sdk", "logs", "temp", "dotnet", "python").Where(job => !job.Name.StartsWith("harness-", StringComparison.Ordinal)))
        {
            var binding = ReleaseCommandTable.Get(job.Name);
            Assert.Equal(job.Name + ".log", binding.LogFile);
            Assert.Equal("plcChecks." + job.Name, binding.RecordKey);
            Assert.Contains(Path.GetFileName(job.Arguments[0]), binding.Command);
        }
    }

    [Fact]
    public void ReleaseCommandTableKeepsHarnessEngineAndPlcBindingsAndD5Reasons()
    {
        var entries = ReleaseCommandTable.Entries.ToArray();
        foreach (var (engine, plc) in ReleaseCommands.CheckAllHarnessSuccessors())
        {
            int index = Array.FindIndex(entries, row => row.Name == engine);
            Assert.True(index >= 0); Assert.Equal(plc, entries[index + 1].Name);
            Assert.Contains("Test-PlcHarness.py", entries[index + 1].Command);
            Assert.Contains("--worker <SDK-ready-PlcWorker>", entries[index + 1].Command);
            Assert.Equal(plc + ".log", entries[index + 1].LogFile);
            Assert.Equal("harness." + plc, entries[index + 1].RecordKey);
        }
        foreach (var (engine, reason) in ReleaseCommands.CheckAllHarnessD5())
        {
            Assert.Contains(reason, ReleaseCommandTable.Get(engine).CountRule);
            Assert.DoesNotContain(entries, row => row.Name == engine + "-plc");
        }
        foreach (var (engine, reason) in ReleaseCommands.CheckAllHarnessD5Assertions())
            Assert.Contains(reason, ReleaseCommandTable.Get(engine).CountRule);
        Assert.Contains("build-solutions", entries.Select(row => row.Name));
        Assert.Equal("TiaMcp.Engine.Harness.exe engineering-api-only", ReleaseCommandTable.Get("engineering-api").Command);
    }

    [Fact]
    [Trait("Category", "Pipeline")]
    public void ParallelPipelinesOverlapAndWriteSeparateEvidence()
    {
        var directory = Path.Combine(Path.GetTempPath(), "release-pipeline-" + Guid.NewGuid().ToString("N"));
        var active = 0;
        var maximum = 0;
        PipelineResult Execute(string name)
        {
            var current = Interlocked.Increment(ref active);
            InterlockedExtensions.Max(ref maximum, current);
            Thread.Sleep(100);
            Interlocked.Decrement(ref active);
            return new PipelineResult(name, 0, name + " output", "");
        }
        try
        {
            var results = ParallelPipeline.Run(
                [("release-v20", () => Execute("release-v20")), ("release-v21", () => Execute("release-v21"))], directory);
            Assert.Equal(2, results.Count);
            Assert.True(maximum > 1, "Both version pipelines should overlap.");
            Assert.StartsWith("release-v20 output", File.ReadAllText(Path.Combine(directory, "release-v20.log")));
            Assert.StartsWith("release-v21 output", File.ReadAllText(Path.Combine(directory, "release-v21.log")));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    [Trait("Category", "Pipeline")]
    public void ParallelBuildReleasePipelinesStartTheirOwnDevelopmentHostCopies()
    {
        // A suite build in one pipeline rebuilds src/FoundationHost/bin/Release; the other pipeline's harness must not start that build (P8-90).
        var runTemp = Path.Combine(Path.GetTempPath(), "tmr-" + Guid.NewGuid().ToString("N")[..8], "br");
        var v20 = ReleaseCommands.PrivateDevelopmentHostPath(runTemp, 20);
        var v21 = ReleaseCommands.PrivateDevelopmentHostPath(runTemp, 21);
        Assert.NotEqual(v20, v21, StringComparer.OrdinalIgnoreCase);
        foreach (var (major, host) in new[] { (20, v20), (21, v21) })
        {
            Assert.Equal("TiaMcp.FoundationHost.exe", Path.GetFileName(host));
            Assert.StartsWith(ReleaseCommands.PipelineTempDirectory(runTemp, major) + Path.DirectorySeparatorChar, host, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(Path.Combine("src", "FoundationHost", "bin"), host, StringComparison.OrdinalIgnoreCase);
        }
        Assert.False(ReleaseCommands.PipelineTempDirectory(runTemp, 20).StartsWith(ReleaseCommands.PipelineTempDirectory(runTemp, 21), StringComparison.OrdinalIgnoreCase)
            || ReleaseCommands.PipelineTempDirectory(runTemp, 21).StartsWith(ReleaseCommands.PipelineTempDirectory(runTemp, 20), StringComparison.OrdinalIgnoreCase),
            "The two pipeline folders must not contain each other.");

        // Every harness step of a version pipeline starts that pipeline's copy; only the shipped-runtime checks after both pipelines use the shared build.
        var source = File.ReadAllText(Path.Combine(Repository.FindRoot(), "build-tools/release/BuildReleasePipeline.cs"));
        var start = source.IndexOf("private static object RunBuildReleaseMajor(", StringComparison.Ordinal);
        var end = source.IndexOf("private static void RunShippedRuntimeChecks(", StringComparison.Ordinal);
        Assert.True(start > 0 && end > start, "version pipeline source not found");
        var harnessSteps = System.Text.RegularExpressions.Regex.Matches(source[start..end], "RunBuildSpec\\(\"[^\"]+\", harness, [^;]*;").Select(match => match.Value).ToArray();
        Assert.True(harnessSteps.Length >= 14, "expected the version pipeline's harness steps, found " + harnessSteps.Length);
        Assert.All(harnessSteps, step => Assert.Contains("developmentHost: developmentHost", step, StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Category", "Pipeline")]
    public void ParallelPipelinesHonorSerialAndBoundedLimits()
    {
        var directory = Path.Combine(Path.GetTempPath(), "release-pipeline-limit-" + Guid.NewGuid().ToString("N"));
        var active = 0;
        var maximum = 0;
        PipelineResult Execute(string name)
        {
            var current = Interlocked.Increment(ref active);
            InterlockedExtensions.Max(ref maximum, current);
            Thread.Sleep(40);
            Interlocked.Decrement(ref active);
            return new PipelineResult(name, 0, name, "");
        }
        try
        {
            var jobs = Enumerable.Range(0, 6).Select(index => ($"job-{index}", (Func<PipelineResult>)(() => Execute($"job-{index}")))).ToArray();
            var serialResults = ParallelPipeline.Run(jobs, Path.Combine(directory, "serial"), maxParallelism: 1);
            Assert.Equal(1, maximum);
            maximum = 0;
            var boundedResults = ParallelPipeline.Run(jobs, Path.Combine(directory, "bounded"), maxParallelism: 2);
            Assert.InRange(maximum, 1, 2);
            Assert.Equal(serialResults.Select(result => (result.Name, result.ExitCode, result.StandardOutput, result.StandardError)),
                boundedResults.Select(result => (result.Name, result.ExitCode, result.StandardOutput, result.StandardError)));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    [Trait("Category", "Pipeline")]
    public void ParallelPipelineFailureStopsQueuedJobsAndWaitsForRunningJobs()
    {
        var directory = Path.Combine(Path.GetTempPath(), "release-pipeline-cancel-" + Guid.NewGuid().ToString("N"));
        var runningFinished = false;
        var queuedStarted = false;
        try
        {
        using var runningStarted = new ManualResetEventSlim();
        var error = Assert.Throws<ReleaseException>(() => ParallelPipeline.Run(
                [("failure", () => { Assert.True(runningStarted.Wait(TimeSpan.FromSeconds(5))); return new PipelineResult("failure", 7, "", "failed"); }),
                 ("running", () => { runningStarted.Set(); Thread.Sleep(80); runningFinished = true; return new PipelineResult("running", 0, "", ""); }),
                 ("queued", () => { queuedStarted = true; return new PipelineResult("queued", 0, "", ""); })], directory, maxParallelism: 2));
            Assert.Contains("failure", error.Message);
            Assert.True(runningFinished);
            Assert.False(queuedStarted);
            Assert.Contains("Not started", File.ReadAllText(Path.Combine(directory, "queued.log")));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    [Trait("Category", "Pipeline")]
    public void ParallelPipelinesReportEveryFailureAndRetainBothLogs()
    {
        var directory = Path.Combine(Path.GetTempPath(), "release-pipeline-" + Guid.NewGuid().ToString("N"));
        using var bothStarted = new CountdownEvent(2);
        PipelineResult FailAfterBothStart(string name, int exitCode)
        {
            bothStarted.Signal();
            Assert.True(bothStarted.Wait(TimeSpan.FromSeconds(5)));
            var version = name == "release-v20" ? "20" : "21";
            return new PipelineResult(name, exitCode, "v" + version, "failure " + version);
        }
        try
        {
            var error = Assert.Throws<ReleaseException>(() => ParallelPipeline.Run(
                [("release-v20", () => FailAfterBothStart("release-v20", 7)),
                 ("release-v21", () => FailAfterBothStart("release-v21", 8))], directory, maxParallelism: 2));
            Assert.Contains("release-v20", error.Message);
            Assert.Contains("release-v21", error.Message);
            Assert.StartsWith("v20failure 20", File.ReadAllText(Path.Combine(directory, "release-v20.log")));
            Assert.StartsWith("v21failure 21", File.ReadAllText(Path.Combine(directory, "release-v21.log")));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}

internal static class InterlockedExtensions
{
    internal static void Max(ref int location, int value)
    {
        int current;
        do { current = Volatile.Read(ref location); }
        while (current < value && Interlocked.CompareExchange(ref location, value, current) != current);
    }
}
