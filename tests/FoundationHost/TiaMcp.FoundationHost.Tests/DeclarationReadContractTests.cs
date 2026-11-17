using System.Text.Json.Nodes;
using TiaMcp.FoundationHost;
using Xunit;

namespace TiaMcp.FoundationHost.Tests;

public sealed class DeclarationReadContractTests
{
    [Theory]
    [InlineData("组 一/表%2F50%25")]
    [InlineData("%E7%BB%84%20%E4%B8%80/%E8%A1%A8%2F50%25")]
    public void Saved_and_readable_table_selectors_accept_readable_rows(string table)
    {
        var rows = Rows("组 一/表%2F50%25", "中文 标记/50%", "A%2FB");
        var result = DeclarationReadContract.Validate("ListPlcSystemConstants", table, rows);
        Assert.True(JsonNode.DeepEquals(rows, result));
        Assert.Equal("组 一/表%2F50%25/中文 标记%2F50%25", (string?)result[0]?["Path"]);
    }

    [Theory]
    [InlineData("中文 单元")]
    [InlineData("单元/50%")]
    public void Unit_declarations_use_the_shared_path_encoding(string unit)
    {
        string prefix = "units/" + TiaMcp.Adapters.Contracts.ObjectPathEncoding.EncodeSegment(unit) + "/tag-tables/默认 表";
        var rows = Rows(prefix, "标记");
        Assert.Single(DeclarationReadContract.Validate("ListPlcSystemConstants", "默认%20表", rows, unit));
        Assert.Single(DeclarationReadContract.Validate("ListPlcSystemConstants", "", rows, includeUnits: true));
    }

    private static JsonArray Rows(string prefix, params string[] names)
    {
        var rows = new JsonArray();
        foreach (var name in names)
            rows
                .Add(new JsonObject {
                    ["Name"] = name, ["Path"] = prefix + "/" + TiaMcp.Adapters.Contracts.ObjectPathEncoding.EncodeSegment(name), ["Kind"] = "system-constant",
                        ["DataType"] = "Pip", ["Value"] = "1"
                });
        return rows;
    }

    [Fact]
    public void Raw_chinese_table_name_accepts_the_canonical_encoded_rows()
    {
        // Worker rows use readable paths; callers may retain saved fully encoded selectors.
        string encoded = "默认变量表";
        var rows = DeclarationReadContract.Validate("ListPlcSystemConstants", "默认变量表", Rows(encoded, "无", "自动更新", "PIP 1"));
        Assert.Equal(3, rows.Count);
        Assert.Equal(3, DeclarationReadContract.Validate("ListPlcSystemConstants", Uri.EscapeDataString(encoded), Rows(encoded, "无", "自动更新", "PIP 1")).Count);
    }

    [Fact]
    public void Rows_from_another_table_or_mixed_prefixes_are_refused()
    {
        string table = "A%20B";
        string encoded = TiaMcp.Adapters.Contracts.ObjectPathEncoding.EncodeSegment(table);
        Assert.Throws<InvalidDataException>(() => DeclarationReadContract.Validate("ListPlcSystemConstants", "默认变量表", Rows("Other", "无")));
        var mixed = Rows(encoded, "无");
        Assert.Single(DeclarationReadContract.Validate("ListPlcSystemConstants", table, mixed));
        Assert.Single(DeclarationReadContract.Validate("ListPlcSystemConstants", table, Rows("A B", "PIP 1")));
        mixed.Add(Rows("A B", "PIP 1")[0]!.DeepClone());
        Assert.Throws<InvalidDataException>(() => DeclarationReadContract.Validate("ListPlcSystemConstants", table, mixed));
        Assert.Single(DeclarationReadContract.Validate("ListPlcSystemConstants", "Group%2F默认变量表", Rows("Group%2F默认变量表", "无")));
        // A grouped path is never matched through its raw-name encoding.
        Assert.Throws<InvalidDataException>(() => DeclarationReadContract.Validate("ListPlcSystemConstants", "Group/默认变量表",
            Rows(TiaMcp.Adapters.Contracts.ObjectPathEncoding.EncodeSegment("Group/默认变量表"), "无")));
    }
}
