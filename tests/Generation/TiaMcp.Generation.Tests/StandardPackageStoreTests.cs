using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using TiaMcp.Logic.Generation;
using Xunit;

namespace TiaMcp.Generation.Tests
{
    public sealed class StandardPackageStoreTests : IDisposable
    {
        private readonly string root = Path.Combine(AppContext.BaseDirectory, "package-store", Guid.NewGuid().ToString("N"));
        private readonly StandardPackageStore store;
        public StandardPackageStoreTests()
        {
            Directory.CreateDirectory(root);
            store = new StandardPackageStore(Path.Combine(AppContext.BaseDirectory, "BasicPackage"), Path.Combine(root, "data", "standards"));
        }
        public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
        private JsonObject Manage(string action, string output = "", string id = "tiamcp.basic", string version = "1.0.0", string source = "", string newId = "", string newVersion = "", bool preview = true, string hash = "")
            => store.Manage(action, id, version, source, output, newId, newVersion, preview, hash);

        [Fact]
        public void StorePlansRemainPortableAcrossBundleAndDataRoots()
        {
            var original = store.Load("tiamcp.basic", "1.0.0");
            string repository = Path.Combine(root, "relocated", "templates", "standards", "tiamcp.basic");
            Directory.CreateDirectory(repository);
            foreach (var name in original.FileNames)
            {
                string path = Path.Combine(repository, name.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllBytes(path, original.ReadFile(name));
            }
            var relocated = new StandardPackageStore(Path.GetDirectoryName(repository)!, Path.Combine(root, "relocated-data", "standards"));
            var first = Manage("fork", newId: "example.portable", newVersion: "2.0.0");
            var second = relocated.Manage("fork", "tiamcp.basic", "1.0.0", "", "", "example.portable", "2.0.0", true, "");
            Assert.Equal((string)first["planHash"]!, (string)second["planHash"]!);
            Assert.NotEqual((string)first["targetPath"]!, (string)second["targetPath"]!);
            Manage("fork", newId: "example.portable", newVersion: "2.0.0", preview: false, hash: (string)first["planHash"]!);
            relocated.Manage("fork", "tiamcp.basic", "1.0.0", "", "", "example.portable", "2.0.0", false, (string)first["planHash"]!);
            Assert.Equal(store.Load("example.portable", "2.0.0").ContentHash, relocated.Load("example.portable", "2.0.0").ContentHash);
            first = Manage("remove", id: "example.portable", version: "2.0.0");
            second = relocated.Manage("remove", "example.portable", "2.0.0", "", "", "", "", true, "");
            Assert.Equal((string)first["planHash"]!, (string)second["planHash"]!);
            string output = Path.Combine(root, "portable.zip");
            Assert.Equal((string)Manage("export", output)["planHash"]!,
                (string)relocated.Manage("export", "tiamcp.basic", "1.0.0", "", output, "", "", true, "")["planHash"]!);
        }
        [Fact]
        public void UserSuppliedPathsRemainBoundToPlans()
        {
            var preview = Manage("fork", newId: "example.paths", newVersion: "2.0.0");
            Manage("fork", newId: "example.paths", newVersion: "2.0.0", preview: false, hash: (string)preview["planHash"]!);
            string source = Path.Combine(root, "source.zip"), copy = Path.Combine(root, "copy.zip");
            using (var output = File.Create(source)) store.Load("example.paths", "2.0.0").WriteCanonicalZip(output);
            File.Copy(source, copy);
            var other = new StandardPackageStore(store.RepositoryRoot, Path.Combine(root, "other-data", "standards"));
            Assert.NotEqual((string)other.Manage("import", "", "", source, "", "", "", true, "")["planHash"]!,
                (string)other.Manage("import", "", "", copy, "", "", "", true, "")["planHash"]!);
            string target = Path.Combine(root, "target.zip");
            Assert.NotEqual((string)Manage("export", target)["planHash"]!, (string)Manage("export", Path.Combine(root, ".", "target.zip"))["planHash"]!);
            string json = Encoding.UTF8.GetString(store.Load("tiamcp.basic", "1.0.0").ReadFile("examples/machine.json"));
            string input = Path.Combine(root, "input.json"), second = Path.Combine(root, "second.json"), result = Path.Combine(root, "result.json");
            File.WriteAllText(input, json, new UTF8Encoding(false)); File.WriteAllText(second, json, new UTF8Encoding(false));
            Assert.NotEqual((string)store.Machine("import", "tiamcp.basic", "1.0.0", "", input, result, "json", true, "")["planHash"]!,
                (string)store.Machine("import", "tiamcp.basic", "1.0.0", "", second, result, "json", true, "")["planHash"]!);
            Assert.NotEqual((string)store.Machine("export", "tiamcp.basic", "1.0.0", json, "", result, "json", true, "")["planHash"]!,
                (string)store.Machine("export", "tiamcp.basic", "1.0.0", json, "", Path.Combine(root, ".", "result.json"), "json", true, "")["planHash"]!);
        }
        [Fact]
        public void ValidationExpandsNamesAndChecksDeterminismForEveryTargetWithoutWriting()
        {
            var result = store.Validate("tiamcp.basic", "1.0.0");
            Assert.True(result["valid"]!.GetValue<bool>());
            Assert.Equal(8, result["selfChecks"]!.AsArray().Count);
            Assert.All(result["selfChecks"]!.AsArray(), check => {
                Assert.Equal(0, check!["errors"]!.GetValue<int>());
                Assert.True(check["deterministic"]!.GetValue<bool>());
                Assert.Equal("NOT RUN", (string)check["nativeAcceptance"]!);
            });
            Assert.False(Directory.Exists(store.UserRoot));
        }
        [Fact]
        public void ForkExportImportRemovePreservesNoticesAndImmutableInventory()
        {
            var original = store.Load("tiamcp.basic", "1.0.0");
            var preview = Manage("fork", newId: "example.basic", newVersion: "2.0.0");
            Assert.False(Directory.Exists(store.UserRoot));
            Assert.Throws<ArgumentException>(() => Manage("fork", newId: "example.basic", newVersion: "2.0.0", preview: false));
            var applied = Manage("fork", newId: "example.basic", newVersion: "2.0.0", preview: false, hash: (string)preview["planHash"]!);
            var fork = store.Load("example.basic", "2.0.0");
            Assert.NotEqual(original.ContentHash, fork.ContentHash);
            Assert.Equal(original.ReadFile("LICENSE"), fork.ReadFile("LICENSE"));
            Assert.Equal(original.ReadFile("NOTICE.md"), fork.ReadFile("NOTICE.md"));
            Assert.Equal(original.ReadFile("sources.json"), fork.ReadFile("sources.json"));
            Assert.DoesNotContain("lib:tiamcp.basic/", fork.Documents["rules/motor.dol.json"].GetRawText());
            fork.ValidateMachine(Encoding.UTF8.GetString(fork.ReadFile("examples/machine.json")));
            var bytes = fork.ReadFile("LICENSE"); bytes[0] ^= 1;
            Assert.Equal(original.ReadFile("LICENSE"), fork.ReadFile("LICENSE"));
            string path = Path.Combine(root, "export.zip");
            preview = Manage("export", path, "example.basic", "2.0.0");
            Assert.False(File.Exists(path));
            Manage("export", path, "example.basic", "2.0.0", preview: false, hash: (string)preview["planHash"]!);
            Assert.Equal(fork.ContentHash, StandardPackageLoader.LoadZip(path).ContentHash);
            var second = new StandardPackageStore(store.RepositoryRoot, Path.Combine(root, "other", "standards"));
            preview = second.Manage("import", "", "", path, "", "", "", true, "");
            second.Manage("import", "", "", path, "", "", "", false, (string)preview["planHash"]!);
            Assert.Equal(fork.ContentHash, second.Load("example.basic", "2.0.0").ContentHash);
            preview = Manage("remove", id: "example.basic", version: "2.0.0");
            Manage("remove", id: "example.basic", version: "2.0.0", preview: false, hash: (string)preview["planHash"]!);
            Assert.Single(store.List()["items"]!.AsArray());
            Assert.Equal(original.ContentHash, store.Load("tiamcp.basic", "1.0.0").ContentHash);
        }
        [Theory]
        [InlineData("remove", "", "")]
        [InlineData("fork", "tiamcp.basic", "2.0.0")]
        [InlineData("copy", "../escape", "2.0.0")]
        [InlineData("fork", "example.basic", "invalid")]
        [InlineData("fork", "con", "2.0.0")]
        [InlineData("unknown", "", "")]
        public void RefusesReadOnlyAndInvalidIdentities(string action, string id, string version)
        {
            Assert.ThrowsAny<Exception>(() => Manage(action, newId: id, newVersion: version));
            Assert.False(Directory.Exists(store.UserRoot));
        }
        [Fact]
        public void RefusesDriveRelativeAndCurrentDrivePaths()
        {
            foreach (var path in new[] { "C:relative.zip", "\\relative.zip", "relative.zip" })
                Assert.Throws<ArgumentException>(() => Manage("export", path));
            Assert.False(Directory.Exists(store.UserRoot));
        }
        [Fact]
        public void RefusesRepositoryOverwriteAndStalePlanBeforeWriting()
        {
            Assert.Throws<ArgumentException>(() => Manage("export", Path.Combine(store.RepositoryRoot, "example.zip")));
            string path = Path.Combine(root, "output.zip");
            var preview = Manage("export", path);
            File.WriteAllText(path, "existing");
            Assert.Throws<ArgumentException>(() => Manage("export", path, preview: false, hash: (string)preview["planHash"]!));
            Assert.Equal("existing", File.ReadAllText(path));
        }
        [Fact]
        public void ImportReusesZipTraversalAndArchiveLimits()
        {
            string path = Path.Combine(root, "unsafe.zip");
            using (var output = File.Create(path)) using (var zip = new ZipArchive(output, ZipArchiveMode.Create)) zip.CreateEntry("../escape");
            Assert.Throws<GenerationValidationException>(() => store.Manage("import", "", "", path, "", "", "", true, ""));
            using (var output = File.Create(path)) output.SetLength(32 * 1024 * 1024 + 1);
            Assert.Throws<GenerationValidationException>(() => store.Manage("import", "", "", path, "", "", "", true, ""));
            Assert.False(Directory.Exists(store.UserRoot));
        }
        [Theory]
        [MemberData(nameof(BasicPackageModelTests.Releases), MemberType = typeof(BasicPackageModelTests))]
        public void CsvAndJsonRoundTripRetainsEveryFieldForEachRelease(string release)
        {
            var package = store.Load("tiamcp.basic", "1.0.0");
            var machine = JsonNode.Parse(Encoding.UTF8.GetString(package.ReadFile("examples/machine.json")))!;
            machine["target"]!["release"] = release;
            machine["devices"]![0]!["name"]!["fr-FR"] = "Quoted, \"motor\"\nsecond line";
            machine["devices"]![0]!["note"] = "Multiline\nremark";
            string json = machine.ToJsonString();
            string directory = Path.Combine(root, "csv"); Directory.CreateDirectory(directory);
            foreach (var file in MachineCsv.Export(package, json)) File.WriteAllBytes(Path.Combine(directory, file.Key), file.Value);
            string imported = MachineCsv.Import(package, directory);
            Assert.Equal(GenerationDocuments.MachineHash(json), GenerationDocuments.MachineHash(imported));
            var result = store.Machine("import", "tiamcp.basic", "1.0.0", "", directory, "", "csv", true, "");
            Assert.Equal(GenerationDocuments.MachineHash(json), (string)result["machineHash"]!);
            string output = Path.Combine(root, "machine.json");
            var preview = store.Machine("export", "tiamcp.basic", "1.0.0", json, "", output, "json", true, "");
            Assert.False(File.Exists(output));
            store.Machine("export", "tiamcp.basic", "1.0.0", json, "", output, "json", false, (string)preview["planHash"]!);
            Assert.Equal(GenerationDocuments.MachineHash(json), GenerationDocuments.MachineHash(File.ReadAllText(output)));
            Assert.DoesNotContain((byte)13, File.ReadAllBytes(output));
        }
        [Fact]
        public void TemplateExposesTypedDefaultsAndCpuSelectionRejects1200()
        {
            var description = store.Describe("tiamcp.basic", "1.0.0");
            Assert.Equal(false, description["cpuSelection"]!["default"]!.GetValue<bool>());
            var template = store.Machine("exportTemplate", "tiamcp.basic", "1.0.0", "", "", Path.Combine(root, "template"), "csv", true, "");
            Assert.Equal(5, template["files"]!.AsObject().Count);
            Assert.False(Directory.Exists(Path.Combine(root, "template")));
            var package = store.Load("tiamcp.basic", "1.0.0");
            store.Machine("exportTemplate", "tiamcp.basic", "1.0.0", "", "", Path.Combine(root, "template"), "csv", false, (string)template["planHash"]!);
            string headers = File.ReadAllLines(Path.Combine(root, "template", "Devices.csv"))[0];
            Assert.Contains("Name:en-US", headers); Assert.Contains("Name:zh-CN", headers);
            Assert.Contains("Param:CycleMs", headers); Assert.Contains("Param:programAlarm", headers);
            Assert.Equal(GenerationDocuments.MachineHash(MachineCsv.EmptyMachine(package)), GenerationDocuments.MachineHash(MachineCsv.Import(package, Path.Combine(root, "template"))));
            var machine = JsonNode.Parse(Encoding.UTF8.GetString(package.ReadFile("examples/machine.json")))!;
            machine["stations"]![0]!["kind"] = "S7-1200";
            var error = Assert.Throws<GenerationValidationException>(() => store.Machine("validate", "tiamcp.basic", "1.0.0", machine.ToJsonString(), "", "", "json", true, ""));
            Assert.Contains(error.Errors, e => e.Rule == "cpu");
        }
        [Fact]
        public void CsvRefusesUnknownSheetsAndDuplicateHeaders()
        {
            var package = store.Load("tiamcp.basic", "1.0.0");
            string directory = Path.Combine(root, "csv"); Directory.CreateDirectory(directory);
            foreach (var file in MachineCsv.Export(package, MachineCsv.EmptyMachine(package))) File.WriteAllBytes(Path.Combine(directory, file.Key), file.Value);
            File.WriteAllText(Path.Combine(directory, "Extra.csv"), "unknown");
            Assert.Throws<ArgumentException>(() => MachineCsv.Import(package, directory));
            File.Delete(Path.Combine(directory, "Extra.csv"));
            File.WriteAllText(Path.Combine(directory, "Machine.csv"), "id,id\nM1,M1\n");
            Assert.Throws<ArgumentException>(() => MachineCsv.Import(package, directory));
            foreach (var file in MachineCsv.Export(package, MachineCsv.EmptyMachine(package))) File.WriteAllBytes(Path.Combine(directory, file.Key), file.Value);
            string devices = Path.Combine(directory, "Devices.csv");
            File.WriteAllText(devices, File.ReadAllText(devices).TrimEnd('\n') + ",\"Unknown\"\n");
            Assert.Throws<ArgumentException>(() => MachineCsv.Import(package, directory));
        }
    }
}
