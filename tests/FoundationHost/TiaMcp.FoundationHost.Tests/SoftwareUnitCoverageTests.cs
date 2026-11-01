using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters;
using TiaMcp.Adapters.Contracts.Candidates;
using TiaMcp.FoundationHost;
using TiaMcp.Logic.V4;
using Xunit;

public sealed class SoftwareUnitCoverageTests
{
    [Flags]
    private enum SoftwareFlags { None = 0, IgnoreStructuralChanges = 2, IgnoreMissingReferencedObjects = 8, IgnoreUnitAttributes = 32 }
    [Flags]
    private enum CultureFlags { None = 0, Override = 1, SkipInactiveCultures = 4, ActivateInactiveCultures = 16 }
    [Theory]
    [InlineData("14sp1", false, false, false)]
    [InlineData("15.1", true, false, false)]
    [InlineData("16", true, true, false)]
    [InlineData("17", true, true, false)]
    [InlineData("18", true, true, false)]
    [InlineData("19", true, true, true)]
    [InlineData("20", true, true, true)]
    [InlineData("21", true, true, true)]
    public void OptionAvailabilityFollowsExactReleaseAndConsumingOverload(string release, bool software, bool units, bool cultures)
    {
        Assert.Null(PlcImportOptionPolicy.Unsupported(release, "block", null, null));
        foreach (var kind in new[] { "block", "UDT" })
        {
            Assert.Equal(software, PlcImportOptionPolicy.Unsupported(release, kind, new[] { "IgnoreStructuralChanges", "IgnoreMissingReferencedObjects" }, null) == null);
            Assert.Equal(units, PlcImportOptionPolicy.Unsupported(release, kind, new[] { "IgnoreUnitAttributes" }, null) == null);
        }
        foreach (var culture in new[] { "SkipInactiveCultures", "ActivateInactiveCultures" })
            foreach (var kind in new[] { "block", "UDT", "TagTable" })
                Assert.Equal(cultures, PlcImportOptionPolicy.Unsupported(release, kind, null, new[] { culture }) == null);
        Assert.Equal(units, PlcImportOptionPolicy.Unsupported(release, "TagTable", null, null, "Unit_1") == null);
        Assert.NotNull(PlcImportOptionPolicy.Unsupported(release, "TagTable", new[] { "IgnoreStructuralChanges" }, null));
        Assert.NotNull(PlcImportOptionPolicy.Unsupported(release, "block", null, new[] { "SkipInactiveCultures", "ActivateInactiveCultures" }));
        Assert.NotNull(PlcImportOptionPolicy.Unsupported(release, "block", new[] { "IgnoreStructuralChanges", "IgnoreStructuralChanges" }, null));
        Assert.NotNull(PlcImportOptionPolicy.Unsupported(release, "block", new[] { "ignoreUnitAttributes" }, null));
        Assert.NotNull(PlcImportOptionPolicy.Unsupported(release, "block", null, new[] { "Override" }));
        var names = software ? new[] { "IgnoreStructuralChanges", "IgnoreMissingReferencedObjects" }.Concat(units ? new[] { "IgnoreUnitAttributes" } : Array.Empty<string>()).ToArray() : Array.Empty<string>();
        Assert.Equal(software ? SoftwareFlags.IgnoreStructuralChanges | SoftwareFlags.IgnoreMissingReferencedObjects | (units ? SoftwareFlags.IgnoreUnitAttributes : SoftwareFlags.None) : SoftwareFlags.None,
            PlcImportOptionPolicy.Combine(SoftwareFlags.None, names));
        foreach (bool overwrite in new[] { false, true })
            foreach (var culture in cultures ? new[] { "SkipInactiveCultures", "ActivateInactiveCultures" } : new[] { "" })
                Assert.Equal((overwrite ? CultureFlags.Override : CultureFlags.None) | (culture == "" ? CultureFlags.None : Enum.Parse<CultureFlags>(culture)),
                    PlcImportOptionPolicy.Combine(overwrite ? CultureFlags.Override : CultureFlags.None, culture == "" ? Array.Empty<string>() : new[] { culture }));
    }

