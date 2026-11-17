extern alias toolpipeline;
using System.Text.Json.Nodes;
using TiaMcp.Adapters.Contracts;
using TiaMcp.FoundationHost;
using Services = toolpipeline::TiaMcpServer.Siemens.Services;
using Siemens = toolpipeline::TiaMcpServer.Siemens;
using Xunit;

[Collection(BundleSelectionCollection.Name)]
public sealed class UnifiedPortAvailabilityTests
{
    [Theory]
    [InlineData("14sp1", false)]
    [InlineData("15.1", false)]
    [InlineData("16", false)]
    [InlineData("17", false)]
    [InlineData("18", false)]
    [InlineData("19", true)]
    [InlineData("20", true)]
    [InlineData("21", true)]
    public void Unified_delete_categories_follow_API_availability(string release, bool available)
    {
        foreach (var category in new[] { "connections", "tagTables" })
            Assert.Equal(available, UnifiedCapabilities.Unsupported(release,
                new UnifiedRequest { Operation = "ManageUnifiedEngineeringObject", Category = category, Action = "delete" }) == null);
        Assert.Equal(available, UnifiedCapabilities.Unsupported(release,
            new UnifiedRequest { Operation = "ListUnifiedEngineeringObjects", Category = "connections" }) == null);
        foreach (var operation in new[] { "ListUnifiedEngineeringObjects", "ExportUnifiedEngineeringList", "ImportUnifiedEngineeringList",
            "ManageUnifiedEngineeringObject" })
            Assert.Equal(release == "21", UnifiedCapabilities.Unsupported(release,
                new UnifiedRequest { Operation = operation, Category = "graphicLists", Action = "delete" }) == null);
    }

    [Theory]
    [InlineData("19")]
    [InlineData("20")]
    [InlineData("21")]
    public void Delete_only_categories_refuse_create_and_update_before_native_access(string release)
    {
        foreach (var category in new[] { "connections", "tagTables" })
        foreach (var action in new[] { "create", "update" })
            Assert.Contains("delete only", UnifiedCapabilities.Unsupported(release,
                new UnifiedRequest { Operation = "ManageUnifiedEngineeringObject", Category = category, Action = action }));
    }

    [Theory]
    [InlineData("20", "textLists")]
    [InlineData("21", "textLists")]
    [InlineData("21", "graphicLists")]
    public void Lists_refuse_create_with_the_import_hint(string release, string category)
        => Assert.Contains("ImportUnifiedEngineeringList", UnifiedCapabilities.Unsupported(release,
            new UnifiedRequest { Operation = "ManageUnifiedEngineeringObject", Category = category, Action = "create" }));

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
        string previousRelease = toolpipeline::TiaMcp.FoundationHost.EngineHostConfiguration.ReleaseKey;
        try
        {
            toolpipeline::TiaMcp.FoundationHost.EngineHostConfiguration.ReleaseKey = "19";
            service.ReleaseUnifiedReadCursor("collection");
        }
        finally
        {
            toolpipeline::TiaMcp.FoundationHost.EngineHostConfiguration.ReleaseKey = previousRelease;
        }
        Assert.Equal("hmi-unified-exchange.ReleaseUnifiedReadCursor", operation);
        Assert.Equal("collection", (string?)arguments!["cursor"]);
        Assert.Single(arguments);
    }
}
