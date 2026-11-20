extern alias caxfixture;
extern alias legacycax;

using System.Text;
using Cax = caxfixture::Siemens.Engineering.Cax;
using CaxEngine = caxfixture::TiaMcp.Adapters.PlcFoundationEngine;
using LegacyCax = legacycax::Siemens.Engineering.Cax;
using LegacyEngine = legacycax::TiaMcp.Adapters.PlcFoundationEngine;
using Xunit;

namespace TiaMcp.Adapters.Contracts.Tests;

public sealed class CaxTransferResultTests
{
    [Fact]
    public void Legacy_importer_can_acquire_its_own_exclusive_scope_after_admission()
    {
        var provider = new LegacyCax.CaxProvider(new(LegacyCax.TransferResultState.Error, 1, 0, []));
        var engine = new LegacyEngine("17", provider);
        var directory = Directory.CreateTempSubdirectory("cax-");
        string source = Path.Combine(directory.FullName, "input.aml");
        string log = Path.Combine(directory.FullName, "import.log");
        try
        {
            File.WriteAllText(source, """<CAEXFile SchemaVersion="2.15" FileName="input.aml"><InstanceHierarchy Name="Project"/></CAEXFile>""");
            engine.ImportHardwareAml(source, log);
            Assert.Equal(0, engine.WriteAdmissions);
            Assert.Equal(0, provider.ImportCalls);
            using (provider.ExclusiveAccess())
                Assert.Throws<InvalidOperationException>(() => engine.ImportHardwareAml(source, log, confirmImport: true, dryRun: false));
            var reply = engine.ImportHardwareAml(source, log, confirmImport: true, dryRun: false);
            Assert.Equal(2, engine.WriteAdmissions);
            Assert.Equal(1, provider.ImportCalls);
            Assert.Equal(false, reply.Meta["nativeResult"]);
            Assert.Equal("ImportFailed", reply.Meta["status"]);
            Assert.Equal("Device import failed.", Assert.Single(Assert.IsType<List<string>>(reply.Meta["logExcerpt"])));
        }
        finally { directory.Delete(true); }
    }