    [Theory]
    [InlineData("14sp1", "ImportPlcBlock", "unitName", "Unit_1")]
    [InlineData("15.1", "ImportPlcType", "swImportOptions", "IgnoreUnitAttributes")]
    [InlineData("18", "ImportPlcBlock", "importOptions", "ActivateInactiveCultures")]
    [InlineData("21", "ImportPlcTagTable", "swImportOptions", "IgnoreMissingReferencedObjects")]
    [InlineData("21", "ImportPlcProgramFromDirectory", "swImportOptions", "Imaginary")]
    public void UnsupportedRequestsReturnTypedCapabilityErrorBeforeWorkerAdmission(string release, string tool, string parameter, string value)
    {
        var args = new Dictionary<string, JsonElement> { [parameter] = parameter.EndsWith("Options", StringComparison.Ordinal)
            ? JsonSerializer.SerializeToElement(new[] { value }) : JsonSerializer.SerializeToElement(value) };
        Assert.Equal(ErrorCode.UnsupportedCapability, FoundationV4Tool.SoftwareUnitCapabilityError(release, tool, args)!.Code);
    }

    [Fact]
    public void RealFormatPreviewBindsUnitOptionsAndOriginalBytes()
    {
        string root = Path.GetFullPath(Path.Combine("bin-build", "su1-fixtures", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        string fixture = Path.Combine(Repository(), "tests/Engine/TiaMcp.Engine.Tests/Fixtures/PlcRender/Parts/BranchShapes.xml");
        string path = Path.Combine(root, "BranchShapes.xml"); File.Copy(fixture, path);
        var original = File.ReadAllBytes(path);
        var adapter = new PreviewAdapter();
        var request = new PlcImportRequest { SoftwarePath = "devices/PLC/CPU", InputPath = path, MaxItems = 1,
            UnitName = "Unit A/B", BlockGroupPath = "Nested", SwImportOptions = new[] { "IgnoreMissingReferencedObjects", "IgnoreUnitAttributes" },
            ImportOptions = new[] { "SkipInactiveCultures" } };
        var session = new PlcImportSession();
        var preview = session.Run(adapter, "21", "ImportPlcBlock", "test", request);
        Assert.True(preview.Ok, V4Json.Serialize(preview));
        var data = JsonNode.Parse(preview.Data!.Value.GetRawText())!;
        Assert.Equal("Unit A/B", (string?)data["unitName"]);
        Assert.Equal("Nested", (string?)data["targets"]![0]!["target"]!["groupPath"]);
        Assert.Equal("SkipInactiveCultures", (string?)data["importOptions"]![0]);
        Assert.Equal(2, data["swImportOptions"]!.AsArray().Count);
        string hash = (string)data["plan"]!["hash"]!;
        Assert.Equal(PlcImportSession.ByteHash(original), (string?)data["files"]![0]!["sha256"]);
        request.UnitName = "Unit_2";
        var stale = session.Run(adapter, "21", "ImportPlcBlock", "test", request, "apply", true, hash, adapter.ReadIdentity().ProjectFile);
        Assert.Equal(ErrorCode.PlanStale, stale.Error!.Code);
        Assert.Equal(0, adapter.ImportCalls);
        Assert.Equal(original, File.ReadAllBytes(path));
        request.UnitName = "Unit A/B"; request.ImportOptions = new[] { "ActivateInactiveCultures" };
        var changed = session.Run(adapter, "21", "ImportPlcBlock", "test", request);
        Assert.True(changed.Ok);
        Assert.NotEqual(hash, changed.Data!.Value.GetProperty("plan").GetProperty("hash").GetString());
    }

    [Fact]
    public void DeclarationContractsPreserveUnitQualifiedIdentityAndRefuseDuplicates()
    {
        JsonObject Row(string prefix) => new() { ["Name"]="Same",["Path"]=prefix+"/Same",["Kind"]="tag",["DataType"]="Bool",["Value"]="%M0.0" };
        var rows = new JsonArray(Row("root/tag-tables/Tags"), Row("units/A/tag-tables/Tags"), Row("units/B/tag-tables/Tags"));
        Assert.Equal(3, DeclarationReadContract.Validate("ReadPlcTags", "Tags", rows, includeUnits:true).Count);
        Assert.Single(DeclarationReadContract.Validate("ReadPlcTags", "Tags", new JsonArray(Row("units/A/tag-tables/Tags")), "A"));
        Assert.Throws<InvalidDataException>(() => DeclarationReadContract.Validate("ReadPlcTags", "Tags", rows, "A"));
        Assert.Throws<InvalidDataException>(() => DeclarationReadContract.Validate("ReadPlcTags", "Tags", new JsonArray(Row("units/A/tag-tables/Tags"),Row("units/A/tag-tables/Tags")), includeUnits:true));
        Assert.Throws<InvalidDataException>(() => DeclarationReadContract.Validate("ReadPlcTags", "", new JsonArray(Row("units/A/tag-tables/../Other")), includeUnits:true));
    }

    [Fact]
    public void BatchExportPreviewNamesTheUnitAndBindsItWithoutChangingSoftwareIdentity()
    {
        string directory = Path.GetFullPath(Path.Combine("bin-build", "su1-export-fixtures", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(directory); int exports = 0;
        var source = new[] { new PlcBatchExportSource { Path="Nested/Block", Consistent=true, Export=_=>exports++ } };
        PlcBatchExportResult Preview(string unit) => PlcBatchExportPolicy.Run("blocks", @"C:\Fixture.ap21", "devices/PLC/CPU", "", true,
            directory, 128, true, "", source, () => throw new InvalidOperationException("Preview must not require a write."), unitName:unit);
        var first = Preview("A"); var second = Preview("B");
        Assert.Equal("A", Assert.IsType<PlcScopedBatchExportResult>(first).UnitName); Assert.Equal("B", Assert.IsType<PlcScopedBatchExportResult>(second).UnitName);
        Assert.Equal("devices/PLC/CPU", first.SoftwarePath);
        Assert.Equal(first.Items[0].ObjectPath, second.Items[0].ObjectPath);
        Assert.NotEqual(first.InventoryHash, second.InventoryHash);
        Assert.Equal(0, exports);
    }

    [Fact]
    public void SingleExportPreviewShowsSelectedUnitAndKeepsTheRootEnvelopeShape()
    {
        var payload = new JsonObject { ["Executed"]=false,["ProjectFile"]=@"C:\Fixture.ap21",["UnitName"]="A",["OutputFile"]=@"C:\export.xml" };
        JsonObject Meta(JsonObject result) => result.Single(field => string.Equals(field.Key,"meta",StringComparison.OrdinalIgnoreCase)).Value!.AsObject();
        var scoped = Meta(V17MutationEnvelope.Wrap("ExportBlock", "Mutation", payload, true));
        Assert.Equal("A", scoped.Single(field => string.Equals(field.Key,"unitName",StringComparison.OrdinalIgnoreCase)).Value!.GetValue<string>());
        payload["UnitName"] = "";
        var root = Meta(V17MutationEnvelope.Wrap("ExportBlock", "Mutation", payload, true));
        Assert.DoesNotContain(root, field => string.Equals(field.Key,"unitName",StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(root, field => string.Equals(field.Key,"swImportOptions",StringComparison.OrdinalIgnoreCase));
    }

    private static string Repository()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "AGENTS.md"))) return directory.FullName;
        throw new DirectoryNotFoundException("Repository fixture root not found.");
    }

    private sealed class PreviewAdapter : IPlcImportAdapter
    {
        internal int ImportCalls;
        public CandidateIdentity ReadIdentity() => new(23, DateTimeOffset.Parse("2026-11-01T00:00:00Z"), @"C:\Fixture.ap21", 1);
        public IReadOnlyList<PlcImportInput> ReadInputs(string release, string tool, PlcImportRequest request, IDictionary<string, Stream> locks)
            => CandidateImportFiles.Read(release, tool, request, locks);
        public IReadOnlyList<PlcImportObject> ReadInventory() => Array.Empty<PlcImportObject>();
        public string TargetGroupIdentity(PlcImportObject target) => "reviewed-group";
        public bool SupportsOverwrite(PlcImportInput input) => true;
        public void BeforeImport(PlcImportInput input) { }
        public PlcImportObject Import(PlcImportInput input, bool overwrite) { ImportCalls++; throw new InvalidOperationException("Stale requests must not import."); }
        public string ReadContent(PlcImportInput input, PlcImportObject imported) => throw new InvalidOperationException("No import expected.");
    }
}
