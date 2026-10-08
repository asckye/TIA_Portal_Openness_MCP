using System.Text.Json;
using TiaMcp.ReleaseTool;
using Xunit;

namespace TiaMcp.ReleaseTool.Tests;

public sealed class ReleaseRecordParityTests
{
    [Fact]
    [Trait("Category", "ReleaseParity")]
    public void EmptySourceHashMatchesSha256() =>
        Assert.Equal("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", HashText(""));

    [Fact]
    [Trait("Category", "ReleaseParity")]
    public void BuildStagesRunPrepareEnginesAndCompleteInOrder()
    {
        var events = new List<string>();
        ReleaseStageRunner.Invoke(new(
            () => events.Add("multi-prepare"),
            () => events.Add("engine"),
            () => events.Add("multi-complete")));
        Assert.Equal(["multi-prepare", "engine", "multi-complete"], events);
    }

    [Fact]
    [Trait("Category", "ReleaseParity")]
    public void FailedPreparationStopsLaterBuildStages()
    {
        var events = new List<string>();
        Assert.Throws<InvalidOperationException>(() => ReleaseStageRunner.Invoke(new(
            () => { events.Add("prepare"); throw new InvalidOperationException("synthetic preparation failure"); },
            () => events.Add("engine"),
            () => events.Add("complete"))));
        Assert.Equal(["prepare"], events);
    }

