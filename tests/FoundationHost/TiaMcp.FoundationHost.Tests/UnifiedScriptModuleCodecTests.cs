using System.Text;
using TiaMcpServer.Siemens.Services;
using Xunit;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

public sealed class UnifiedScriptModuleCodecTests : IDisposable
{
    private readonly string directory = Path.Combine(AppContext.BaseDirectory, "script-pairs", Guid.NewGuid().ToString("N"));

    public UnifiedScriptModuleCodecTests() => Directory.CreateDirectory(directory);

    [Theory]
    [InlineData("Navigation")]
    [InlineData("状态模块")]
    [InlineData("It's #global")]
    [InlineData("true")]
    [InlineData("false")]
    [InlineData("null")]
    [InlineData("~")]
    [InlineData("123")]
    [InlineData("#module")]
    public void Compose_round_trips_exact_text_and_minimal_UTF8_pair(string name)
    {
        const string code = "export function Status() {\n  return '状态';\n}\n";
        var pair = UnifiedScriptFileCodec.ComposePair(directory, name, code);
        var readback = UnifiedScriptFileCodec.ReadPair(directory, name);
        Assert.Equal(name + ".hmi.js", pair.JsName);
        Assert.Equal(name + ".hmi.yml", pair.YamlName);
        Assert.Equal(code, pair.Text);
        Assert.Equal(code, readback.Text);
        Assert.Equal(Encoding.UTF8.GetBytes(code), pair.Js);
        Assert.Equal(pair.Yaml, readback.Yaml);
        Assert.False(pair.Bom);
        Assert.False(pair.Yaml.Take(3).SequenceEqual(new byte[] { 0xef, 0xbb, 0xbf }));
        var yaml = Encoding.UTF8.GetString(pair.Yaml);
        Assert.DoesNotContain("\r", yaml);
        Assert.Contains("\n", yaml);
        if (name is "true" or "null" or "~" or "123")
            Assert.Equal(Encoding.UTF8.GetBytes("ScriptModules:\n  \"" + name + "\":\n    ScriptFile: \"" + name + ".hmi.js\"\n"), pair.Yaml);
        Assert.DoesNotContain("\n...\n", yaml);
        var stream = new YamlStream(); stream.Load(new StringReader(yaml));
        var root = Assert.IsType<YamlMappingNode>(Assert.Single(stream.Documents).RootNode);
        var modules = Assert.IsType<YamlMappingNode>(Assert.Single(root.Children).Value);
        Assert.Equal("ScriptModules", Assert.IsType<YamlScalarNode>(Assert.Single(root.Children).Key).Value);
        var module = Assert.Single(modules.Children);
        Assert.Equal(name, Assert.IsType<YamlScalarNode>(module.Key).Value);
        Assert.Equal(ScalarStyle.DoubleQuoted, Assert.IsType<YamlScalarNode>(module.Key).Style);
        var entry = Assert.Single(Assert.IsType<YamlMappingNode>(module.Value).Children);
        Assert.Equal("ScriptFile", Assert.IsType<YamlScalarNode>(entry.Key).Value);
        Assert.Equal(name + ".hmi.js", Assert.IsType<YamlScalarNode>(entry.Value).Value);
        Assert.Equal(ScalarStyle.DoubleQuoted, Assert.IsType<YamlScalarNode>(entry.Value).Style);
        Assert.Equal(2, Directory.GetFileSystemEntries(directory).Length);
    }

    public static IEnumerable<object[]> InvalidNames()
    {
        foreach (var name in new[] { "", new string('n', 129), " leading", "trailing ", "\tname", "name\n", "a\0b", "a\u007fb" })
            yield return new object[] { name };
        foreach (char character in "/\\:*?\"<>|") yield return new object[] { "a" + character + "b" };
    }

    [Theory, MemberData(nameof(InvalidNames))]
    public void Compose_refuses_forbidden_names_without_writing_files(string name)
    {
        var error = Assert.Throws<ArgumentException>(() => UnifiedScriptFileCodec.ComposePair(directory, name, "export const x = 1;"));
        Assert.Equal("moduleName", error.ParamName);
        Assert.Empty(Directory.GetFileSystemEntries(directory));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Compose_refuses_nonempty_directory(bool subdirectory)
    {
        var existing = Path.Combine(directory, "existing");
        if (subdirectory) Directory.CreateDirectory(existing); else File.WriteAllText(existing, "keep");
        Assert.Throws<ArgumentException>(() => UnifiedScriptFileCodec.ComposePair(directory, "Status", "export const x = 1;"));
        Assert.Single(Directory.GetFileSystemEntries(directory));
        if (!subdirectory) Assert.Equal("keep", File.ReadAllText(existing));
    }

    public void Dispose()
    {
        Assert.StartsWith(Path.GetFullPath(AppContext.BaseDirectory), Path.GetFullPath(directory), StringComparison.OrdinalIgnoreCase);
        Directory.Delete(directory, true);
    }
}
