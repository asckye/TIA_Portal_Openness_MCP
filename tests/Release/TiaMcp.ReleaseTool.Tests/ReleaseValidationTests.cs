using TiaMcp.ReleaseTool;
using System.Text.Json;
using Xunit;

namespace TiaMcp.ReleaseTool.Tests;

public sealed class ReleaseValidationTests
{
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
