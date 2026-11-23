using System.IO.Compression;
using TiaMcp.Adapters.Native.Options;
using Xunit;

namespace TiaMcp.Adapters.Contracts.Tests;

public sealed class CfcInventoryTests
{
    [Fact]
    public void Inventory_reads_charts_from_an_archive_with_a_doctype()
    {
        string path = Path.GetTempFileName();
        try
        {
            using (var archive = new ZipArchive(File.OpenWrite(path), ZipArchiveMode.Create))
            using (var writer = new StreamWriter(archive.CreateEntry("charts.xml").Open()))
                writer.Write("<!DOCTYPE Charts><Charts><Chart Name='Fixture'/></Charts>");
            var inventory = CfcInventory.InspectExport(path);
            Assert.Equal("charts.xml", Assert.Single(inventory.Entries));
            Assert.Equal("Fixture", Assert.Single(inventory.Charts));
        }
        finally { File.Delete(path); }
    }
}
