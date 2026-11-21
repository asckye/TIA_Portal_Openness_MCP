using TiaMcp.FoundationHost;
using Xunit;

namespace TiaMcp.FoundationHost.Tests;

public sealed class PlcBatchGuidanceTests
{
    [Fact]
    public void Delete_confirmation_explains_the_hash_and_when_to_set_it()
    {
        using var worker = new HardwareAddressingAdmissionTests.UnboundWorker();
        using var session = new HardwareAddressingAdmissionTests.SessionFixture(worker, "20");
        var tool = LegacyHostToolRegistry.Create(worker, "20", false).Single(t => t.ProtocolTool.Name == "ManagePlcTableEntries");
        var text = tool.ProtocolTool.InputSchema.GetProperty("properties").GetProperty("confirmDelete").GetProperty("description").GetString();
        Assert.Contains("both preview and apply", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("new plan hash", text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, worker.Calls);
    }

    [Fact]
    public void Published_batch_descriptions_explain_the_import_order_and_export_directory()
    {
        using var worker = new HardwareAddressingAdmissionTests.UnboundWorker();
        using var session = new HardwareAddressingAdmissionTests.SessionFixture(worker, "20");
        var tools = LegacyHostToolRegistry.Create(worker, "20", false).ToDictionary(t => t.ProtocolTool.Name, StringComparer.Ordinal);
        Assert.Contains("complete importOrder", tools["ImportPlcBlocksFromDirectory"].ProtocolTool.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ordered manifest", tools["ImportPlcBlocksDocuments"].ProtocolTool.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("new directory", tools["ExportPlcBlockDocuments"].ProtocolTool.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, worker.Calls);
    }
}