    [Theory, InlineData("gbk"), InlineData("utf8"), InlineData("utf16-bom"), InlineData("utf8-cut")]
    public void Legacy_log_decodes_bom_then_strict_utf8_then_ansi(string format)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var gbk = Encoding.GetEncoding(936);
        const string message = "设备导入失败";
        byte[] bytes = format switch {
            "gbk" => gbk.GetBytes(message),
            "utf16-bom" => Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(message)).ToArray(),
            "utf8-cut" => Encoding.UTF8.GetBytes(message + "\n" + new string('x', 4094 - Encoding.UTF8.GetByteCount(message) - 1) + "界"),
            _ => Encoding.UTF8.GetBytes(message)
        };
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, bytes);
            var lines = LegacyEngine.ReadLogExcerpt(new FileInfo(path), gbk);
            Assert.Equal(message, lines[0]);
            if (format == "utf8-cut") Assert.All(lines[1], c => Assert.Equal('x', c));
        }
        finally { File.Delete(path); }
    }

    [Theory, InlineData("19"), InlineData("20"), InlineData("21")]
    public void Transfer_result_import_passes_write_admission_and_runs_outside_exclusive_access(string release)
    {
        var result = new Cax.TransferResult(Cax.TransferResultState.Warning, 0, 1, []);
        var provider = new Cax.CaxProvider(result);
        using (provider.ExclusiveAccess())
            Assert.Throws<InvalidOperationException>(() => Import(release, provider));

        var reply = Import(release, provider);
        Assert.Equal(1, provider.ImportCalls);
        Assert.Equal("Warning", reply.Meta["nativeResult"]);
        Assert.True(reply.MayHaveChanged);
    }

    [Theory]
    [InlineData("19")]
    [InlineData("20")]
    [InlineData("21")]
    public void Returned_error_preserves_the_verdict_and_first_40_sanitized_messages(string release)
    {
        var children = Enumerable.Range(1, 65).Select(index => new Cax.TransferResultMessage(Cax.TransferResultState.Error,
            $@"Device {index} password=""private credential"" C:\private\source.aml", [])).ToList();
        var result = new Cax.TransferResult(Cax.TransferResultState.Error, 65, 1,
            [new(Cax.TransferResultState.Warning, "Inspect the devices", children)]);

        var reply = Import(release, result);

        Assert.False(reply.OutcomeUnknown);
        Assert.True(reply.MayHaveChanged);
        Assert.Equal(false, reply.Meta["operationSuccess"]);
        Assert.Equal(true, reply.Meta["apiCallSuccess"]);
        Assert.Equal(false, reply.Meta["dataComplete"]);
        Assert.Equal("ImportFailed", reply.Meta["status"]);
        Assert.Equal("Error", reply.Meta["nativeResult"]);
        Assert.Equal(65, reply.Meta["errorCount"]);
        Assert.Equal(1, reply.Meta["warningCount"]);
        Assert.False(reply.Meta.ContainsKey("diagnosticReadFailed"));
        Assert.Null(reply.Meta["logFile"]);
        var messages = Assert.IsType<List<string>>(reply.Meta["messages"]);
        Assert.Equal(40, messages.Count);
        Assert.Equal("Warning: Inspect the devices", messages[0]);
        for (int index = 1; index < messages.Count; index++)
        {
            Assert.StartsWith($"Error: Device {index} ", messages[index]);
            Assert.DoesNotContain("private", messages[index]);
            Assert.Contains("<path>", messages[index]);
        }
        var cleanup = Assert.IsType<Dictionary<string, object?>[]>(reply.Meta["cleanupRequired"]);
        Assert.Equal("inspect devices per log", Assert.Single(cleanup)["action"]);
    }

    [Fact]
    public void Log_keeps_the_https_url_and_redacts_local_paths()
    {
        const string url = "https://example.invalid/devices/report";
        var result = new Cax.TransferResult(Cax.TransferResultState.Error, 1, 0,
            [new(Cax.TransferResultState.Error, url + @" C:\private folder\source.aml " + url + "/after", [])]);
        var line = Assert.Single(Assert.IsType<List<string>>(Import("21", result).Meta["messages"]));
        Assert.Contains(url, line);
        Assert.Contains(url + "/after", line);
        Assert.DoesNotContain("private", line);
        Assert.Contains("<path>", line);
    }

    [Theory]
    [InlineData("file:///C:/Users/private/source.aml")]
    [InlineData("FILE://private-server/share/source%20file.aml")]
    [InlineData("file:///C:/Users/name/folder;private/source.aml")]
    public void File_uris_are_redacted_between_intact_web_urls(string file)
    {
        const string before = "https://example.invalid/devices?path=C:/public";
        const string after = "http://example.invalid/mirror/C:/public";
        var result = new Cax.TransferResult(Cax.TransferResultState.Error, 1, 0,
            [new(Cax.TransferResultState.Error, before + " " + file + " failed near " + after, [])]);
        Assert.Equal("Error: " + before + " <path> failed near " + after,
            Assert.Single(Assert.IsType<List<string>>(Import("21", result).Meta["messages"])));
    }

    [Fact]
    public void Long_transfer_message_is_bounded_to_four_KiB_of_valid_utf8()
    {
        var result = new Cax.TransferResult(Cax.TransferResultState.Error, 1, 0,
            [new(Cax.TransferResultState.Error, "Device " + new string('界', 2000), [])]);

        var reply = Import("21", result);

        var message = Assert.Single(Assert.IsType<List<string>>(reply.Meta["messages"]));
        Assert.StartsWith("Error: Device 界", message);
        Assert.DoesNotContain("\uFFFD", message);
        Assert.InRange(Encoding.UTF8.GetByteCount(message), 4094, 4096);
    }

    private static HardwareAddressingReply Import(string release, Cax.TransferResult result)
    {
        var provider = new Cax.CaxProvider(result);
        var reply = Import(release, provider);
        Assert.Equal(1, provider.ImportCalls);
        return reply;
    }

    private static HardwareAddressingReply Import(string release, Cax.CaxProvider provider)
    {
        var engine = new CaxEngine(release, provider);
        string directory = Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, "cax-" + Guid.NewGuid().ToString("N"))).FullName;
        string source = Path.Combine(directory, "input.aml");
        try
        {
            File.WriteAllText(source, """<CAEXFile SchemaVersion="2.15" FileName="input.aml"><InstanceHierarchy Name="Project"/></CAEXFile>""");
            var reply = engine.ImportHardwareAml(source, Path.Combine(directory, "import.log"), confirmImport: true, dryRun: false);
            Assert.Equal(1, engine.WriteAdmissions);
            return reply;
        }
        finally
        {
            File.Delete(source);
            Directory.Delete(directory);
        }
    }
}
