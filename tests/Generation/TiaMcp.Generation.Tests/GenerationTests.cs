using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Logic.Generation;
using Xunit;

namespace TiaMcp.Generation.Tests
{
    public sealed class GenerationTests
    {
        private static string Example(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Examples", name + ".json"));
        private static JsonObject Object(string name) => JsonNode.Parse(Example(name))!.AsObject();
        public static IEnumerable<object[]> SchemaNames => new[] { "common", "package", "naming", "structure", "hardware", "library", "modes", "alarms", "hmi", "checks", "rule", "machine", "plan", "check-result" }.Select(n => new object[] { n });

        [Theory]
        [MemberData(nameof(SchemaNames))]
        public void EverySchemaAcceptsItsPositiveExample(string name)
        {
            Assert.Empty(GenerationSchemas.Validate(name, CanonicalJson.Parse(Example(name))));
            var schema = CanonicalJson.Parse(GenerationSchemas.GetJson(name));
            Assert.Equal("https://json-schema.org/draft/2020-12/schema", schema.GetProperty("$schema").GetString());
        }

        [Theory]
        [MemberData(nameof(SchemaNames))]
        public void EverySchemaRejectsWrongTypesAndMissingRequiredFields(string name)
        {
            Assert.Contains(GenerationSchemas.Validate(name, CanonicalJson.Parse("[]")), e => e.Rule == "type");
            Assert.Contains(GenerationSchemas.Validate(name, CanonicalJson.Parse("{}")), e => e.Rule == "required" && e.Path.StartsWith("/", StringComparison.Ordinal));
            var value = Object(name);
            value["unknown"] = true;
            Assert.Contains(GenerationSchemas.Validate(name, CanonicalJson.Parse(value.ToJsonString())), e => e.Rule == "additionalProperties" && e.Path == "/unknown");
        }

        [Fact]
        public void TypedModelsRoundTripAllParts()
        {
            RoundTrip<StandardPackageManifest>("package"); RoundTrip<NamingPart>("naming"); RoundTrip<StructurePart>("structure");
            RoundTrip<HardwarePart>("hardware"); RoundTrip<LibraryPart>("library"); RoundTrip<ModesPart>("modes");
            RoundTrip<AlarmsPart>("alarms"); RoundTrip<HmiPart>("hmi"); RoundTrip<ChecksPart>("checks"); RoundTrip<DeviceTypeRule>("rule");
            RoundTrip<MachineDescription>("machine"); RoundTrip<GenerationPlan>("plan"); RoundTrip<StandardCheckResult>("check-result");
            var rule = Object("rule");
            rule["params"]!["properties"]!["nullable"] = JsonNode.Parse("{\"type\":\"null\",\"default\":null}");
            var canonicalRule = GenerationDocuments.Canonical(GenerationDocuments.Load<DeviceTypeRule>(rule.ToJsonString()));
            Assert.Contains("\"default\":null", canonicalRule);
            Assert.Equal(JsonValueKind.Null, GenerationDocuments.Load<DeviceTypeRule>(canonicalRule).Params.Properties!["nullable"].Default.ValueKind);
            void RoundTrip<T>(string name) where T : class
            {
                var canonical = GenerationDocuments.Canonical(GenerationDocuments.Load<T>(Example(name)));
                Assert.Equal(canonical, GenerationDocuments.Canonical(GenerationDocuments.Load<T>(canonical)));
            }
        }

        [Theory]
        [InlineData("en-US")]
        [InlineData("fr-FR")]
        [InlineData("tr-TR")]
        [InlineData("zh-CN")]
        public void CanonicalGoldenBytesIgnoreCultureOrderingWhitespaceAndLineEndings(string culture)
        {
            var previous = CultureInfo.CurrentCulture;
            var previousUi = CultureInfo.CurrentUICulture;
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
                CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
                const string expected = "{\"a\":123e-2,\"b\":1000,\"text\":\"中😀\\u000a\",\"zero\":0}";
                var first = CanonicalJson.Parse("{\r\n \"zero\": -0.0, \"text\":\"中\\ud83d\\ude00\\n\", \"b\":1e3, \"a\":1.2300 }");
                var second = CanonicalJson.Parse(expected);
                Assert.Equal(expected, Encoding.UTF8.GetString(CanonicalJson.Encode(first)));
                Assert.Equal(CanonicalJson.Hash(second), CanonicalJson.Hash(first));
                Assert.Equal("sha256:" + "bed4e1220d4daaf5d1c5c58c4b0cf7f4ad1632a16c3a6819e5a5b6cbd8ec83ea", CanonicalJson.Hash(first));
            }
            finally { CultureInfo.CurrentCulture = previous; CultureInfo.CurrentUICulture = previousUi; }
        }

