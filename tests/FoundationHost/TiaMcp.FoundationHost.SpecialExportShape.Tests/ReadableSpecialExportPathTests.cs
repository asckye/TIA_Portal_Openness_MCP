using Siemens.Engineering.SW.WatchAndForceTables;
using TiaMcp.Adapters;
using TiaMcp.Adapters.Contracts;
using Xunit;

public sealed class ReadableSpecialExportPathTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("表\\一")]
    public void Watch_selection_refuses_null_and_raw_backslashes_before_export(string? path)
    {
        var engine = new PlcFoundationEngine();
        var table = new PlcWatchTable { Name = "表\\一" };
        engine.Software.WatchAndForceTableGroup.WatchTables.Add(table);
        var error = Assert.Throws<AdapterPreconditionException>(() =>
            engine.ExportPlcWatchTable("CPU", path!, "unused.xml", dryRun: false));
        Assert.Equal("watchTableName", error.ParamName);
        Assert.Equal(0, table.ExportCalls);
        engine.ExportPlcWatchTable("CPU", "表%5C一", "unused.xml", dryRun: false);
        Assert.Equal(1, table.ExportCalls);
    }
}
