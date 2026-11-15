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
        string identity = tool == "GetUnifiedGraphicSelection" ? "ReadUnifiedGraphicSelection" : tool;
        Assert.Equal(identity, reply.Meta!["scope"]!.Field("tool"));
        Assert.Equal(identity, ((HmiArray)reply.Meta["failures"]!)[0]!.Field("path"));
        Assert.Equal(false, reply.Meta["apiCallSuccess"]);
        Assert.Equal(0, session.Resolves);
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
            var meta = new HmiObject();
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
