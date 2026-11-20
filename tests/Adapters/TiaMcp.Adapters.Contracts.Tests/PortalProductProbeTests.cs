using System.Text.Json;
using Xunit;

namespace TiaMcp.Adapters.Contracts.Tests;

public sealed class PortalProductProbeTests
{
    private sealed record Product(string Name, string Version, params Product[] Options);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("true")]
    [InlineData("0")]
    public void Worker_requires_the_exact_opt_in_before_acquiring_a_process(string? enabled)
    {
        int calls = 0;
        var reply = PortalProductProbe.Read<int, Product>(enabled, () => ++calls, _ => [], p => p.Options,
            p => p.Name, p => p.Version, _ => calls++);
        Assert.Equal("disabled", reply["status"]);
        Assert.Equal(0, calls);
    }

    [Fact]
    public void Reports_only_explicit_versions_and_journals_before_each_getter()
    {
        var sequence = new List<string>();
        T Read<T>(string member, T value)
        {
            Assert.Equal("before " + member, sequence.Last());
            sequence.Add(member);
            return value;
        }
        var portal = new Product("TIA Portal", "V20 Update 2", new Product("SIMATIC SiVArc", "20.1"));
        var other = new Product("private product name", "private version");
        var reply = PortalProductProbe.Read<int, Product>("1", () => Read("TiaPortal.GetCurrentProcess", 1),
            _ => Read("TiaPortalProcess.InstalledSoftware", new[] { portal, other }), p => Read("TiaPortalProduct.Options", p.Options),
            p => Read("TiaPortalProduct.Name", p.Name), p => Read("TiaPortalProduct.Version", p.Version), step => sequence.Add("before " + step));
        Assert.Equal("ok", reply["status"]);
        Assert.Equal("V20 Update 2", reply["tiaUpdateLevel"]);
        Assert.Equal("20.1", reply["sivarcProductVersion"]);
        Assert.Null(reply["installedPatch"]);
        Assert.Null(reply["sivarcLicenceState"]);
        Assert.DoesNotContain("private", JsonSerializer.Serialize(reply));
        Assert.Equal(new[] { "TiaPortal.GetCurrentProcess", "TiaPortalProcess.InstalledSoftware", "TiaPortalProduct.Name",
            "TiaPortalProduct.Version", "TiaPortalProduct.Options", "TiaPortalProduct.Name", "TiaPortalProduct.Version",
            "TiaPortalProduct.Name", "TiaPortalProduct.Options" }, Assert.IsType<List<string>>(reply["steps"]));
    }

    [Fact]
    public void A_failed_product_lookup_reports_the_exception_type_only()
    {
        var reply = PortalProductProbe.Read<int, Product>("1", () => 1, _ => throw new InvalidOperationException("private failure"),
            p => p.Options, p => p.Name, p => p.Version, _ => { });
        Assert.Equal("read-failed", reply["status"]);
        Assert.Equal(typeof(InvalidOperationException).FullName, reply["exceptionType"]);
        Assert.DoesNotContain("private", JsonSerializer.Serialize(reply));
    }
}
