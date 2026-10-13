extern alias enginehost;
using System.Text.Json.Nodes;
using TiaMcp.Adapters.Contracts;
using TiaMcp.FoundationHost;
using Services = enginehost::TiaMcpServer.Siemens.Services;
using Siemens = enginehost::TiaMcpServer.Siemens;
using Xunit;

[Collection(BundleSelectionCollection.Name)]
public sealed class UnifiedPortAvailabilityTests
{
    public static IEnumerable<object[]> RegistrationCases =>
        from family in PortedFamilies.All.Where(f => f.Name is "F27" or "F28")
        from tool in family.Tools
        from release in new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" }
        select new object[] { release, tool };

    [Theory, MemberData(nameof(RegistrationCases))]
    public void Every_release_has_an_explicit_per_tool_registration(string release, string tool)
    {
        Assert.True(PortedFamilies.ToolReleases.ContainsKey(tool));
        bool expected = release is "20" or "21" || release == "19"
            && tool is not ("ExportUnifiedEngineeringList" or "ImportUnifiedEngineeringList" or "ManageUnifiedListEntries");
        Assert.Equal(expected, PortedFamilies.Available(release, tool));
    }

    [Theory]
    [InlineData("19", "GetUnifiedAuditSettings", "alarmAuditClasses", "", "")]
    [InlineData("19", "ListUnifiedEngineeringObjects", "textLists", "", "")]
    [InlineData("19", "ManageUnifiedDynamization", "", "create", "Expression")]
    [InlineData("19", "ManageUnifiedDynamization", "", "update", "TagParameter")]
    [InlineData("20", "ExportUnifiedEngineeringList", "graphicLists", "", "")]
    [InlineData("21", "ManageUnifiedListEntries", "textLists", "create", "")]
    public void Missing_actions_are_rejected_without_native_access(string release, string tool, string category, string action, string kind)
        => Assert.NotNull(UnifiedCapabilities.Unsupported(release,
            new UnifiedRequest { Operation = tool, Category = category, Action = action, DynamizationKind = kind }));

    [Theory]
    [InlineData("19", "GetUnifiedAuditSettings", "auditTrails")]
    [InlineData("19", "ListUnifiedEngineeringObjects", "alarmClasses")]
    [InlineData("20", "ExportUnifiedEngineeringList", "textLists")]
    [InlineData("21", "ExportUnifiedEngineeringList", "graphicLists")]
    public void Supported_actions_remain_available(string release, string tool, string category)
        => Assert.Null(UnifiedCapabilities.Unsupported(release, new UnifiedRequest { Operation = tool, Category = category }));

    [Fact]
    public void Graphic_comparison_remains_available_without_a_portal_or_worker()
    {
        int calls = 0;
        var service = new Services.GraphicSelectionService((_, _) => { calls++; throw new InvalidOperationException(); }, () => "fixture");
        var result = service.CompareUnifiedGraphicSelections("[]", "[]");
        Assert.False((bool)result.Meta!["success"]!);
        Assert.Equal(0, calls);
        Assert.True((bool)result.Meta["offline"]!);
    }

    [Fact]
    public void Cursor_release_uses_the_managed_cleanup_operation()
    {
        string? operation = null; JsonObject? arguments = null;
        var service = new Services.MigrationReadService((name, args) => {
            operation = name; arguments = args;
            return new JsonObject { ["Value"] = new JsonObject { ["Message"] = "released", ["Meta"] = new JsonObject { ["success"] = true, ["released"] = false } } };
        }, () => "fixture");
        string previousRelease = enginehost::TiaMcp.FoundationHost.EngineHostConfiguration.ReleaseKey;
        try
        {
            enginehost::TiaMcp.FoundationHost.EngineHostConfiguration.ReleaseKey = "19";
            service.ReleaseUnifiedReadCursor("collection");
        }
        finally
        {
            enginehost::TiaMcp.FoundationHost.EngineHostConfiguration.ReleaseKey = previousRelease;
        }
        Assert.Equal("hmi-unified-exchange.ReleaseUnifiedReadCursor", operation);
        Assert.Equal("collection", (string?)arguments!["cursor"]);
        Assert.Single(arguments);
    }
}