        [Theory]
        [InlineData("{\"x\":1,\"x\":2}")]
        [InlineData("{\"x\":{\"a\":1,\"\\u0061\":1}}")]
        [InlineData("{\"x\":NaN}")]
        [InlineData("{\"x\":1e1000001}")]
        [InlineData("{\"x\":\"\\ud800\"}")]
        [InlineData("{\"x\":\"\\udc00\"}")]
        public void MalformedOrAmbiguousJsonHasTypedErrors(string json)
        {
            Assert.NotEmpty(Assert.Throws<GenerationValidationException>(() => CanonicalJson.Parse(json)).Errors);
        }

        [Fact]
        public void EncodingDepthAndExactNumberBoundariesAreEnforced()
        {
            Assert.Throws<GenerationValidationException>(() => CanonicalJson.Parse(new byte[] { 0xef, 0xbb, 0xbf, 123, 125 }));
            Assert.Throws<GenerationValidationException>(() => CanonicalJson.Parse(new byte[] { 123, 34, 120, 34, 58, 34, 0xff, 34, 125 }));
            Assert.Throws<GenerationValidationException>(() => CanonicalJson.Parse(Encoding.Unicode.GetBytes("{}")));
            Assert.Throws<GenerationValidationException>(() => CanonicalJson.Parse(new string('[', 65) + "0" + new string(']', 65)));
            Assert.Equal("12345678901234567890123456789e-1", CanonicalJson.Normalize("1234567890123456789012345678.9"));
            var model = Object("structure"); model["organizationBlocks"]![0]!["number"] = JsonNode.Parse("1.0");
            Assert.Equal(1, GenerationDocuments.Load<StructurePart>(model.ToJsonString()).OrganizationBlocks![0].Number);
        }

        [Fact]
        public void MachineAndPlanHashesAreCanonicalAndBindMeaningfulChanges()
        {
            var machine = Object("machine");
            var hash = GenerationDocuments.MachineHash(machine.ToJsonString());
            Assert.Equal(hash, GenerationDocuments.MachineHash(Reverse(machine).ToJsonString(new JsonSerializerOptions { WriteIndented = true }).Replace("\n", "\r\n")));
            machine["devices"]![0]!["params"]!["powerKw"] = 1.25;
            Assert.NotEqual(hash, GenerationDocuments.MachineHash(machine.ToJsonString()));
            var plan = Object("plan");
            hash = GenerationDocuments.PlanHash(plan.ToJsonString());
            plan["planHash"] = "sha256:" + new string('a', 64);
            Assert.Equal(hash, GenerationDocuments.PlanHash(Reverse(plan).ToJsonString()));
            plan["target"]!["projectIdentity"] = "another-project";
            Assert.NotEqual(hash, GenerationDocuments.PlanHash(plan.ToJsonString()));
        }

