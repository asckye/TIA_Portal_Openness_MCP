using TiaMcp.ReleaseTool;
using Xunit;

namespace TiaMcp.ReleaseTool.Tests;

[Trait("Category", "StaticChecks")]
public sealed class PythonCheckChangesTests
{
    [Theory]
    [InlineData("scripts/checks/transport.py", "check_outer")]
    [InlineData("tests/test_transport.py", "test_outer")]
    public void Each_removed_assertion_belongs_to_its_innermost_function(string path, string outer)
    {
        string source = $$"""
            class TestTransport:
                def {{outer}}(self):
                    assert ready
                    async def nested():
                        def leaf():
            # A comment's indentation does not end the nested function.
                            assert ready
                        assert ready
                    assert ready
            """;
        var removed = CheckLoosening.Scan([new(path, path, source, source.Replace("assert ready", "pass"))]);
        string prefix = "TestTransport/" + outer, hash = CheckLoosening.Hash("assert ready");
        Assert.All(removed, finding => Assert.Equal("assertion-removed", finding.Kind));
        Assert.Equal(new[] { $"{prefix}:{hash}:1", $"{prefix}:{hash}:2", $"{prefix}/nested:{hash}:1", $"{prefix}/nested/leaf:{hash}:1" }.Order(),
            removed.Select(finding => finding.Identifier).Order());
    }

    [Theory]
    [InlineData("", "test_connected")]
    [InlineData("class TestConnection:\n", "TestConnection/test_connected")]
    public void Removing_a_test_keeps_its_existing_full_body_approval_key(string container, string name)
    {
        const string path = "tests/test_connection.py";
        string indent = container.Length == 0 ? "" : "    ";
        string body = $"{indent}def test_connected():\n{indent}    def helper():\n{indent}        assert ready\n{indent}    helper()\n";
        var removed = CheckLoosening.Scan([new(path, path, container + body, "")]);
        Assert.Equal(name + ":" + CheckLoosening.Hash(body), Assert.Single(removed, finding => finding.Kind == "test-removed").Identifier);
    }

    [Fact]
    public void Moving_an_assertion_into_a_helper_cannot_satisfy_its_outer_scope()
    {
        const string path = "tests/test_connection.py";
        const string before = "def test_connected():\n    def probe():\n        pass\n    assert ready\n";
        const string after = "def test_connected():\n    def probe():\n        assert ready\n";
        var finding = Assert.Single(CheckLoosening.Scan([new(path, path, before, after)]));
        Assert.Equal("test_connected:" + CheckLoosening.Hash("assert ready") + ":1", finding.Identifier);
    }

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
