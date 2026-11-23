using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Adapters;
using TiaMcp.Adapters.Contracts;
using TiaMcp.FoundationHost;
using Xunit;

namespace TiaMcp.FoundationHost.Tests;

public sealed class ReadablePathContractTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("组\\表")]
    public void Table_selection_refuses_null_and_raw_backslashes(string? input)
    {
        var table = new { Path = "组%5C表", Name = "组\\表" };
        var error = Assert.Throws<AdapterPreconditionException>(() =>
            PlcExchangePolicy.SelectTable(new[] { table }, input!, item => item.Path, item => item.Name));
        Assert.Equal("table", error.ParamName);
        Assert.Same(table, PlcExchangePolicy.SelectTable(new[] { table }, "组%5C表", item => item.Path, item => item.Name));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("devices/站\\一/PLC")]
    public void Software_identity_checks_refuse_null_and_raw_backslashes(string? input)
    {
        const string path = "devices/站%5C一/PLC";
        var error = Assert.Throws<AdapterPreconditionException>(() => TiaOpenness.Shared.NativeExportPolicy.RequireSoftwarePath(input!, path, true));
        Assert.Equal("softwarePath", error.ParamName);
        Assert.Throws<AdapterPreconditionException>(() => TiaOpenness.Shared.NativeExportPolicy.ResolvedIdentityMatches(input!, input!));
        TiaOpenness.Shared.NativeExportPolicy.RequireSoftwarePath(path, path, true);
    }

    [Theory]
    [InlineData("组 一/表%2F50%25")]
    [InlineData("%E7%BB%84%20%E4%B8%80/%E8%A1%A8%2F50%25")]
    public void Table_lookup_accepts_saved_and_minimal_paths(string input)
    {
        var table = new { Path = "组 一/表%2F50%25", Name = "表/50%" };
        Assert.Same(table, PlcExchangePolicy.SelectTable(new[] { table }, input, item => item.Path, item => item.Name));
    }

    [Fact]
    public void Batch_software_identity_accepts_saved_spelling_and_still_refuses_aliases()
    {
        const string readable = "devices/中文 站/PLC";
        const string saved = "devices/%E4%B8%AD%E6%96%87%20%E7%AB%99/PLC";
        TiaOpenness.Shared.NativeExportPolicy.RequireSoftwarePath(saved, readable, true);
        Assert.True(TiaOpenness.Shared.NativeExportPolicy.ResolvedIdentityMatches(saved, readable));
        Assert.True(ExternalSourcePlanContract.SameSoftware(readable, saved));
        var alias = Assert.Throws<AdapterPreconditionException>(() => TiaOpenness.Shared.NativeExportPolicy.RequireSoftwarePath("PLC", readable, true));
        Assert.Contains(readable, alias.Message);
        Assert.Throws<AdapterPreconditionException>(() => TiaOpenness.Shared.NativeExportPolicy.RequireSoftwarePath("devices/Other/PLC", readable, true));
    }

    [Theory]
    [InlineData("组 一/表%2F50%25")]
    [InlineData("中文块")]
    public void Watch_rows_accept_readable_paths_and_still_refuse_noncanonical_worker_rows(string path)
    {
        JsonObject Payload(string value) => new() {
            ["SoftwarePath"] = "devices/站 一/PLC", ["ReleaseKey"] = "21", ["Scope"] = SupplementaryReadContract.WatchScope,
            ["Items"] = new JsonArray(value) };
        var result = SupplementaryReadContract.Wrap("ListPlcWatchTables", Payload(path), new JsonSerializerOptions());
        Assert.Equal(path, (string?)Assert.Single(result["Items"]!.AsArray()));
        string saved = string.Join("/", path.Split('/').Select(segment => Uri.EscapeDataString(ObjectPathEncoding.DecodeSegment(segment))));
        Assert.Throws<InvalidDataException>(() => SupplementaryReadContract.Wrap("ListPlcWatchTables", Payload(saved), new JsonSerializerOptions()));
    }

    [Fact]
    public void Inventory_hash_changes_refuse_a_preview_from_the_old_path_format()
    {
        string directory = Path.Combine(TestDirectory(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        int exports = 0, gates = 0;
        PlcBatchExportSource Source(string path) => new() { Path = path, Consistent = true, Export = _ => exports++ };
        PlcBatchExportResult Preview(string path, bool dry = true, string hash = "") => PlcBatchExportPolicy.Run(
            "blocks", "C:/fixture.ap21", "devices/PLC", "", true, directory, 128, dry, hash, new[] { Source(path) }, () => gates++);
        try
        {
            var old = Preview("%E4%B8%AD%E6%96%87%E5%9D%97%20A");
            var current = Preview("中文块 A");
            Assert.NotEqual(old.InventoryHash, current.InventoryHash);
            Assert.Throws<AdapterPreconditionException>(() => Preview("中文块 A", false, old.InventoryHash));
            PlcSpecialExportResult Plan(string path, bool dry = true, string hash = "") => PlcSpecialExportPolicy.Run(
                "watch-table", "21", "C:/fixture.ap21", "devices/PLC", path, Path.Combine(directory, "watch.xml"), true,
                dry, hash, () => gates++, _ => exports++);
            var currentPlan = Plan("中文块 A");
            string oldPlanHash = PlcSpecialExportPolicy.Hash(currentPlan.Kind, currentPlan.ReleaseKey, currentPlan.ProjectFile,
                currentPlan.SoftwarePath, "%E4%B8%AD%E6%96%87%E5%9D%97%20A", currentPlan.OutputFile, currentPlan.Scope,
                currentPlan.ExportOptions, currentPlan.MethodEvidence, currentPlan.SemanticsEvidence, currentPlan.Status);
            Assert.NotEqual(oldPlanHash, currentPlan.PlanHash);
            Assert.Throws<AdapterPreconditionException>(() => Plan("中文块 A", false, oldPlanHash));
            Assert.Equal(0, exports);
            Assert.Equal(0, gates);
            Assert.Empty(Directory.GetFileSystemEntries(directory));
        }
        finally { Directory.Delete(directory, true); }
    }

    private static string TestDirectory() => Path.Combine(
        TiaMcp.Engine.Tests.Shared.TestPaths.RepositoryRoot(AppContext.BaseDirectory), "bin-build", "refactor", "min-escape-fixtures");
}
