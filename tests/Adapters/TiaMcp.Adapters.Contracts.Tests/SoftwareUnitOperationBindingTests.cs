using System.Text.Json;
using ModelContextProtocol;
using TiaMcp.Adapters.Contracts;
using TiaMcp.PlcWorker;
using Xunit;

public sealed class SoftwareUnitOperationBindingTests
{
    [Fact]
    public void ScopedRepliesKeepEvidenceThroughTheExistingWorkerAndHostCodecs()
    {
        object Create(string name)
        {
            var type = typeof(IOpennessAdapter).Assembly.GetType("TiaMcp.Adapters.Contracts." + name, true)!;
            var value = Activator.CreateInstance(type, true)!;
            type.GetProperty("UnitName")!.SetValue(value, "Unit A/B");
            return value;
        }
        object[] replies = { Create("PlcScopedMutationResult"), Create("PlcScopedBatchImportResult"), Create("PlcScopedBatchExportResult") };
        replies[0].GetType().GetProperty("SwImportOptions")!.SetValue(replies[0], new[] { "IgnoreUnitAttributes" });
        replies[1].GetType().GetProperty("ImportOptions")!.SetValue(replies[1], new[] { "SkipInactiveCultures" });
        foreach (var reply in replies)
        {
            using var worker = JsonDocument.Parse(WorkerJson.Serialize(reply));
            Assert.Equal("Unit A/B", worker.RootElement.GetProperty("UnitName").GetString());
            using var host = JsonDocument.Parse(JsonSerializer.Serialize(reply, reply.GetType(), McpJsonUtilities.DefaultOptions));
            var name = McpJsonUtilities.DefaultOptions.PropertyNamingPolicy?.ConvertName("UnitName") ?? "UnitName";
            Assert.Equal("Unit A/B", host.RootElement.GetProperty(name).GetString());
        }
        using var mutation = JsonDocument.Parse(WorkerJson.Serialize(replies[0]));
        Assert.Equal("IgnoreUnitAttributes", mutation.RootElement.GetProperty("SwImportOptions")[0].GetString());
        using var import = JsonDocument.Parse(WorkerJson.Serialize(replies[1]));
        Assert.Equal("SkipInactiveCultures", import.RootElement.GetProperty("ImportOptions")[0].GetString());
    }

    public sealed class Facade
    {
        public string Unit = "";
        public string[]? SoftwareOptions;
        public string[]? CultureOptions;
        public bool Overwrite;
        public bool DryRun;
        public string ListTags(string plc, string table) => "legacy";
        public string ListTagsScoped(string plc, string table, string unitName = "", bool includeUnits = false)
        { Unit = unitName; return includeUnits ? "all" : "selected"; }
        public string ImportBlocks(string softwarePath, string groupPath, string importPath, bool overwrite = false, bool dryRun = true) => "legacy";
        public string ImportBlocksScoped(string softwarePath, string groupPath, string importPath, bool overwrite = false, bool dryRun = true,
            string unitName = "", string[]? swImportOptions = null, string[]? importOptions = null)
        { Unit = unitName; SoftwareOptions = swImportOptions; CultureOptions = importOptions; Overwrite = overwrite; DryRun = dryRun; return "scoped"; }
        public string ReadState() => "managed";
        public string ReadStateScoped() => throw new InvalidOperationException("Unreviewed alias must not be selected.");
    }

    [Fact]
    public void ActualModuleRegistrationBindsScopeArgumentsWithoutExposingHelpersOrChangingLegacySignatures()
    {
        var methods = WorkerOperationModule.Register(new[] { new WorkerOperationModule("", typeof(Facade),
            new[] { "ListTags", "ImportBlocks", "ReadState" }) });
        Assert.Equal(new[] { "ImportBlocks", "ListTags", "ReadState" }, methods.Keys.OrderBy(name => name));
        Assert.Equal(2, typeof(Facade).GetMethod("ListTags")!.GetParameters().Length);
        Assert.Equal(5, typeof(Facade).GetMethod("ImportBlocks")!.GetParameters().Length);
        var facade = new Facade();
        object? Invoke(string name, object arguments) => methods[name].Invoke(facade,
            WorkerJson.Arguments(methods[name].GetParameters(), WorkerJson.ParseArguments(JsonSerializer.Serialize(arguments))));
        Assert.Equal("selected", Invoke("ListTags", new { plc = "PLC", table = "Tags", unitName = "Unit A/B" }));
        Assert.Equal("Unit A/B", facade.Unit);
        Assert.Equal("all", Invoke("ListTags", new { plc = "PLC", table = "", includeUnits = true }));
        Assert.Equal("scoped", Invoke("ImportBlocks", new { softwarePath = "PLC", groupPath = "Nested", importPath = "Block.xml",
            unitName = "Unit A/B", swImportOptions = new[] { "IgnoreUnitAttributes" }, importOptions = new[] { "SkipInactiveCultures" } }));
        Assert.Equal("Unit A/B", facade.Unit);
        Assert.Equal(new[] { "IgnoreUnitAttributes" }, facade.SoftwareOptions);
        Assert.Equal(new[] { "SkipInactiveCultures" }, facade.CultureOptions);
        Assert.False(facade.Overwrite);
        Assert.True(facade.DryRun);
        Assert.Equal("scoped", Invoke("ImportBlocks", new { softwarePath = "PLC", groupPath = "", importPath = "Block.xml" }));
        Assert.Equal("", facade.Unit);
        Assert.Null(facade.SoftwareOptions);
        Assert.Null(facade.CultureOptions);
        Assert.Equal("managed", Invoke("ReadState", new { }));
    }
}
