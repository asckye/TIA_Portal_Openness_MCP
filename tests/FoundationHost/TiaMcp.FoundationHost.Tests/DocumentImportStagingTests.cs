using System.Security.Cryptography;
using System.Text;
using TiaMcp.Adapters;
using TiaMcp.Logic.ModelContextProtocol;
using Xunit;

public sealed class DocumentImportStagingTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "tia-utf8-stage-" + Guid.NewGuid().ToString("N").Substring(0, 8));

    private ImportStagingStore Store(string release = "21")
    {
        Directory.CreateDirectory(root);
        return new ImportStagingStore(root, release, Guid.NewGuid().ToString("N"));
    }

    private const string Code = "DATA_BLOCK 示例数据块\nVAR\n\"Value\" : Int;\nEND_VAR\n\"Value\" := 20;\nEND_DATA_BLOCK\n";

    [Theory]
    [InlineData("20")]
    [InlineData("21")]
    public void Staged_utf8_document_bytes_pass_admission_without_bom(string release)
    {
        using var store = Store(release);
        var files = new[] { new StagedTextFile { FileName = "示例数据块.s7dcl", Kind = "s7dcl", Content = Code },
            new StagedTextFile { FileName = "示例数据块.s7res", Kind = "s7res", Content = "MultiLingualTexts: []\n" } };
        Assert.False((bool?)store.Stage(files)["executed"]);
        Assert.Empty(Directory.GetDirectories(root));
        var result = store.Stage(files, false);
        var entry = result["files"]!.AsArray().Single(row => (string?)row!["kind"] == "s7dcl")!;
        Assert.Equal("utf-8", (string?)entry["encoding"]);
        byte[] bytes = File.ReadAllBytes((string)entry["path"]!);
        Assert.Equal(Encoding.UTF8.GetBytes(Code), bytes);
        Assert.False(bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }));
        Assert.Equal("示例数据块", PlcDocumentDeclaration.Parse(bytes));
        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), (string?)entry["sha256"]);
    }

    [Theory]
    [InlineData("scl", "F.scl")]
    [InlineData("udt", "F.udt")]
    [InlineData("udt", "F.scl")]
    public void Other_external_sources_still_require_ascii(string kind, string name)
    {
        using var store = Store();
        Assert.Throws<ArgumentException>(() => store.Stage(new[] { new StagedTextFile { FileName = name, Kind = kind, Content = Code } }));
    }

    [Fact]
    public void Utf8_document_staging_still_refuses_bom_and_invalid_unicode()
    {
        using var store = Store();
        foreach (string content in new[] { "\ufeff" + Code, Code + "\ud800", Code + '\u2028' })
            Assert.ThrowsAny<ArgumentException>(() => store.Stage(new[] {
                new StagedTextFile { FileName = "Example.s7dcl", Kind = "s7dcl", Content = content } }));
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
