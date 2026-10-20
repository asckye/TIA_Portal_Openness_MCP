using TiaMcp.Adapters.Contracts;
using Xunit;

namespace TiaMcp.Adapters.Contracts.Tests;

// Decided 2026-10-19: device creation uses only the requested type identifier, version and name; refusals are typed and list
// the hardware-catalog candidates so the caller chooses again.
public sealed class DeviceCreationRulesTests
{
    [Theory]
    [InlineData("6ES7 513-1AM03-0AB0", "V3.0", "OrderNumber:6ES7 513-1AM03-0AB0/V3.0")]
    [InlineData("6ES7513-1AM03-0AB0", "V3.0", "OrderNumber:6ES7513-1AM03-0AB0/V3.0")]
    [InlineData("6ES7 513-1AM03-0AB0", "3.0", "OrderNumber:6ES7 513-1AM03-0AB0/3.0")]
    [InlineData("  6AV2 124-0GC01-0AX0 ", " 14.0.1.0 ", "OrderNumber:6AV2 124-0GC01-0AX0/14.0.1.0")]
    [InlineData("6ES7 211-1BE40-0XB0", "", "OrderNumber:6ES7 211-1BE40-0XB0")]
    [InlineData("OrderNumber:6ES7 211-1BE40-0XB0/V4.7", "", "OrderNumber:6ES7 211-1BE40-0XB0/V4.7")]
    [InlineData("GSD:GSDML-V2.34-FIXTURE.XML/DAP/DAP 1", "", "GSD:GSDML-V2.34-FIXTURE.XML/DAP/DAP 1")]
    public void The_requested_type_is_used_verbatim_without_spelling_or_version_variants(string orderNumber, string version, string expected)
        => Assert.Equal(expected, DeviceCreationRules.RequestedTypeIdentifier(orderNumber, version, "orderNumber", "version"));

