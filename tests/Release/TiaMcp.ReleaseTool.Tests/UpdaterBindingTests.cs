using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.ReleaseTool;
using Xunit;

namespace TiaMcp.ReleaseTool.Tests;

// The separately built updater (bin-build/updater, packaged as runtime/tools/TiaMcp.Updater.exe and .config) is in neither runtime
// inventory; build-release records it in updaterFiles and the eight-release record binds the same rows (P8-upd-bind).
public sealed class UpdaterBindingTests
{
    private const string Exe = "runtime/tools/TiaMcp.Updater.exe";
    private const string Config = "runtime/tools/TiaMcp.Updater.exe.config";

    [Fact]
    public void BuildOutputIsRecordedWithPackagePathHashAndLength()
    {
        using var tree = new StaticTree();
        Assert.Throws<ReleaseException>(() => ReleaseRecords.HashUpdaterOutput(tree.Root));
        WriteBuildOutput(tree);
        var rows = ReleaseRecords.HashUpdaterOutput(tree.Root);
        Assert.Equal([Exe, Config], rows.Select(row => row.Path));
        foreach (var row in rows)
        {
            var built = Path.Combine(tree.Root, ReleaseRecords.UpdaterOutputs[row.Path]);
            Assert.Equal(ReleaseRecords.HashFile(built), row.Sha256);
            Assert.Equal(new FileInfo(built).Length, row.Length);
        }
        var json = JsonSerializer.SerializeToNode(rows, JsonOptions)!.AsArray();
        Assert.Equal(["length", "path", "sha256"], json[0]!.AsObject().Select(pair => pair.Key).Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("missing-file")]
    [InlineData("extra-file")]
    [InlineData("uppercase-hash")]
    [InlineData("hash-with-newline")]
    [InlineData("zero-length")]
    [InlineData("text-length")]
    [InlineData("in-files-inventory")]
    [InlineData("in-runtime-inventory")]
    [InlineData("not-an-array")]
    public void RecordShapeBindsExactlyTheTwoFilesOutsideTheRuntimeInventory(string mutation)
    {
        var multi = Multi(Rows("exe", "config"));
        Assert.Equal([Exe, Config], Read(multi).Select(row => row.Path).Order(StringComparer.Ordinal));
        Assert.Empty(Read(new JsonObject { ["release"] = "3.3.0", ["files"] = new JsonArray() }));
        var rows = multi["updaterFiles"]!.AsArray();
        switch (mutation)
        {
            case "missing-file": rows.RemoveAt(1); break;
            case "extra-file": rows.Add(Row("runtime/tools/Other.exe", "other")); break;
            case "uppercase-hash": rows[0]!["sha256"] = new string('A', 64); break;
            case "hash-with-newline": rows[0]!["sha256"] = new string('a', 64) + "\n"; break;
            case "zero-length": rows[0]!["length"] = 0; break;
            case "text-length": rows[0]!["length"] = "12"; break;
            case "in-files-inventory": multi["files"]!.AsArray().Add(rows[0]!.DeepClone()); break;
            case "in-runtime-inventory": multi["runtimeFiles"] = new JsonArray(rows[1]!.DeepClone()); break;
            case "not-an-array": multi["updaterFiles"] = rows[0]!.DeepClone(); break;
        }
        Assert.Throws<ReleaseException>(() => Read(multi));
        using var doc = JsonDocument.Parse(multi.ToJsonString());
        Assert.NotEqual("", ReleaseRecords.UpdaterReason(Path.GetTempPath(), doc.RootElement, package: false));
    }

    [Fact]
    public void ReasonChecksTheBytesWhereRepositoryAndPackageKeepThem()
    {
        using var tree = new StaticTree();
        WriteBuildOutput(tree);
        using var record = JsonDocument.Parse(Multi(RowsFrom(tree)).ToJsonString());
        Assert.Equal("", ReleaseRecords.UpdaterReason(tree.Root, record.RootElement, package: false));
        Assert.Equal("updater missing: " + Exe, ReleaseRecords.UpdaterReason(tree.Root, record.RootElement, package: true));
        foreach (var (package, built) in ReleaseRecords.UpdaterOutputs)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(tree.Root, package))!);
            File.Copy(Path.Combine(tree.Root, built), Path.Combine(tree.Root, package));
        }
        Assert.Equal("", ReleaseRecords.UpdaterReason(tree.Root, record.RootElement, package: true));
        // Same length, other bytes: the hash decides; another length fails as well.
        tree.Write(Config, "BUILT CONFIG");
        Assert.Equal("updater changed after validation: " + Config, ReleaseRecords.UpdaterReason(tree.Root, record.RootElement, package: true));
        tree.Write(ReleaseRecords.UpdaterOutputs[Exe], "built exe, rebuilt");
        Assert.Equal("updater changed after validation: " + ReleaseRecords.UpdaterOutputs[Exe], ReleaseRecords.UpdaterReason(tree.Root, record.RootElement, package: false));
        using var unbound = JsonDocument.Parse(new JsonObject { ["release"] = "4.0.0", ["files"] = new JsonArray() }.ToJsonString());
        Assert.Contains("not recorded", ReleaseRecords.UpdaterReason(tree.Root, unbound.RootElement, package: false), StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "BundleParity")]
    public void PackageValidationRequiresTheRecordedUpdaterBytes()
    {
        using var tree = new StaticTree();
        var multi = PackageTree(tree);
        Validate(tree, multi, noBinaries: false);
        // Changed updater bytes in the package.
        tree.Write(Exe, "other updater");
        Assert.Contains("updater changed after validation: " + Exe, Assert.Throws<ReleaseException>(() => Validate(tree, multi, noBinaries: false)).Message, StringComparison.Ordinal);
        tree.Write(Exe, "built exe");
        File.Delete(Path.Combine(tree.Root, Config));
        Assert.Contains("updater missing: " + Config, Assert.Throws<ReleaseException>(() => Validate(tree, multi, noBinaries: false)).Message, StringComparison.Ordinal);
        tree.Write(Config, "built config");
        Validate(tree, multi, noBinaries: false);
        // A 4.x record that ships binaries must bind the updater.
        var unbound = (JsonObject)multi.DeepClone();
        unbound.Remove("updaterFiles");
        Assert.Contains("not recorded", Assert.Throws<ReleaseException>(() => Validate(tree, unbound, noBinaries: false)).Message, StringComparison.Ordinal);
        // Without binaries the shape is still checked.
        var invalid = (JsonObject)multi.DeepClone();
        invalid["updaterFiles"]!.AsArray().RemoveAt(1);
        Assert.Throws<ReleaseException>(() => Validate(tree, invalid, noBinaries: true));
        Validate(tree, multi, noBinaries: true);
    }

    [Fact]
    public void ComparisonRequiresTheEightReleaseRecordToBindTheRecordedUpdater()
    {
        var rows = Rows("exe", "config");
        var engine = new JsonObject { ["release"] = "4.0.0", ["fileVersion"] = "4.0.0.0", ["tier"] = "package", ["updaterFiles"] = rows.DeepClone() };
        var multi = (JsonObject)engine.DeepClone();
        multi["plcRuntimes"] = new JsonObject();
        var compared = Compare(engine, multi);
        Assert.Contains(compared, row => row.EngineField == "updaterFiles[0].sha256" && row.MultiField == "updaterFiles[0].sha256" && row.Status == "equal");
        Assert.Contains(compared, row => row.EngineField == "updaterFiles[1].length" && row.MultiField == "updaterFiles[1].length" && row.Status == "equal");
        Assert.DoesNotContain(compared, row => row.EngineField.StartsWith("updaterFiles", StringComparison.Ordinal) && row.Status != "equal");
        multi["updaterFiles"]![0]!["sha256"] = new string('0', 64);
        Assert.Contains(Compare(engine, multi), row => row.MultiField == "updaterFiles[0].sha256" && row.Status == "different");
        multi.Remove("updaterFiles");
        Assert.Contains(Compare(engine, multi), row => row.MultiField == "updaterFiles[1].path" && row.Status == "missing");
    }

    [Fact]
    public void PythonAndCSharpAcceptTheSameUpdaterRecords()
    {
        var vectors = new JsonArray();
        var valid = Multi(Rows("exe", "config"));
        vectors.Add(valid.DeepClone());
        vectors.Add(new JsonObject { ["release"] = "3.3.0", ["files"] = new JsonArray() });
        var missing = (JsonObject)valid.DeepClone(); missing["updaterFiles"]!.AsArray().RemoveAt(0); vectors.Add(missing);
        var newline = (JsonObject)valid.DeepClone(); newline["updaterFiles"]![1]!["sha256"] = new string('b', 64) + "\n"; vectors.Add(newline);
        var fraction = (JsonObject)valid.DeepClone(); fraction["updaterFiles"]![0]!["length"] = 1.5; vectors.Add(fraction);
        var boolean = (JsonObject)valid.DeepClone(); boolean["updaterFiles"]![0]!["length"] = true; vectors.Add(boolean);
        var inventory = (JsonObject)valid.DeepClone(); inventory["files"]!.AsArray().Add(new JsonObject { ["path"] = Config, ["sha256"] = new string('c', 64) }); vectors.Add(inventory);
        var expected = vectors.Select(vector =>
        {
            using var doc = JsonDocument.Parse(vector!.ToJsonString());
            try { ReleaseRecords.UpdaterFiles(doc.RootElement); return true; }
            catch (ReleaseException) { return false; }
        }).ToArray();
        Assert.Equal([true, true, false, false, false, false, false], expected);

        using var tree = new StaticTree();
        var input = tree.Write("updater-vectors.json", vectors.ToJsonString());
        var root = TiaMcp.BuildCommon.Repository.FindRoot();
        var code = "import importlib.util,json,sys; s=importlib.util.spec_from_file_location('layout',sys.argv[1]); m=importlib.util.module_from_spec(s); s.loader.exec_module(m)\n" +
            "def ok(r):\n    try:\n        m.updater_inventory(r); return True\n    except ValueError:\n        return False\n" +
            "print(json.dumps([ok(r) for r in json.load(open(sys.argv[2], encoding='utf-8'))]))";
        var result = ProcessRunner.Run(OperatingSystem.IsWindows() ? "python" : "python3", ["-c", code, Path.Combine(root, "scripts/checks/Check-BundleLayout.py"), input], root,
            new Dictionary<string, string?> { ["PYTHONDONTWRITEBYTECODE"] = "1" });
        Assert.True(result.ExitCode == 0, result.StandardError);
        Assert.Equal(expected, JsonSerializer.Deserialize<bool[]>(result.StandardOutput));
    }

    [Fact]
    public void BuildReleaseRecordsAndTheCompletionCopiesTheTestedUpdater()
    {
        var root = TiaMcp.BuildCommon.Repository.FindRoot();
        var pipeline = File.ReadAllText(Path.Combine(root, "build-tools/release/BuildReleasePipeline.cs"));
        Assert.Contains("runtimeFiles, updaterFiles, sourceFiles, validationInputs, validationArtifacts = Array.Empty<object>()", pipeline, StringComparison.Ordinal);
        Assert.Contains("versionPolicySdkPassed = versionPolicyPassed, updaterFiles };", pipeline, StringComparison.Ordinal);
        var commands = File.ReadAllText(Path.Combine(root, "build-tools/release/ReleaseCommands.cs"));
        Assert.Contains("updaterFiles = engineRecord.RootElement.GetProperty(\"updaterFiles\"),", commands, StringComparison.Ordinal);
        Assert.Contains("ReleaseRecords.UpdaterReason(Root, engineRecord.RootElement, package: false)", commands, StringComparison.Ordinal);
        Assert.Contains("ReleaseRecords.UpdaterReason(Root, written.RootElement, package: false)", commands, StringComparison.Ordinal);
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private static string Hash(string text) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)));

    private static JsonObject Row(string path, string content) => new() { ["path"] = path, ["sha256"] = Hash(content), ["length"] = content.Length };

    private static JsonArray Rows(string exe, string config) => new(Row(Exe, exe), Row(Config, config));

    private static JsonArray RowsFrom(StaticTree tree) => new(ReleaseRecords.HashUpdaterOutput(tree.Root).Select(row => JsonSerializer.SerializeToNode(row, JsonOptions)).ToArray());

    private static void WriteBuildOutput(StaticTree tree)
    {
        tree.Write(ReleaseRecords.UpdaterOutputs[Exe], "built exe");
        tree.Write(ReleaseRecords.UpdaterOutputs[Config], "built config");
    }

    // P8-04l: a 4.x eight-release record without an embedded engine record (engineCompatibility).
    private static JsonObject Multi(JsonArray rows) =>
        new() { ["release"] = "4.0.0", ["files"] = new JsonArray(), ["updaterFiles"] = rows };

    private static IReadOnlyList<UpdaterArtifact> Read(JsonObject record)
    {
        using var doc = JsonDocument.Parse(record.ToJsonString());
        return ReleaseRecords.UpdaterFiles(doc.RootElement);
    }

    private static IReadOnlyList<ReleaseRecords.FieldComparison> Compare(JsonObject engine, JsonObject multi)
    {
        using var left = JsonDocument.Parse(engine.ToJsonString());
        using var right = JsonDocument.Parse(multi.ToJsonString());
        return ReleaseRecords.Compare(left.RootElement, right.RootElement);
    }

    // An unpacked 4.x package: the eight-release record with its PLC artifacts, the configurator and the updater under runtime/tools.
    private static JsonObject PackageTree(StaticTree tree)
    {
        var plan = JsonSerializer.SerializeToNode(new ReleaseCheckPlan("package", [], [], []), JsonOptions);
        var files = new JsonArray();
        var runtimes = new JsonObject();
        foreach (var key in new[] { "20", "21" })
        {
            var runtime = new JsonObject { ["checks"] = new JsonObject() };
            foreach (var (field, name) in new[] { ("worker", $"worker/TiaMcp.PlcWorker.{key}.exe"), ("adapter", $"worker/TiaMcp.Adapter.{key}.dll"), ("host", "TiaMcp.FoundationHost.exe") })
            {
                var path = $"runtime/v{key}/{name}";
                tree.Write(path, "synthetic " + key + field);
                runtime[field] = new JsonObject { ["path"] = path, ["sha256"] = ReleaseRecords.HashFile(Path.Combine(tree.Root, path)) };
                files.Add(runtime[field]!.DeepClone());
            }
            foreach (var field in new[] { "sdkWorker", "catalog", "toolsList" }) runtime[field] = new JsonObject { ["path"] = field, ["sha256"] = Hash(field) };
            runtimes["V" + key] = runtime;
        }
        tree.Write(Exe, "built exe");
        tree.Write(Config, "built config");
        var rows = Rows("built exe", "built config");
        var engine = new JsonObject
        {
            ["release"] = "4.0.0", ["fileVersion"] = "4.0.0.0", ["tier"] = "package", ["checkPlan"] = plan!.DeepClone(),
            ["sourceFiles"] = new JsonArray(), ["validationInputs"] = new JsonArray(), ["validation"] = new JsonObject(),
            ["runtimeFiles"] = files.DeepClone(), ["updaterFiles"] = rows.DeepClone()
        };
        var multi = (JsonObject)engine.DeepClone();
        multi.Remove("runtimeFiles");
        multi["files"] = files;
        multi["plcRuntimes"] = runtimes;
        tree.Write("CHANGELOG.md", "## [4.0.0]\n");
        tree.Write("TiaOpenness.exe", "launcher");
        tree.Write("manifest/configurator-build.json", new JsonObject
        { ["testsPassed"] = 1, ["executable"] = new JsonObject { ["path"] = "TiaOpenness.exe", ["sha256"] = Hash("launcher") } }.ToJsonString());
        tree.Write("manifest/package-manifest.json", "{\"bundleVersion\":\"4.0.0\",\"packageName\":\"TIA_MCP_Delivery_v4.0.0_20261021\"}");
        return multi;
    }

    private static void Validate(StaticTree tree, JsonObject multi, bool noBinaries)
    {
        tree.Write(ReleaseRecords.MultiPath, multi.ToJsonString());
        tree.Write("manifest/delivery.json", new JsonObject
        {
            ["release"] = "4.0.0", ["engineRelease"] = "4.0.0", ["fileVersion"] = "4.0.0.0",
            ["multiVersionBuildSha256"] = ReleaseRecords.HashFile(Path.Combine(tree.Root, ReleaseRecords.MultiPath)),
            ["configuratorBuildSha256"] = ReleaseRecords.HashFile(Path.Combine(tree.Root, "manifest/configurator-build.json"))
        }.ToJsonString());
        ReleaseCommands.ValidateBuildRecords(tree.Root, package: true, noBinaries: noBinaries, skipSourceHashes: true);
    }
}