        [Theory]
        [InlineData("{{System.IO.File.Delete(device.id)}}")]
        [InlineData("{{upper(device.id + 'x')}}")]
        [InlineData("{{eval(device.id)}}")]
        [InlineData("{{device.id}}}}")]
        [InlineData("{{device.id")]
        [InlineData("{{pad(device.id)}}")]
        [InlineData("{{alloc.io(device.id, signal)}}")]
        public void PackageRulesRejectExecutableOrMalformedPlaceholders(string template)
        {
            var rule = Object("rule"); rule["emit"]![0]!["name"] = template;
            Assert.Contains(Assert.Throws<GenerationValidationException>(() => GenerationDocuments.Load<DeviceTypeRule>(rule.ToJsonString())).Errors, e => e.Rule == "placeholder");
        }

        [Fact]
        public void RulesAllowOnlyDeclarativeConditionsAndParameterSchemas()
        {
            var rule = Object("rule");
            rule["emit"]![0]!["name"] = "{{pad(seq('tags'), 4)}}_{{lower(device.id)}}";
            rule["emit"]![0]!["when"] = JsonNode.Parse("{\"equals\":{\"path\":\"device.type\",\"value\":\"motor.dol\"}}");
            GenerationDocuments.Load<DeviceTypeRule>(rule.ToJsonString());
            rule["emit"]![0]!["when"] = JsonNode.Parse("{\"in\":{\"path\":\"device.type\",\"values\":[\"motor.dol\"]}}");
            GenerationDocuments.Load<DeviceTypeRule>(rule.ToJsonString());
            rule["params"]!["$ref"] = "https://example.invalid/schema";
            Assert.Throws<GenerationValidationException>(() => GenerationDocuments.Load<DeviceTypeRule>(rule.ToJsonString()));
            rule = Object("rule"); rule["emit"]![0]!["when"] = "device.power > 3";
            Assert.Throws<GenerationValidationException>(() => GenerationDocuments.Load<DeviceTypeRule>(rule.ToJsonString()));
        }

        [Fact]
        public void MachineReferencesAndPlanDependenciesAndDigestsAreChecked()
        {
            var machine = Object("machine"); machine["devices"]![0]!["station"] = "missing";
            Assert.Contains(Assert.Throws<GenerationValidationException>(() => GenerationDocuments.Load<MachineDescription>(machine.ToJsonString())).Errors, e => e.Path == "/devices/0/station");
            var plan = Object("plan"); plan["steps"]![0]!["dependsOn"] = new JsonArray("s1");
            Assert.Contains(Assert.Throws<GenerationValidationException>(() => GenerationDocuments.Load<GenerationPlan>(plan.ToJsonString())).Errors, e => e.Rule == "dependency");
            plan = Object("plan"); plan["steps"]![0]!["arguments"]!["source"] = "changed.scl";
            Assert.Contains(Assert.Throws<GenerationValidationException>(() => GenerationDocuments.Load<GenerationPlan>(plan.ToJsonString())).Errors, e => e.Rule == "argument-digest");
        }

        [Fact]
        public void DirectoryZipCanonicalZipRoundTripsWithAllPartsAndResources()
        {
            var files = PackageFiles();
            var first = Load(files);
            Assert.Equal("fixture.basic", first.Manifest.Id);
            Assert.Equal(14, first.FileNames.Count);
            Assert.Equal("idb", first.GetPart<NamingPart>("naming.json").Rules[0].Id);
            Assert.Equal("M101", first.ValidateMachine(Example("machine")).Devices[0].Id);
            using var stream = new MemoryStream();
            first.WriteCanonicalZip(stream); stream.Position = 0;
            var second = StandardPackageLoader.LoadZip(stream);
            Assert.Equal(first.ContentHash, second.ContentHash);
            using var folder = new PackageFolder(files);
            Assert.Equal(first.ContentHash, StandardPackageLoader.LoadDirectory(folder.Path).ContentHash);
            files["package.json"] = Encoding.UTF8.GetBytes(Reverse(JsonNode.Parse(Encoding.UTF8.GetString(files["package.json"]))!).ToJsonString());
            files["rule.json"] = Encoding.UTF8.GetBytes(Reverse(Object("rule")).ToJsonString(new JsonSerializerOptions { WriteIndented = true }).Replace("\n", "\r\n"));
            Assert.Equal(first.ContentHash, Load(files.Reverse().ToDictionary(p => p.Key, p => p.Value)).ContentHash);
            files["sources/motor.scl"] = Encoding.UTF8.GetBytes("changed source");
            Assert.Contains(Assert.Throws<GenerationValidationException>(() => Load(files)).Errors, e => e.Rule == "file-hash");
        }

