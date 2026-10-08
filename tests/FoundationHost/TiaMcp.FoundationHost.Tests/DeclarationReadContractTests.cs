using System.Text.Json.Nodes;
using TiaMcp.FoundationHost;
using Xunit;

namespace TiaMcp.FoundationHost.Tests;

public sealed class DeclarationReadContractTests
{
    private static JsonArray Rows(string prefix, params string[] names)
    {
        var rows = new JsonArray();
        foreach (var name in names)
            rows.Add(new JsonObject { ["Name"] = name, ["Path"] = prefix + "/" + Uri.EscapeDataString(name), ["Kind"] = "system-constant", ["DataType"] = "Pip", ["Value"] = "1" });
        return rows;
    }

    [Fact]
    public void Raw_chinese_table_name_accepts_the_canonical_encoded_rows()
    {
        // V14 SP1 VM: ListPlcSystemConstants(plc="PLC_1", table="默认变量表") is answered with encoded paths.
        string encoded = Uri.EscapeDataString("默认变量表");
        var rows = DeclarationReadContract.Validate("ReadPlcSystemConstants", "默认变量表", Rows(encoded, "无", "自动更新", "PIP 1"));
        Assert.Equal(3, rows.Count);
        Assert.Equal(3, DeclarationReadContract.Validate("ReadPlcSystemConstants", encoded, Rows(encoded, "无", "自动更新", "PIP 1")).Count);
    }

    [Fact]
    public void Rows_from_another_table_or_mixed_prefixes_are_refused()
    {
        string encoded = Uri.EscapeDataString("默认变量表");
        Assert.Throws<InvalidDataException>(() => DeclarationReadContract.Validate("ReadPlcSystemConstants", "默认变量表", Rows("Other", "无")));
        var mixed = Rows(encoded, "无");
        mixed.Add(Rows("默认变量表", "PIP 1")[0]!.DeepClone());
        Assert.Throws<InvalidDataException>(() => DeclarationReadContract.Validate("ReadPlcSystemConstants", "默认变量表", mixed));
        // A grouped path is never matched through its raw-name encoding.
        Assert.Throws<InvalidDataException>(() => DeclarationReadContract.Validate("ReadPlcSystemConstants", "Group/默认变量表",
            Rows(Uri.EscapeDataString("Group/默认变量表"), "无")));
    }
}
