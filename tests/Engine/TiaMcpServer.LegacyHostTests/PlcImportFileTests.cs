using System.Text;
using System.Xml.Linq;
using TiaMcp.Logic.V4;
using Xunit;

public sealed class PlcImportFileTests
{
    private static string Workspace()
    {
        string root = Path.GetFullPath(Path.Combine("bin-build", "P6-28", "file-fixtures", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root); return root;
    }
    private static byte[] Xml(string release, string kind = "FC", string value = "literal", bool bom = false)
    {
        var data = Encoding.UTF8.GetBytes("<Document><Engineering version=\"" + (release == "14sp1" ? "V14 SP1" : "V" + release) + "\"/><" + kind + " ID=\"1\"><AttributeList><Name>Object_1</Name><ProgrammingLanguage>LAD</ProgrammingLanguage><Number>7</Number></AttributeList><ObjectList><Text xml:space=\"preserve\">" + value + "</Text></ObjectList></" + kind + "></Document>");
        return bom ? new byte[] { 239, 187, 191 }.Concat(data).ToArray() : data;
    }
    [Theory]
    [InlineData("14sp1")] [InlineData("15.1")] [InlineData("16")] [InlineData("17")] [InlineData("18")] [InlineData("19")] [InlineData("20")] [InlineData("21")]
    public void ExactReleaseInputKeepsOriginalBomAndBytesAndHoldsAReadLock(string release)
    {
        string path = Path.Combine(Workspace(), "Object_1.xml"); byte[] bytes = Xml(release, "SW.Blocks.FC", bom: true); File.WriteAllBytes(path, bytes);
        var locks = new Dictionary<string, Stream>();
        try
        {
            var input = Assert.Single(PlcImportFiles.Read(release, "ImportPlcBlock", new() { InputPath = path }, locks));
            Assert.Equal("FC", input.Target.Kind); Assert.Equal(7, input.Target.Number); Assert.Equal(bytes, PlcImportSession.Read(locks[path]));
            Assert.Throws<IOException>(() => new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None));
            Assert.Equal(bytes, File.ReadAllBytes(path));
        }
        finally { foreach (var stream in locks.Values) stream.Dispose(); }
        Assert.Equal(bytes, File.ReadAllBytes(path));
    }
    [Fact]
    public void LockedAndUnreadableSourceIsRejectedWithoutARewrite()
    {
        string path = Path.Combine(Workspace(), "Object_1.xml"); byte[] bytes = Xml("21", "SW.Blocks.FC"); File.WriteAllBytes(path, bytes);
        using (var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.Throws<IOException>(() => PlcImportFiles.Read("21", "ImportPlcBlock", new() { InputPath = path }, new Dictionary<string, Stream>()));
        Assert.Equal(bytes, File.ReadAllBytes(path));
    }
    [Theory]
    [InlineData("missing")] [InlineData("duplicate")] [InlineData("case")] [InlineData("outside")] [InlineData("limit")]
    public void DirectoryManifestRequiresEverySelectedPathOnceAndKeepsTheLimit(string error)
    {
        string root = Workspace(); File.WriteAllBytes(Path.Combine(root, "a.xml"), Xml("21", "SW.Blocks.FC")); File.WriteAllBytes(Path.Combine(root, "b.xml"), Xml("21", "SW.Blocks.FC"));
        var request = new PlcImportRequest { InputPath = root, ImportOrder = new[] { "b.xml", "a.xml" } };
        if (error == "missing") request.ImportOrder = new[] { "a.xml" };
        if (error == "duplicate") request.ImportOrder = new[] { "a.xml", "a.xml" };
        if (error == "case") request.ImportOrder = new[] { "A.xml", "b.xml" };
        if (error == "outside") request.ImportOrder = new[] { "../a.xml", "b.xml" };
        if (error == "limit") request.MaxItems = 1;
        var locks = new Dictionary<string, Stream>();
        try { Assert.Throws<PlcImportRejection>(() => PlcImportFiles.Read("21", "ImportPlcBlocksFromDirectory", request, locks)); }
        finally { foreach (var stream in locks.Values) stream.Dispose(); }
    }
    [Fact]
    public void DirectoryOrderIsPreservedAndExactProducerCannotBeRepaired()
    {
        string root = Workspace(); File.WriteAllBytes(Path.Combine(root, "a.xml"), Xml("21", "SW.Blocks.FC")); File.WriteAllBytes(Path.Combine(root, "b.xml"), Xml("21", "SW.Blocks.FC"));
        var request = new PlcImportRequest { InputPath = root, ImportOrder = new[] { "b.xml", "a.xml" } }; var locks = new Dictionary<string, Stream>();
        try
        {
            Assert.Equal(new[] { "b.xml", "a.xml" }, PlcImportFiles.Read("21", "ImportPlcBlocksFromDirectory", request, locks).Select(i => Path.GetFileName(i.Path)));
            Assert.Equal(ErrorCode.UnsupportedCapability, Assert.Throws<PlcImportRejection>(() => PlcImportFiles.Read("20", "ImportPlcBlocksFromDirectory", request, locks)).Error.Code);
        }
        finally { foreach (var stream in locks.Values) stream.Dispose(); }
        Assert.Equal(Xml("21", "SW.Blocks.FC"), File.ReadAllBytes(Path.Combine(root, "a.xml")));
    }
    [Fact]
    public void ContentCanonicalizationPreservesLiteralWhitespaceMemberValuesAndWiring()
    {
        byte[] original = Xml("21", "SW.Blocks.FC", " literal ");
        Assert.NotEqual(PlcImportFiles.XmlHash(original), PlcImportFiles.XmlHash(Xml("21", "SW.Blocks.FC", "literal")));
        string code = "<Document><Engineering version=\"V21\"/><SW.Blocks.FC ID=\"1\"><AttributeList><Name>F</Name></AttributeList><ObjectList><SW.Blocks.CompileUnit ID=\"2\"><Wire UId=\"1\"><NameCon UId=\"2\" Name=\"in\"/></Wire><Part UId=\"2\"/></SW.Blocks.CompileUnit></ObjectList></SW.Blocks.FC></Document>";
        string different = code.Replace("Name=\"in\"", "Name=\"out\"");
        Assert.NotEqual(PlcImportFiles.XmlHash(Encoding.UTF8.GetBytes(code)), PlcImportFiles.XmlHash(Encoding.UTF8.GetBytes(different)));
        Assert.Equal(PlcImportFiles.XmlHash(Encoding.UTF8.GetBytes(code)), PlcImportFiles.XmlHash(Encoding.UTF8.GetBytes(code.Replace("ID=\"1\"", "ID=\"9\"").Replace("ID=\"2\"", "ID=\"8\""))));
    }
    [Theory]
    [InlineData("SW.Types.PlcStruct", "ImportPlcType", "UDT")] [InlineData("SW.Tags.PlcTagTable", "ImportPlcTagTable", "TagTable")]
    public void TypeAndTagContentsKeepEveryValue(string rootKind, string tool, string kind)
    {
        string path = Path.Combine(Workspace(), "object.xml"); byte[] bytes = Xml("21", rootKind, "42"); File.WriteAllBytes(path, bytes); var locks = new Dictionary<string, Stream>();
        try { Assert.Equal(kind, Assert.Single(PlcImportFiles.Read("21", tool, new() { InputPath = path }, locks)).Target.Kind); }
        finally { foreach (var stream in locks.Values) stream.Dispose(); }
        Assert.NotEqual(PlcImportFiles.XmlHash(bytes), PlcImportFiles.XmlHash(Xml("21", rootKind, "43")));
    }
    [Theory]
    [InlineData("20", true)] [InlineData("21", false)] [InlineData("21", true)]
    public void DocumentPairsKeepOriginalFilesAndReturnContentHashes(string release, bool resource)
    {
        string root = Workspace(); byte[] bytes = new byte[] { 239, 187, 191 }.Concat(Encoding.UTF8.GetBytes("DATA_BLOCK Object_1\r\nVAR\r\nValue : Int := 42;\r\nEND_VAR\r\nEND_DATA_BLOCK")).ToArray();
        File.WriteAllBytes(Path.Combine(root, "Object_1.s7dcl"), bytes);
        if (resource) File.WriteAllText(Path.Combine(root, "Object_1.s7res"), "resource-original");
        var locks = new Dictionary<string, Stream>();
        try
        {
            var input = Assert.Single(PlcImportFiles.Read(release, "ImportPlcBlockDocuments", new() { InputPath = root, FileNameWithoutExtension = "Object_1" }, locks));
            Assert.Equal("GlobalDB", input.Target.Kind); Assert.Equal(resource ? 2 : 1, input.Files.Length);
            Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(root, "Object_1.s7dcl"))); Assert.Equal(64, input.ContentHash.Length);
        }
        finally { foreach (var stream in locks.Values) stream.Dispose(); }
    }
    [Fact]
    public void V20MissingResourceAndDtdOrMixedObjectXmlAreRejected()
    {
        string root = Workspace(); File.WriteAllText(Path.Combine(root, "Object_1.s7dcl"), "DATA_BLOCK Object_1\nEND_DATA_BLOCK");
        var locks = new Dictionary<string, Stream>();
        try { Assert.Throws<PlcImportRejection>(() => PlcImportFiles.Read("20", "ImportPlcBlockDocuments", new() { InputPath = root, FileNameWithoutExtension = "Object_1" }, locks)); }
        finally { foreach (var stream in locks.Values) stream.Dispose(); }
        Assert.ThrowsAny<Exception>(() => PlcImportFiles.XmlHash(Encoding.UTF8.GetBytes("<!DOCTYPE Document [<!ENTITY x SYSTEM 'file:///not-read'>]><Document>&x;</Document>")));
        Assert.ThrowsAny<Exception>(() => PlcImportFiles.XmlHash(Encoding.UTF8.GetBytes("<Document><SW.Blocks.FC/><SW.Blocks.FB/></Document>")));
    }
    [Fact]
    public void IllegalDocumentBasenameIsRefusedBeforeOpeningAnySource()
    {
        var locks = new Dictionary<string, Stream>();
        var error = Assert.Throws<PlcImportRejection>(() => PlcImportFiles.Read("21", "ImportPlcBlockDocuments",
            new() { InputPath = Workspace(), FileNameWithoutExtension = "../Other" }, locks));
        Assert.Equal(ErrorCode.InvalidArgument, error.Error.Code); Assert.Empty(locks);
    }

}