        [Theory]
        [InlineData("../escape.json")]
        [InlineData("/root.json")]
        [InlineData("C:/root.json")]
        [InlineData("a\\..\\escape.json")]
        [InlineData("a/./b.json")]
        [InlineData("a//b.json")]
        [InlineData("CON.json")]
        [InlineData("file.json:stream")]
        [InlineData("a./file.json")]
        [InlineData("a /file.json")]
        [InlineData("e\u0301.json")]
        public void ZipSlipAndNonPortablePathsAreRejectedWithoutExtraction(string path)
        {
            Assert.Contains(Assert.Throws<GenerationValidationException>(() => Load(new Dictionary<string, byte[]> { [path] = Encoding.UTF8.GetBytes("{}") })).Errors, e => e.Rule == "path");
        }

        [Fact]
        public void DuplicateNamesCaseAliasesFileDirectoryCollisionsAndLinksAreRejected()
        {
            foreach (var names in new[] { new[] { "package.json", "package.json" }, new[] { "package.json", "PACKAGE.json" }, new[] { "a", "a/b" }, new[] { "a/b", "a" }, new[] { "a/", "A/" }, new[] { "a/x", "A/y" } })
            {
                using var stream = Zip(names.Select(n => new KeyValuePair<string, byte[]>(n, n.EndsWith("/", StringComparison.Ordinal) ? Array.Empty<byte>() : Encoding.UTF8.GetBytes("{}"))));
                Assert.Contains(Assert.Throws<GenerationValidationException>(() => StandardPackageLoader.LoadZip(stream)).Errors, e => e.Rule == "duplicate-name");
            }
            using var linked = Zip(new[] { new KeyValuePair<string, byte[]>("link.json", Encoding.UTF8.GetBytes("{}")) }, unchecked((int)0xa1ff0000));
            Assert.Contains(Assert.Throws<GenerationValidationException>(() => StandardPackageLoader.LoadZip(linked)).Errors, e => e.Rule == "link");
        }

        [Fact]
        public void SizeCountAndArchiveLimitsCoverCompressedAndDirectoryInput()
        {
            var files = PackageFiles();
            AssertRule("file-size", new PackageLoadLimits(maximumFileBytes: 32));
            AssertRule("file-count", new PackageLoadLimits(maximumFiles: 1));
            AssertRule("entry-count", new PackageLoadLimits(maximumFiles: 1, maximumEntries: 1));
            AssertRule("total-size", new PackageLoadLimits(maximumFileBytes: 4096, maximumTotalBytes: 4096));
            AssertRule("archive-size", new PackageLoadLimits(maximumArchiveBytes: 32), directory: false);
            var bomb = new Dictionary<string, byte[]> { ["package.json"] = new byte[65536] };
            Assert.Contains(Assert.Throws<GenerationValidationException>(() => Load(bomb, new PackageLoadLimits(maximumFileBytes: 1024))).Errors, e => e.Rule == "file-size");
            using var directories = Zip(Enumerable.Range(0, 3).Select(i => new KeyValuePair<string, byte[]>("dir" + i + "/", Array.Empty<byte>())));
            Assert.Contains(Assert.Throws<GenerationValidationException>(() => StandardPackageLoader.LoadZip(directories, new PackageLoadLimits(1, 2))).Errors, e => e.Rule == "entry-count");
            void AssertRule(string rule, PackageLoadLimits limits, bool directory = true)
            {
                Assert.Contains(Assert.Throws<GenerationValidationException>(() => Load(files, limits)).Errors, e => e.Rule == rule);
                if (!directory) return;
                using var folder = new PackageFolder(files);
                Assert.Contains(Assert.Throws<GenerationValidationException>(() => StandardPackageLoader.LoadDirectory(folder.Path, limits)).Errors, e => e.Rule == rule);
            }
        }

