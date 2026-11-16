using System;
using System.Collections.Generic;
using System.Text.Json;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Native.Hmi;
using TiaMcpServer.ModelContextProtocol;
using Xunit;

namespace TiaMcp.Engine.Tests;

public sealed class MigrationResultTests
{
    [Theory]
    [InlineData("en-US")]
    [InlineData("fr-FR")]
    public void Snapshot_dates_are_round_trip_invariant(string culture)
    {
        var previous = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo(culture);
            var date = new DateTime(2026, 11, 16, 12, 34, 56, DateTimeKind.Utc).AddTicks(1234567);
            var snapshot = HmiSnapshot.Capture(date);
            Assert.Equal("2026-11-16T12:34:56.1234567Z", ((HmiArray)snapshot["nodes"]!)[0]!.Field("value"));
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = previous; }
    }

    [Fact]
    public void Security_device_path_messages_use_the_declared_name()
    {
        var error = Assert.Throws<ArgumentException>(() => TiaMcpServer.Siemens.ProjectSecurityLogic.ValidateDevicePath("{}"));
        Assert.Contains("devicePath", error.Message);
        Assert.DoesNotContain("devicePathJson", error.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("PN")]
    [InlineData(" industrial ethernet ")]
    public void Network_planner_accepts_the_default_and_supported_subnet_types(string subnetType)
    {
        var plan = JsonSerializer.Serialize(new { operations = new[] {
            new { type = "EnsureSubnet", anchorDeviceItemPath = "PLC/CPU", subnetName = "PN", subnetType } } });
        var result = HardwareNetworkPlanValidator.Validate(plan);
        Assert.True((bool?)result["ok"], result.ToJsonString());
        Assert.Empty(result["errors"]!.AsArray());
    }

    public static IEnumerable<object[]> Tools()
    {
        foreach (string release in new[] { "19", "20", "21" })
            foreach (string tool in new[] { "ListUnifiedGlobalScripts", "GetUnifiedGlobalScript", "ListUnifiedTagDefinitions",
                "GetUnifiedScreenBranch", "GetUnifiedLibraryType", "ListUnifiedLibraryFolderEntries", "GetUnifiedFaceplateInstance",
                "GetUnifiedGraphicSelection" })
                yield return new object[] { release, tool };
    }

    [Theory, MemberData(nameof(Tools))]
    public void Migration_scope_uses_the_tool_identity_in_request_failures(string release, string tool)
    {
        using var version = HardwareContract.UseRelease(release);
        using var session = new Session { CurrentProject = null };
        var reply = Read(session, tool);
        string identity = tool;
        Assert.Equal(identity, reply.Meta!["scope"]!.Field("tool"));
        Assert.Equal(identity, ((HmiArray)reply.Meta["failures"]!)[0]!.Field("path"));
        Assert.Equal(false, reply.Meta["apiCallSuccess"]);
        Assert.Equal(0, session.Resolves);
    }

    [Theory]
    [InlineData("19")]
    [InlineData("20")]
    [InlineData("21")]
    public void Graphic_selection_producer_names_the_public_tool(string release)
    {
        using var version = HardwareContract.UseRelease(release);
        using var session = new Session();
        var adapter = new MigrationReadAdapter(session);
        var scope = new HmiObject { ["screenPath"] = "/Overview", ["itemNames"] = new HmiArray("Button") };
        var geometry = new HmiObject();
        foreach (string field in new[] { "Left", "Top", "Width", "Height" })
            geometry[field] = new HmiObject { ["path"] = "/Screens/Overview/ScreenItems/Button/" + field,
                ["kind"] = "value", ["status"] = "ok", ["value"] = 1 };
        var first = adapter.MigrationPage("GetUnifiedGraphicSelection", "HMI", "Fixture", scope, "", 1, 5000,
            _ => new[] {
                new HmiObject { ["path"] = "/Screens/Overview/ScreenItems/Button", ["kind"] = "graphicObject", ["status"] = "ok",
                    ["name"] = "Button", ["type"] = "Siemens.Engineering.HmiUnified.UI.Controls.HmiButton",
                    ["geometryComplete"] = true, ["fields"] = geometry },
                new HmiObject { ["path"] = "/Screens/Overview/$selection", ["kind"] = "graphicSelectionSummary", ["status"] = "ok" }
            }, expectedCount: 2);
        var cursor = (string)first.Meta!["nextCursor"]!;
        Assert.False(string.IsNullOrEmpty(cursor));
        var reply = new GraphicSelectionAdapter(session, adapter)
            .ReadUnifiedGraphicSelection("HMI", "Fixture", "/Overview", "[\"Button\"]", cursor);
        Assert.Equal("GetUnifiedGraphicSelection", reply.Meta!["tool"]);
        Assert.Equal("GetUnifiedGraphicSelection", reply.Meta!["scope"]!.Field("tool"));
        Assert.Equal(true, reply.Meta["apiCallSuccess"]);
        Assert.Equal(true, reply.Meta["traversalComplete"]);
        Assert.Equal(1, session.Resolves);
    }

    [Theory]
    [InlineData("19")]
    [InlineData("20")]
    [InlineData("21")]
    public void Migration_scope_names_the_public_tool(string release)
    {
        using var version = HardwareContract.UseRelease(release);
        using var session = new Session();
        var adapter = new MigrationReadAdapter(session);
        HmiObject scope = new HmiObject { ["moduleName"] = "Global" };
        var first = adapter.MigrationPage("GetUnifiedGlobalScript", "HMI", "Fixture", scope, "", 1, 5000,
            _ => new[] {
                new HmiObject { ["path"] = "/Scripts/Global", ["kind"] = "scriptModule", ["status"] = "ok",
                    ["name"] = "Global", ["bodyReadSuccess"] = false },
                new HmiObject { ["path"] = "/Scripts/Global", ["kind"] = "nativeExportPlan", ["status"] = "ok",
                    ["operationId"] = "fixture-export" } });
        Assert.Equal("GetUnifiedGlobalScript", first.Meta!["scope"]!.Field("tool"));
        var next = (string)first.Meta["nextCursor"]!;
        Assert.False(string.IsNullOrEmpty(next));
        var second = adapter.ReadUnifiedGlobalScript("HMI", "Fixture", "Global", next);
        Assert.Equal("GetUnifiedGlobalScript", second.Meta!["scope"]!.Field("tool"));
        Assert.Equal(true, second.Meta["apiCallSuccess"]);
        Assert.Equal(true, second.Meta["traversalComplete"]);
        Assert.Equal(1, session.Resolves);
    }

    private static HmiMessage Read(Session session, string tool)
    {
        var adapter = new MigrationReadAdapter(session);
        return tool switch {
            "ListUnifiedGlobalScripts" => adapter.ListUnifiedGlobalScripts("HMI", "Fixture"),
            "GetUnifiedGlobalScript" => adapter.ReadUnifiedGlobalScript("HMI", "Fixture", "Global"),
            "ListUnifiedTagDefinitions" => adapter.ReadUnifiedTagDefinitions("HMI", "Fixture"),
            "GetUnifiedScreenBranch" => adapter.ReadUnifiedScreenBranch("HMI", "Fixture", "/Overview"),
            "GetUnifiedLibraryType" => adapter.ReadUnifiedLibraryType("HMI", "Fixture", "/Type", "1.0"),
            "ListUnifiedLibraryFolderEntries" => adapter.ListUnifiedLibraryFolder("HMI", "Fixture"),
            "GetUnifiedFaceplateInstance" => adapter.ReadUnifiedFaceplateInstance("HMI", "Fixture", "/Overview", "Faceplate"),
            _ => new GraphicSelectionAdapter(session, adapter).ReadUnifiedGraphicSelection("HMI", "Fixture", "/Overview", "[\"Button\"]")
        };
    }

    public sealed class Project { public string Name => "Fixture"; }
    private sealed class Session : IUnifiedSession, IDisposable
    {
        private readonly Func<Dictionary<string, object?>, string>? _renderer = HmiValues.Renderer;
        private readonly Dictionary<string, object?>? _inputs = UnifiedRequestValues.Inputs;
        internal Session()
        {
            HmiValues.Renderer = value => JsonSerializer.Serialize(value);
            UnifiedRequestValues.Inputs = new Dictionary<string, object?>(StringComparer.Ordinal) {
                ["[\"Button\"]"] = new HmiArray("Button") };
        }
        internal int Resolves;
        public object? CurrentProject { get; set; } = new Project();
        public object? HmiReadFault => null;
        public MigrationPages MigrationPages { get; } = new MigrationPages();
        public object ResolveHmiSoftwareOrThrow(string path) { Resolves++; return new Siemens.Engineering.HmiUnified.HmiSoftware(); }
        public object ExactUnifiedRoot(string path) => throw new NotSupportedException();
        public object ExactPlantViews() => throw new NotSupportedException();
        public string UnifiedCollection(string category) => throw new NotSupportedException();
        public IDisposable AcquireHmiEditAccess() => throw new NotSupportedException();
        public void RecordHmiReadFault(HmiObject meta) => throw new InvalidOperationException("No native fault is expected.");
        public HmiMessage RunHmiStepTool(string tool, Func<HmiObject, string> action, bool requiresProject = true)
        {
            var meta = new HmiObject { ["tool"] = tool };
            return new HmiMessage { Message = action(meta), Meta = meta };
        }
        public void Dispose()
        {
            MigrationPages.Dispose();
            HmiValues.Renderer = _renderer;
            UnifiedRequestValues.Inputs = _inputs;
        }
    }
}