    [Theory]
    [Trait("Category", "ReleaseParity")]
    [InlineData(".ttf")]
    [InlineData(".otf")]
    [InlineData(".OTF")]
    public void FontSourceHashUsesRawBytes(string extension)
    {
        var path = TempFile("font" + extension);
        var bytes = new byte[] { 255, 0, 13, 10, 128 };
        try
        {
            File.WriteAllBytes(path, bytes);
            Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)), ReleaseValidation.SourceHash(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    [Trait("Category", "ReleaseParity")]
    public void SourceInventoriesTrackUpdaterWriteGuardAndSuiteInputs()
    {
        var root = Path.Combine(Path.GetTempPath(), "release-source-roots-" + Guid.NewGuid().ToString("N"));
        var expected = new[]
        {
            "src/Updater/Updater.cs", "src/Updater/TiaMcp.Updater.csproj", "src/Updater/App.config",
            "src/Updater/app.manifest", "src/Updater/UpdaterMessages.resx", "src/Tools/WriteGuard/Guard.cs",
            "src/Tools/WriteGuard/Guard.resx", "tests/Updater/UpdaterTests.cs", "tests/Tools/GuardTests.cs",
            "tests/test-suites.json"
        };
        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "Version.props"), "<Project />");
            foreach (var relative in expected.Append("build-tools/release/ReleaseTool.cs"))
            {
                var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, "source");
            }

            var engine = ReleaseRecords.GetSources(root, "engine").Select(row => row.Path).ToHashSet(StringComparer.Ordinal);
            Assert.All(expected, path => Assert.Contains(path, engine));
            Assert.Contains("build-tools/release/ReleaseTool.cs", engine);

            var multi = ReleaseRecords.GetSources(root, "multi").Select(row => row.Path).ToHashSet(StringComparer.Ordinal);
            Assert.Contains("src/Tools/WriteGuard/Guard.cs", multi);
            Assert.Contains("src/Tools/WriteGuard/Guard.resx", multi);
            Assert.Contains("tests/Tools/GuardTests.cs", multi);
            Assert.DoesNotContain("src/Updater/Updater.cs", multi);

            var validation = ReleaseRecords.GetValidationInputs(root, "engine").Select(row => row.Path).ToHashSet(StringComparer.Ordinal);
            Assert.Contains("tests/test-suites.json", validation);
            Assert.Contains("src/Updater/Updater.cs", validation);
            Assert.Contains("tests/Updater/UpdaterTests.cs", validation);
            Assert.Contains("build-tools/release/ReleaseTool.cs", validation);
            Assert.DoesNotContain("src/Updater/UpdaterMessages.resx", validation);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Theory]
    [Trait("Category", "ReleaseParity")]
    [InlineData("runtimeFiles", "unchanged", "")]
    [InlineData("files", "unchanged", "")]
    [InlineData("runtimeFiles", "release", "release/fileVersion changed")]
    [InlineData("files", "release", "release/fileVersion changed")]
    [InlineData("runtimeFiles", "file-version", "release/fileVersion changed")]
    [InlineData("files", "file-version", "release/fileVersion changed")]
    [InlineData("runtimeFiles", "source-hash", "source changed: input.cs")]
    [InlineData("files", "source-hash", "source changed: input.cs")]
    [InlineData("runtimeFiles", "source-removed", "source inventory changed (added/removed input)")]
    [InlineData("files", "source-removed", "source inventory changed (added/removed input)")]
    [InlineData("runtimeFiles", "source-added", "source inventory changed (added/removed input)")]
    [InlineData("files", "source-added", "source inventory changed (added/removed input)")]
    [InlineData("runtimeFiles", "binary-hash", "binary changed: runtime/app.exe")]
    [InlineData("files", "binary-hash", "binary changed: runtime/app.exe")]
    [InlineData("runtimeFiles", "binary-missing", "binary missing: runtime/missing.exe")]
    [InlineData("files", "binary-missing", "binary missing: runtime/missing.exe")]
    public void ReuseRuleMatchesLegacyInventoryCases(string inventoryKey, string mutation, string expected)
    {
        var root = Path.Combine(Path.GetTempPath(), "release-reuse-parity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "runtime"));
        var sourcePath = Path.Combine(root, "input.cs");
        var binaryPath = Path.Combine(root, "runtime/app.exe");
        File.WriteAllText(sourcePath, "source");
        File.WriteAllText(binaryPath, "binary");
        try
        {
            var release = mutation == "release" ? "4.0.1" : "4.0.0";
            var fileVersion = mutation == "file-version" ? "4.0.0.1" : "4.0.0.0";
            var actualSource = new ReleaseArtifact("input.cs", ReleaseValidation.SourceHash(sourcePath));
            var sources = mutation == "source-removed" ? Array.Empty<ReleaseArtifact>()
                : mutation == "source-added" ? [actualSource, new ReleaseArtifact("added.cs", "new")]
                : [actualSource];
            var recordedSources = mutation == "source-hash" ? new[] { new ReleaseArtifact("input.cs", "changed") } : [actualSource];
            if (mutation == "source-removed") recordedSources = [actualSource];
            if (mutation == "source-added") recordedSources = [actualSource];
            var binaryPathValue = mutation == "binary-missing" ? "runtime/missing.exe" : "runtime/app.exe";
            var recordedBinaryHash = ReleaseRecords.HashFile(binaryPath);
            if (mutation == "binary-hash") File.WriteAllText(binaryPath, "changed");
            var expectedHash = mutation == "binary-missing" ? "missing" : recordedBinaryHash;
            var row = new Dictionary<string, object?>
            {
                ["release"] = release,
                ["fileVersion"] = fileVersion,
                ["sourceFiles"] = recordedSources.Select(source => new { path = source.Path, sha256 = source.Sha256 }).ToArray(),
                [inventoryKey] = new[] { new { path = binaryPathValue, sha256 = expectedHash } }
            };
            using var record = JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(row));
            Assert.Equal(expected, ReleaseRecords.ReuseReason(root, record.RootElement, "4.0.0", sources, inventoryKey));
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [Trait("Category", "ReleaseParity")]
    [InlineData("matching", false)]
    [InlineData("missing", true)]
    [InlineData("changed", true)]
    [InlineData("restored", false)]
    public void AuditEvidenceMustBeCompleteHashBoundAndRestorable(string mode, bool rejected)
    {
        var root = Path.Combine(Path.GetTempPath(), "release-audit-parity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var rows = new List<object>();
            foreach (var major in new[] { 20, 21 })
                foreach (var name in new[] { $"native-call-coverage-v{major}.json", $"tool-usage-v{major}.json" })
                {
                    var relative = $"bin-build/releases/v4.0.0/v{major}/{name}";
                    var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    File.WriteAllText(path, "audit");
                    rows.Add(new { path = relative, sha256 = ReleaseRecords.HashFile(path) });
                }
            using var record = JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(new { release = "4.0.0", validationArtifacts = rows }));
            if (mode == "missing") File.Delete(Path.Combine(root, "bin-build/releases/v4.0.0/v20/native-call-coverage-v20.json"));
            if (mode == "changed") File.WriteAllText(Path.Combine(root, "bin-build/releases/v4.0.0/v20/native-call-coverage-v20.json"), "changed");
            if (mode == "restored")
            {
                var archive = ReleaseRecords.MovePreviousReleaseOutput(root, "4.0.0");
                Assert.NotNull(archive);
                ReleaseRecords.RestoreArchivedAuditEvidence(root, "4.0.0", archive!, record.RootElement);
            }
            var error = ReleaseRecords.AuditEvidenceReason(root, "4.0.0", record.RootElement);
            if (rejected) Assert.NotEmpty(error); else Assert.Empty(error);
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [Trait("Category", "ReleaseParity")]
    [InlineData("valid", false)]
    [InlineData("source", true)]
    [InlineData("validation", true)]
    [InlineData("binary", true)]
    [InlineData("inventory", true)]
    [InlineData("api", true)]
    public void MultiVersionPreparationRejectsChangedSourceEvidenceAndApi(string mutation, bool rejected)
    {
        var root = Path.Combine(Path.GetTempPath(), "release-multi-parity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "runtime"));
        var sourcePath = Path.Combine(root, "input.cs");
        var binaryPath = Path.Combine(root, "runtime/app.exe");
        var evidencePath = Path.Combine(root, "evidence.json");
        File.WriteAllText(sourcePath, "source");
        File.WriteAllText(binaryPath, "binary");
        File.WriteAllText(evidencePath, "evidence");
        try
        {
            var sources = new[] { new ReleaseArtifact("input.cs", ReleaseValidation.SourceHash(sourcePath)) };
            var validation = new[] { new ReleaseArtifact("input.cs", ReleaseValidation.SourceHash(sourcePath)) };
            var files = new[] { new ReleaseArtifact("runtime/app.exe", ReleaseRecords.HashFile(binaryPath)) };
            var evidence = new[] { new ReleaseArtifact("evidence.json", ReleaseRecords.HashFile(evidencePath)) };
            var record = new
            {
                release = "4.0.0", fileVersion = "4.0.0.0", publicApiRoot = "local-api",
                sourceFiles = sources.Select(row => new { path = row.Path, sha256 = row.Sha256 }),
                validationInputs = validation.Select(row => new { path = row.Path, sha256 = row.Sha256 }),
                files = files.Select(row => new { path = row.Path, sha256 = row.Sha256 }),
                evidence = evidence.Select(row => new { path = row.Path, sha256 = row.Sha256 })
            };
            using var json = JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(record));
            switch (mutation)
            {
                case "source": sources = [new("input.cs", "changed")]; break;
                case "validation": validation = [new("input.cs", "changed")]; break;
                case "binary": File.WriteAllText(binaryPath, "changed"); break;
                case "inventory": files = [.. files, new("runtime/extra.dll", "new")]; break;
            }
            var api = mutation == "api" ? "other-api" : "local-api";
            if (rejected) Assert.Throws<ReleaseException>(() => ReleaseRecords.AssertMultiVersionPreparation(root, json.RootElement, "4.0.0", sources, validation, files, api));
            else ReleaseRecords.AssertMultiVersionPreparation(root, json.RootElement, "4.0.0", sources, validation, files, api);
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [Trait("Category", "ReleaseParity")]
    [InlineData(false)]
    [InlineData(true)]
    public void RuntimePreparationChecksAllBundledHostAndWorkerDependencies(bool removeDependency)
    {
        var root = Path.Combine(Path.GetTempPath(), "release-runtime-parity-" + Guid.NewGuid().ToString("N"));
        try
        {
            CreateRuntimePreparation(root);
            if (removeDependency) File.Delete(Path.Combine(root, "runtime/dotnet/shared/Microsoft.NETCore.App/10.0.12/System.Text.Json.dll"));
            if (removeDependency) Assert.Throws<ReleaseException>(() => ReleaseRecords.AssertRuntimePreparation(root));
            else ReleaseRecords.AssertRuntimePreparation(root);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    [Trait("Category", "ReleaseParity")]
    public void PreparedRuntimeInventoryExcludesEngineStageTools()
    {
        var root = Path.Combine(Path.GetTempPath(), "release-prepared-runtime-" + Guid.NewGuid().ToString("N"));
        try
        {
            foreach (var relative in new[]
                     {
                         "runtime/tools/TiaMcp.WriteGuard.exe", "runtime/tools/TiaMcp.WriteGuard.dll",
                         "runtime/v20/worker/TiaMcp.Engine.V20.exe", "runtime/verification/NativeCallWeaver.dll",
                         "runtime/studio/TiaOpenness.exe"
                     })
            {
                var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, "binary");
            }

            var prepared = ReleaseRecords.GetRuntimeFiles(root, prepared: true).Select(row => row.Path).ToHashSet(StringComparer.Ordinal);
            Assert.Contains("runtime/studio/TiaOpenness.exe", prepared);
            Assert.DoesNotContain("runtime/tools/TiaMcp.WriteGuard.exe", prepared);
            Assert.DoesNotContain("runtime/v20/worker/TiaMcp.Engine.V20.exe", prepared);
            Assert.DoesNotContain("runtime/verification/NativeCallWeaver.dll", prepared);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static string HashText(string value)
    {
        var path = TempFile("empty.py");
        try { File.WriteAllText(path, value); return ReleaseValidation.SourceHash(path); }
        finally { File.Delete(path); }
    }

    private static string TempFile(string name) => Path.Combine(Path.GetTempPath(), "release-parity-" + Guid.NewGuid().ToString("N") + "-" + name);

    private static void CreateRuntimePreparation(string root)
    {
        var dependencies = new[] { "TiaMcp.WorkerChannel.dll", "System.Text.Json.dll", "System.Text.Encodings.Web.dll", "System.IO.Pipelines.dll", "Microsoft.Bcl.AsyncInterfaces.dll", "System.Buffers.dll", "System.Memory.dll", "System.Numerics.Vectors.dll", "System.Runtime.CompilerServices.Unsafe.dll", "System.Threading.Tasks.Extensions.dll" };
        var required = new List<string> { "runtime/studio/TiaOpenness.exe", "runtime/studio/TiaMcp.WorkerChannel.dll" };
        required.AddRange(dependencies.Select(name => "runtime/studio/bridge/" + name));
        required.AddRange(new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" }.Select(key => $"runtime/studio/bridge/adapters/v{key}/TiaOpenness.Openness.dll"));
        foreach (var key in new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" })
        {
            required.Add($"runtime/v{key}/TiaMcp.FoundationHost.exe");
            if (key is not ("20" or "21")) required.AddRange(dependencies.Select(name => $"runtime/v{key}/worker/{name}"));
            required.Add($"runtime/v{key}/release-key.txt");
            required.Add($"runtime/v{key}/TiaMcp.FoundationHost.runtimeconfig.json");
            required.Add($"runtime/v{key}/TiaMcp.WorkerChannel.dll");
        }
        required.AddRange(new[] { "System.Text.Json.dll", "System.Text.Encodings.Web.dll", "System.IO.Pipelines.dll" }.Select(name => "runtime/dotnet/shared/Microsoft.NETCore.App/10.0.12/" + name));
        foreach (var relative in required)
        {
            var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "fixture");
        }
    }
}