        [Theory]
        [InlineData("bom")]
        [InlineData("utf16")]
        [InlineData("invalid")]
        public void LoaderRejectsBomAndUnsupportedEncodings(string encoding)
        {
            var files = PackageFiles();
            files["package.json"] = encoding == "bom" ? new byte[] { 0xef, 0xbb, 0xbf }.Concat(files["package.json"]).ToArray()
                : encoding == "utf16" ? Encoding.Unicode.GetBytes(Encoding.UTF8.GetString(files["package.json"])) : new byte[] { 0xff, 0xfe };
            Assert.Throws<GenerationValidationException>(() => Load(files));
            using var directory = new PackageFolder(files);
            Assert.Throws<GenerationValidationException>(() => StandardPackageLoader.LoadDirectory(directory.Path));
        }

        [Theory]
        [InlineData("missing-resource")]
        [InlineData("coverage")]
        [InlineData("missing-type")]
        [InlineData("missing-naming")]
        [InlineData("missing-widget")]
        [InlineData("missing-part")]
        [InlineData("bad-hash")]
        [InlineData("unlisted")]
        public void PackageIntegrityAndReferencesAreChecked(string fault)
        {
            var files = PackageFiles();
            if (fault == "missing-resource") files.Remove("sources/motor.scl");
            if (fault == "unlisted") files["unexpected.txt"] = Encoding.UTF8.GetBytes("extra");
            if (fault == "bad-hash") files["sources/state.scl"] = Encoding.UTF8.GetBytes("changed");
            if (fault == "coverage" || fault == "missing-type")
            {
                var library = Object("library");
                if (fault == "coverage") library["types"]![0]!["implementations"]![0]!["releases"] = ">=20";
                else library["types"]![0]!["id"] = "unknown";
                files["library.json"] = Encoding.UTF8.GetBytes(library.ToJsonString()); UpdateInventory(files);
            }
            if (fault == "missing-naming" || fault == "missing-widget")
            {
                var rule = Object("rule");
                if (fault == "missing-naming") rule["emit"]![1]!["name"] = "{{naming.missing(device)}}";
                else rule["emit"]![3]!["template"] = "missing.json";
                files["rule.json"] = Encoding.UTF8.GetBytes(rule.ToJsonString()); UpdateInventory(files);
            }
            if (fault == "missing-part") { var manifest = JsonNode.Parse(Encoding.UTF8.GetString(files["package.json"]))!; manifest["parts"]!["naming"] = "missing.json"; files["package.json"] = Encoding.UTF8.GetBytes(manifest.ToJsonString()); }
            Assert.NotEmpty(Assert.Throws<GenerationValidationException>(() => Load(files)).Errors);
        }

        [Fact]
        public void MachineParametersUseDeviceTypeSchemaAndSignalRoles()
        {
            var package = Load(PackageFiles());
            var machine = Object("machine"); machine["devices"]![0]!["params"]!["powerKw"] = -1;
            Assert.Contains(Assert.Throws<GenerationValidationException>(() => package.ValidateMachine(machine.ToJsonString())).Errors, e => e.Path == "/devices/0/params/powerKw" && e.Rule == "minimum");
            machine = Object("machine"); machine["devices"]![0]!["io"]!["unknown"] = "auto";
            Assert.Contains(Assert.Throws<GenerationValidationException>(() => package.ValidateMachine(machine.ToJsonString())).Errors, e => e.Rule == "reference");
        }

