using System.Text.Json;
using System.IO.Compression;
using System.Xml.Linq;
using TiaMcp.PlcWorker;
using TiaMcp.Logic.Exchange;
using Xunit;

namespace TiaMcp.Adapters.Contracts.Tests;

public sealed class ProjectTextProbeTests
{
    [Theory]
    [InlineData("14sp1", "1", "unsupported")]
    [InlineData("15.1", "1", "unsupported")]
    [InlineData("16", null, "disabled")]
    [InlineData("21", "0", "disabled")]
    public void Admission_precedes_the_export(string release, string? enabled, string expected)
    {
        var result = ProjectTextProbe.Scan(enabled, release, "MCP_RC_PLC", "", "Owner", "Kind", "List", "Alarm",
            _ => throw new Exception("unexpected export"), _ => throw new Exception("unexpected journal entry"));
        Assert.Equal(expected, result["status"]);
        Assert.False(result.ContainsKey("textListRows"));
    }

    [Theory]
    [InlineData("16")] [InlineData("17")] [InlineData("18")] [InlineData("19")] [InlineData("20")] [InlineData("21")]
    public void Only_the_selected_PLC_and_categories_contribute_to_counts(string release)
    {
        var sequence = new List<string>();
        string? exportedFile = null;
        var result = ProjectTextProbe.Scan("1", release, "/Devices/MCP_RC_PLC", "Texts", "Owner", "Kind", "List", "Alarm", file => {
            sequence.Add("export");
            exportedFile = file;
            var table = new TableData(["Owner", "Kind", "en-US"], [
                [@"Devices\MCP_RC_PLC\Lists", "List", "private text"],
                ["Devices/MCP_RC_PLC", "Alarm", "private text"],
                ["Devices/MCP_RC_PLC", "Alarm", "other text"],
                ["Devices/MCP_RC_PLC2/Lists", "List", "Devices/MCP_RC_PLC"],
                ["Devices/MCP_RC_PLC", "Comment", "Alarm"]]);
            File.WriteAllBytes(file, TableFile.WriteXlsx(table, "Texts"));
            return new HardwareAddressingReply { Meta = { ["apiCallSuccess"] = true } };
        }, sequence.Add, "en-US", "de-DE");
        Assert.Equal(new[] { "Project.ExportProjectTexts", "export" }, sequence);
        Assert.Equal("ok", result["status"]);
        Assert.Equal(4, result["ownerRows"]);
        Assert.Equal(1, result["textListRows"]);
        Assert.Equal(2, result["alarmTextRows"]);
        Assert.Equal(new[] { "Owner", "Kind", "en-US" }, Assert.IsType<Dictionary<string, string[]>>(result["worksheetHeaders"])["Texts"]);
        Assert.DoesNotContain("private", JsonSerializer.Serialize(result));
        Assert.False(Directory.Exists(Path.GetDirectoryName(exportedFile)));
    }

    [Theory]
    [InlineData(false, "export-failed")] [InlineData(true, "unknown-columns")]
    public void An_unusable_export_has_no_counts_and_its_temporary_file_is_removed(bool exported, string status)
    {
        string? path = null;
        var reply = ProjectTextProbe.Scan("1", "16", "MCP_RC_PLC", "", "Owner", "Kind", "List", "Alarm", file => {
            path = file;
            File.WriteAllBytes(file, TableFile.WriteXlsx(new TableData(["Translation"], [["private text"]]), "Texts"));
            return new HardwareAddressingReply { Meta = { ["apiCallSuccess"] = exported } };
        }, _ => { });
        Assert.Equal(status, reply["status"]);
        if (exported) Assert.Equal(new[] { ProjectTextProbe.OtherCell }, Assert.IsType<Dictionary<string, string[]>>(reply["worksheetHeaders"])["Texts"]);
        else Assert.False(reply.ContainsKey("worksheetHeaders"));
        Assert.False(reply.ContainsKey("textListRows"));
        Assert.False(reply.ContainsKey("alarmTextRows"));
        Assert.NotNull(path);
        Assert.False(File.Exists(path));
        Assert.False(Directory.Exists(Path.GetDirectoryName(path)));
    }