    [Theory]
    [InlineData("", "V3.0", "orderNumber", true)]
    [InlineData("   ", "", "orderNumber", true)]
    [InlineData("OrderNumber:6ES7 211-1BE40-0XB0/V4.7", "V4.7", "version", true)]
    public void A_missing_type_or_a_doubled_version_is_refused_without_a_family_default(string orderNumber, string version, string parameter, bool isArgument)
    {
        var refusal = Assert.Throws<AdapterPreconditionException>(() => DeviceCreationRules.RequestedTypeIdentifier(orderNumber, version, "orderNumber", "version"));
        Assert.Equal(parameter, refusal.ParamName);
        Assert.Equal(isArgument, refusal.IsArgument);
        if (parameter == "orderNumber") Assert.Contains("No family default device is created.", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Keyword_creation_selects_only_an_exact_type_identifier()
    {
        var ranked = new[] { "OrderNumber:6AV2 123-2GB03-0AX0/17.0.0.0", "OrderNumber:6AV2 123-2GB03-0AX0/16.0.0.0", null, " " };
        Assert.Equal("OrderNumber:6AV2 123-2GB03-0AX0/16.0.0.0",
            DeviceCreationRules.ExactCandidate(ranked, " OrderNumber:6AV2 123-2GB03-0AX0/16.0.0.0 ", "hardware catalog"));
        // A keyword never selects the best-ranked row, and a case variant is not the requested identifier.
        foreach (var keyword in new[] { "KTP700 Basic PN", "orderNumber:6AV2 123-2GB03-0AX0/17.0.0.0", "OrderNumber:6AV2 123-2GB03-0AX0" })
        {
            var refusal = Assert.Throws<AdapterPreconditionException>(() => DeviceCreationRules.ExactCandidate(ranked, keyword, "hardware catalog"));
            Assert.Equal("keyword", refusal.ParamName);
            Assert.False(refusal.IsArgument);
            Assert.Contains("No device was created and no candidate was tried.", refusal.Message, StringComparison.Ordinal);
            Assert.Contains("Ranked hardware catalog candidates: OrderNumber:6AV2 123-2GB03-0AX0/17.0.0.0; OrderNumber:6AV2 123-2GB03-0AX0/16.0.0.0.", refusal.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Refusal_candidate_lists_are_bounded_and_explain_metadata_only_rows()
    {
        var many = Enumerable.Range(1, 20).Select(i => (string?)("GSD:FIXTURE-" + i.ToString("00", System.Globalization.CultureInfo.InvariantCulture) + ".XML/DAP/DAP " + i));
        var refusal = Assert.Throws<AdapterPreconditionException>(() => DeviceCreationRules.ExactCandidate(many, "AFM60A", "installed GSD/catalog", " 2 GSDML file row(s) carry no TypeIdentifier."));
        Assert.Contains("GSD:FIXTURE-12.XML/DAP/DAP 12; ...", refusal.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("FIXTURE-13", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("2 GSDML file row(s) carry no TypeIdentifier.", refusal.Message, StringComparison.Ordinal);
        var none = Assert.Throws<AdapterPreconditionException>(() => DeviceCreationRules.ExactCandidate(new string?[] { null }, "AFM60A", "installed GSD/catalog"));
        Assert.Contains("found no candidate with a TypeIdentifier", none.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_tia_refusal_lists_the_catalog_candidates_of_the_article_number()
    {
        Assert.Equal("6ES7 513-1AM03-0AB0", DeviceCreationRules.CatalogFilter("OrderNumber:6ES7 513-1AM03-0AB0/V9.9"));
        Assert.Equal("6ES7 513-1AM03-0AB0", DeviceCreationRules.CatalogFilter(" OrderNumber:6ES7 513-1AM03-0AB0 "));
        var candidates = DeviceCreationRules.CandidateText("6ES7 513-1AM03-0AB0", new[] { "OrderNumber:6ES7 513-1AM03-0AB0/V3.0", "OrderNumber:6ES7 513-1AM03-0AB0/V3.0", "OrderNumber:6ES7 513-1AM03-0AB0/V3.1" });
        Assert.Equal(" Hardware-catalog candidates for '6ES7 513-1AM03-0AB0': OrderNumber:6ES7 513-1AM03-0AB0/V3.0; OrderNumber:6ES7 513-1AM03-0AB0/V3.1. Choose one exact TypeIdentifier and call again.", candidates);
        var cause = new InvalidOperationException("Invalid TypeIdentifier");
        var refusal = DeviceCreationRules.TiaRefusal("OrderNumber:6ES7 513-1AM03-0AB0/V9.9", "PLC_2", cause.Message, candidates, "orderNumber/version", cause);
        Assert.False(refusal.IsArgument);
        Assert.Equal("orderNumber/version", refusal.ParamName);
        Assert.Same(cause, refusal.InnerException);
        Assert.Contains("No device was created (device inventory unchanged); no other type, version or name was tried.", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("OrderNumber:6ES7 513-1AM03-0AB0/V3.1", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("no entry for '6ES7 999'", DeviceCreationRules.CandidateText("6ES7 999", Array.Empty<string>()), StringComparison.Ordinal);
        Assert.Contains("no HardwareCatalog API (V18+)", DeviceCreationRules.NoCatalogText("OrderNumber:6ES7 513-1AM03-0AB0/V3.0"), StringComparison.Ordinal);
    }

    [Fact]
    public void Only_an_unchanged_inventory_proves_a_refusal()
    {
        var a = new object(); var b = new object(); var c = new object();
        Assert.True(DeviceCreationRules.SameInventory(new[] { a, b }, new[] { b, a }));
        Assert.False(DeviceCreationRules.SameInventory(new[] { a, b }, new[] { a, b, c }));
        Assert.False(DeviceCreationRules.SameInventory(new[] { a, b }, new[] { a, c }));
    }

    [Fact]
    public void Device_and_head_item_names_are_both_occupied()
    {
        var occupied = new[] { "S7-1500/ET200MP station_1", "PLC_1", "HMI_RT_1" };
        DeviceCreationRules.RequireFreeName(occupied, "PLC_2");
        foreach (var name in new[] { "plc_1", "S7-1500/ET200MP station_1", "HMI_RT_1" })
        {
            var refusal = Assert.Throws<AdapterPreconditionException>(() => DeviceCreationRules.RequireFreeName(occupied, name));
            Assert.Equal("deviceName", refusal.ParamName);
            Assert.False(refusal.IsArgument);
        }
        Assert.True(Assert.Throws<AdapterPreconditionException>(() => DeviceCreationRules.RequireFreeName(occupied, " ")).IsArgument);
    }

    [Theory]
    [InlineData("", "", "plcMlfb is required")]
    [InlineData("OrderNumber:6ES7 513-1AM03-0AB0/V3.0", "V3.0", "already contains its version")]
    [InlineData("6ES7 513-1AM03-0AB0", "V3.0", null)]
    [InlineData("OrderNumber:6ES7 513-1AM03-0AB0/V3.0", "", null)]
    public void Scaffold_device_specs_name_exactly_one_type(string mlfb, string version, string? problem)
    {
        var actual = DeviceCreationRules.SpecProblem(mlfb, version, "plcMlfb", "plcVersion");
        if (problem == null) Assert.Null(actual);
        else Assert.Contains(problem, actual, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("14sp1", false)] [InlineData("15.1", false)] [InlineData("16", false)] [InlineData("17", false)]
    [InlineData("18", true)] [InlineData("19", true)] [InlineData("20", true)] [InlineData("21", true)]
    public void Gsd_device_creation_is_registered_only_where_a_catalog_type_identifier_exists(string release, bool available)
    {
        Assert.Equal(available, PortedFamilies.Available(release, "CreateGsdDevice"));
        Assert.Equal(!available, HardwareCapabilities.Unsupported(release, "CreateGsdDevice") != null);
        Assert.Equal(!available, HardwareCapabilities.Unsupported(release, "hardware-devices.HardwareCreateGsdDevice") != null);
        // The read-only GSDML search stays registered on every release.
        Assert.True(PortedFamilies.Available(release, "SearchInstalledGsdDevices"));
    }
}
