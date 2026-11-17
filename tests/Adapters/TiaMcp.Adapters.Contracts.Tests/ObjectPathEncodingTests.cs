using System.Globalization;
using TiaMcp.Adapters.Contracts;
using Xunit;

namespace TiaMcp.Adapters.Contracts.Tests;

public sealed class ObjectPathEncodingTests
{
    [Theory]
    [InlineData("中文块", "中文块")]
    [InlineData("Group A", "Group A")]
    [InlineData("50%", "50%25")]
    [InlineData("A/B", "A%2FB")]
    [InlineData("A\\B", "A%5CB")]
    [InlineData("A%2FB", "A%252FB")]
    [InlineData(" +#?:*\"<>|&='[]{}😀é", " +#?:*\"<>|&='[]{}😀é")]
    public void Only_path_grammar_characters_are_escaped(string name, string expected)
    {
        Assert.Equal(expected, ObjectPathEncoding.EncodeSegment(name));
        Assert.Equal(name, ObjectPathEncoding.DecodeSegment(expected));
    }

    public static IEnumerable<object[]> Controls() => Enumerable.Range(0, 32).Append(127)
        .Select(value => new object[] { ((char)value).ToString(), "%" + value.ToString("X2", CultureInfo.InvariantCulture) });

    [Theory, MemberData(nameof(Controls))]
    public void Every_ascii_control_is_escaped_and_round_trips(string name, string expected)
    {
        Assert.Equal(expected, ObjectPathEncoding.EncodeSegment(name));
        Assert.Equal(name, ObjectPathEncoding.DecodeSegment(expected));
    }

    [Theory]
    [InlineData("%E4%B8%AD%E6%96%87%E5%9D%97", "中文块")]
    [InlineData("中文块", "中文块")]
    [InlineData("Group%20A/A%2FB", "Group A/A%2FB")]
    [InlineData("50%", "50%25")]
    [InlineData("A%252FB", "A%252FB")]
    [InlineData("A%5CB", "A%5CB")]
    public void Saved_and_raw_paths_normalize_once_per_segment(string input, string expected)
        => Assert.Equal(expected, ObjectPathEncoding.NormalizePath(input));

    [Theory]
    [InlineData(null)]
    [InlineData("A\\B")]
    public void Normalization_refuses_null_and_unencoded_backslashes(string? input)
    {
        var error = Assert.Throws<AdapterPreconditionException>(() => ObjectPathEncoding.NormalizePath(input!, "groupPath"));
        Assert.True(error.IsArgument);
        Assert.Equal("groupPath", error.ParamName);
    }

    [Fact]
    public void Temporary_file_names_retain_full_encoding()
    {
        const string name = "中文块 :*?\"<>|/%\\";
        const string expected = "%E4%B8%AD%E6%96%87%E5%9D%97%20%3A%2A%3F%22%3C%3E%7C%2F%25%5C";
        Assert.Equal(expected, ObjectFileNameEncoding.Encode(name));
        Assert.Equal(name, Uri.UnescapeDataString(expected));
        Assert.DoesNotContain(ObjectFileNameEncoding.Encode(name), character => Path.GetInvalidFileNameChars().Contains(character));
    }
}