    [Theory]
    [InlineData(true, "layout-only")]
    [InlineData(false, "select-sheet")]
    public void Multi_sheet_discovery_exposes_only_names_and_row_one(bool layoutOnly, string status)
    {
        var reply = ProjectTextProbe.Scan("1", "16", "MCP_RC_PLC", "", layoutOnly ? "" : "Owner", layoutOnly ? "" : "Kind",
            layoutOnly ? "" : "List", layoutOnly ? "" : "Alarm", file => {
                WriteWorkbook(file);
                return new HardwareAddressingReply { Meta = { ["apiCallSuccess"] = true } };
            }, _ => { }, "en-US");
        Assert.Equal(status, reply["status"]);
        var headers = Assert.IsType<Dictionary<string, string[]>>(reply["worksheetHeaders"]);
        Assert.Equal(2, headers.Count);
        Assert.Equal(new[] { ProjectTextProbe.OtherCell, ProjectTextProbe.OtherCell, "en-US" }, headers[" Texte & 文本 "]);
        var counts = Assert.IsType<Dictionary<string, int>>(reply["worksheetColumnCounts"]);
        Assert.Equal(3, counts[" Texte & 文本 "]);
        Assert.Equal(0, counts["Notes"]);
        Assert.Empty(headers["Notes"]);
        Assert.DoesNotContain("private", JsonSerializer.Serialize(reply));
        Assert.False(reply.ContainsKey("ownerRows"));
    }

    [Fact]
    public void Row_one_user_text_is_masked_and_only_known_or_requested_titles_are_echoed()
    {
        var reply = ProjectTextProbe.Scan("1", "17", "MCP_RC_PLC", "", "", "", "", "", file => {
            File.WriteAllBytes(file, TableFile.WriteXlsx(new TableData(["private header", "zh-CN", "private Owner"], [["a", "b", "c"]]),
                "Texts"));
            return new HardwareAddressingReply { Meta = { ["apiCallSuccess"] = true } };
        }, _ => { }, "en-US", "zh-CN");
        Assert.Equal("layout-only", reply["status"]);
        Assert.Equal(new[] { ProjectTextProbe.OtherCell, "zh-CN", ProjectTextProbe.OtherCell },
            Assert.IsType<Dictionary<string, string[]>>(reply["worksheetHeaders"])["Texts"]);
        Assert.Equal(3, Assert.IsType<Dictionary<string, int>>(reply["worksheetColumnCounts"])["Texts"]);
        Assert.DoesNotContain("private", JsonSerializer.Serialize(reply));
    }

    private static void WriteWorkbook(string path)
    {
        File.WriteAllBytes(path, TableFile.WriteXlsx(new TableData([" Owner ", "类别", "en-US"], [["private owner", "Alarm", "private text"]]), " Texte & 文本 "));
        using var zip = ZipFile.Open(path, ZipArchiveMode.Update);
        void AddSibling(string part, string element, params (XName Key, string Value)[] attributes)
        {
            var entry = zip.GetEntry(part)!;
            XDocument xml;
            using (var input = entry.Open()) xml = XDocument.Load(input);
            var original = xml.Descendants().First(e => e.Name.LocalName == element);
            var copy = new XElement(original);
            foreach (var attribute in attributes) copy.SetAttributeValue(attribute.Key, attribute.Value);
            original.AddAfterSelf(copy);
            entry.Delete();
            using var output = zip.CreateEntry(part).Open();
            xml.Save(output);
        }
        XNamespace rel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        AddSibling("xl/workbook.xml", "sheet", ("name", "Notes"), ("sheetId", "2"), (rel + "id", "rId2"));
        AddSibling("xl/_rels/workbook.xml.rels", "Relationship", ("Id", "rId2"), ("Target", "worksheets/sheet2.xml"));
        AddSibling("[Content_Types].xml", "Override", ("PartName", "/xl/worksheets/sheet2.xml"),
            ("ContentType", "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"));
        using var writer = new StreamWriter(zip.CreateEntry("xl/worksheets/sheet2.xml").Open());
        writer.Write("""<worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData>""");
        writer.Write("""<row r="2"><c r="A2" t="inlineStr"><is><t>private data</t></is></c></row></sheetData></worksheet>""");
    }
}
