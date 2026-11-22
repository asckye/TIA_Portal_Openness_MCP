using TiaMcp.ReleaseTool;
using Xunit;

namespace TiaMcp.ReleaseTool.Tests;

[Trait("Category", "StaticChecks")]
public sealed class PythonCheckChangesTests
{
    [Fact]
    public void NestedHelperCannotConsumeTheTopLevelCheck()
    {
        const string path = "scripts/checks/transport.py";
        const string check = "def check_stdio(result):\n    assert result.returncode == 0\n";
        const string helper = "async def simulate():\n    def check_stdio(result):\n        return result\n    return check_stdio\n";
        Assert.Empty(CheckLoosening.Scan([new(path, path, check, helper + check)]));
        string nestedOnly = helper.Replace("return result", "assert result.returncode == 0");
        var removed = Assert.Single(CheckLoosening.Scan([new(path, path, check, nestedOnly)]));
        Assert.Equal("assertion-removed", removed.Kind);
        Assert.StartsWith("check_stdio:", removed.Identifier);
    }

    [Theory]
    [InlineData("TestConnection")]
    [InlineData("simulate")]
    public void AClassTestMovedInsideAFunctionIsStillRemoved(string function)
    {
        const string path = "tests/test_connection.py";
        const string method = "    def test_connected(self):\n        assert self.connected\n";
        var findings = CheckLoosening.Scan([new(path, path, "class TestConnection:\n" + method, $"def {function}():\n" + method)]);
        Assert.Equal("test-removed", Assert.Single(findings).Kind);
    }
}