        [Fact]
        public void ConditionalSchemasRejectIncompleteImplementationsAndExistingTargets()
        {
            var library = Object("library"); library["types"]![0]!["implementations"]![0]!.AsObject().Remove("files");
            Assert.Contains(GenerationSchemas.Validate("library", CanonicalJson.Parse(library.ToJsonString())), e => e.Rule == "required");
            var machine = Object("machine"); machine["target"]!["project"]!["mode"] = "existing";
            Assert.Contains(GenerationSchemas.Validate("machine", CanonicalJson.Parse(machine.ToJsonString())), e => e.Path == "/target/project/softwarePath");
            var package = Object("package"); package["version"] = "01.0.0";
            Assert.Contains(GenerationSchemas.Validate("package", CanonicalJson.Parse(package.ToJsonString())), e => e.Rule == "pattern");
            var hmi = Object("hmi"); hmi["profiles"]![0]!["width"] = 0;
            Assert.Contains(GenerationSchemas.Validate("hmi", CanonicalJson.Parse(hmi.ToJsonString())), e => e.Rule == "minimum");
        }

        [Fact]
        public void ExactReleaseSelectionsAndDerivedPackagesRoundTrip()
        {
            var files = PackageFiles(); var library = Object("library");
            library["types"]![0]!["implementations"]![0]!["releases"] = new JsonArray("14sp1", "15.1", "16", "17", "18", "19", "20", "21");
            files["library.json"] = Encoding.UTF8.GetBytes(library.ToJsonString()); UpdateInventory(files);
            Assert.Equal(2, Load(files).GetPart<LibraryPart>("library.json").Types.Count);
            library["types"]![0]!["implementations"]![0]!["releases"] = ">=14\t<=21";
            files["library.json"] = Encoding.UTF8.GetBytes(library.ToJsonString()); UpdateInventory(files);
            Assert.Equal(2, Load(files).GetPart<LibraryPart>("library.json").Types.Count);
            var manifest = Object("package"); manifest["id"] = "fixture.state";
            manifest["extends"] = new JsonObject { ["package"] = "fixture.basic", ["version"] = "^1.0" };
            var derived = new Dictionary<string, byte[]> { ["package.json"] = Encoding.UTF8.GetBytes(manifest.ToJsonString()) };
            Assert.Equal("fixture.basic", Load(derived).Manifest.Extends!.Package);
        }

        [Fact]
        public void InvalidDefaultsRangesAndStateReferencesAreTypedFailures()
        {
            var rule = Object("rule"); rule["params"]!["properties"]!["powerKw"]!["default"] = -1;
            Assert.Contains(Assert.Throws<GenerationValidationException>(() => GenerationDocuments.Load<DeviceTypeRule>(rule.ToJsonString())).Errors, e => e.Path == "/params/properties/powerKw/default");
            var modes = Object("modes"); modes["models"]![0]!["transitions"]![0]!["to"] = "absent";
            Assert.Contains(Assert.Throws<GenerationValidationException>(() => GenerationDocuments.Load<ModesPart>(modes.ToJsonString())).Errors, e => e.Rule == "reference");
            var structure = Object("structure"); structure["numberRanges"]![0]!["end"] = 1;
            Assert.Contains(Assert.Throws<GenerationValidationException>(() => GenerationDocuments.Load<StructurePart>(structure.ToJsonString())).Errors, e => e.Rule == "range");
        }

        [Fact]
        public void ResourceAndManifestCopiesCannotChangeLoadedPackageHash()
        {
            var package = Load(PackageFiles()); var hash = package.ContentHash;
            var bytes = package.ReadFile("sources/motor.scl"); bytes[0] = 0;
            package.Manifest.Id = "changed";
            Assert.Equal("fixture.basic", package.Manifest.Id);
            Assert.NotEqual(0, package.ReadFile("sources/motor.scl")[0]);
            Assert.Equal(hash, package.ContentHash);
            using var output = new MemoryStream(); package.WriteCanonicalZip(output); output.Position = 0;
            Assert.Equal(hash, StandardPackageLoader.LoadZip(output).ContentHash);
        }

        private static JsonNode Reverse(JsonNode node)
        {
            if (node is JsonObject obj) { var result = new JsonObject(); foreach (var pair in obj.Reverse()) result.Add(pair.Key, pair.Value == null ? null : Reverse(pair.Value)); return result; }
            if (node is JsonArray array) return new JsonArray(array.Select(n => n == null ? null : Reverse(n)).ToArray());
            return node.DeepClone();
        }

        private static Dictionary<string, byte[]> PackageFiles()
        {
            var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            var manifest = Object("package"); var parts = manifest["parts"]!.AsObject();
            foreach (var name in new[] { "naming", "structure", "hardware", "library", "modes", "alarms", "hmi", "checks" })
            { files[name + ".json"] = Encoding.UTF8.GetBytes(Example(name)); parts[name] = name + ".json"; }
            files["rule.json"] = Encoding.UTF8.GetBytes(Example("rule")); parts["rules"] = new JsonArray("rule.json");
            files["sources/motor.scl"] = Encoding.UTF8.GetBytes("// Self-authored fixture\n");
            files["sources/state.scl"] = Encoding.UTF8.GetBytes("// Self-authored fixture state\n");
            files["sources/screen.json"] = Encoding.UTF8.GetBytes("{\"width\":1280}");
            files["sources/widget.json"] = Encoding.UTF8.GetBytes("{\"kind\":\"group\"}");
            files["package.json"] = Encoding.UTF8.GetBytes(manifest.ToJsonString());
            UpdateInventory(files); return files;
        }

        private static void UpdateInventory(IDictionary<string, byte[]> files)
        {
            var manifest = JsonNode.Parse(Encoding.UTF8.GetString(files["package.json"]))!;
            var inventory = new JsonArray();
            foreach (var file in files.Where(f => f.Key != "package.json").OrderBy(f => f.Key, StringComparer.Ordinal))
            {
                var bytes = file.Key.EndsWith(".json", StringComparison.Ordinal) ? CanonicalJson.Encode(CanonicalJson.Parse(file.Value)) : file.Value;
                inventory.Add(new JsonObject { ["path"] = file.Key, ["sha256"] = CanonicalJson.HashBytes(bytes) });
            }
            manifest["files"] = inventory;
            files["package.json"] = Encoding.UTF8.GetBytes(manifest.ToJsonString());
        }

        private static StandardPackage Load(IDictionary<string, byte[]> files, PackageLoadLimits? limits = null)
        { using var stream = Zip(files); return StandardPackageLoader.LoadZip(stream, limits); }

        private static MemoryStream Zip(IEnumerable<KeyValuePair<string, byte[]>> files, int? attributes = null)
        {
            var stream = new MemoryStream();
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
                foreach (var file in files)
                {
                    var entry = zip.CreateEntry(file.Key);
                    if (attributes.HasValue) entry.ExternalAttributes = attributes.Value;
                    using var output = entry.Open(); output.Write(file.Value, 0, file.Value.Length);
                }
            stream.Position = 0; return stream;
        }

        private sealed class PackageFolder : IDisposable
        {
            public string Path { get; }
            internal PackageFolder(IDictionary<string, byte[]> files)
            {
                var root = new DirectoryInfo(AppContext.BaseDirectory);
                while (root != null && !File.Exists(System.IO.Path.Combine(root.FullName, "AGENTS.md"))) root = root.Parent;
                Path = System.IO.Path.Combine(root!.FullName, "bin-build", "generation-tests", Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(Path);
                foreach (var file in files)
                {
                    var destination = System.IO.Path.Combine(Path, file.Key.Replace('/', System.IO.Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(destination)!); File.WriteAllBytes(destination, file.Value);
                }
            }
            public void Dispose() => Directory.Delete(Path, true);
        }
    }
}
