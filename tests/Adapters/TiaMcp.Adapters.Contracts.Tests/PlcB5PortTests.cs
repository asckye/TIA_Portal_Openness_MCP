using System.Text.Json;
using TiaMcp.Adapters.Contracts;
using TiaMcp.PlcWorker;
using Xunit;
namespace TiaMcp.Adapters.Contracts.Tests;
public sealed class PlcB5PortTests
{
    [Theory]
    [InlineData("14sp1", 1, 0, 0)]
    [InlineData("15.1", 1, 0, 6)]
    [InlineData("16", 1, 5, 6)]
    [InlineData("17", 1, 10, 6)]
    [InlineData("18", 1, 11, 6)]
    [InlineData("19", 1, 11, 6)]
    [InlineData("20", 7, 11, 6)]
    [InlineData("21", 7, 11, 8)]
    public void EveryToolHasAnExplicitRegistrationDecision(string release, int documents, int alarms, int opcUa)
    {
        foreach (var (family, expected) in new[] { ("F10", documents), ("F14", alarms), ("F15", opcUa) })
        {
            var tools = PortedFamilies.All.Single(f => f.Name == family).Tools;
            Assert.Equal(expected, tools.Count(t => PortedFamilies.Available(release, t)));
            Assert.All(tools, t => Assert.True(PortedFamilies.ToolReleases.ContainsKey(t)));
        }
    }
    [Theory]
    [InlineData("15.1", "ManageOpcUaInterface", "read", "SimaticInterface", "", "", true)]
    [InlineData("16", "ManageOpcUaInterface", "read", "SimaticInterface", "", "", false)]
    [InlineData("15.1", "GenerateOpcUaModelledInterface", "", "", "Unit", "unit", true)]
    [InlineData("16", "GenerateOpcUaModelledInterface", "", "", "Unit", "unit", false)]
    [InlineData("17", "ExchangePlcAlarmTextLists", "export", "", "Safety", "safety", true)]
    [InlineData("18", "ExchangePlcAlarmTextLists", "export", "", "Safety", "safety", false)]
    [InlineData("20", "GetOpcUaAccessControl", "", "", "", "", true)]
    [InlineData("21", "GetOpcUaAccessControl", "", "", "", "", false)]
    public void PartialActionsRequireTheirNativeApi(string release, string tool, string action, string interfaceType, string unit, string kind, bool refused)
        => Assert.Equal(refused, PlcB5Capabilities.Unsupported(release, new PlcB5Request { Operation = tool, Action = action, InterfaceType = interfaceType, UnitName = unit, UnitKind = kind }) != null);
    [Theory]
    [InlineData("18", true)] [InlineData("19", true)] [InlineData("20", false)] [InlineData("21", false)]
    public void AlarmMasterCopyModesRequireV20(string release, bool refused)
        => Assert.Equal(refused, PlcB5Capabilities.Unsupported(release, new PlcB5Request { Operation = "ManagePlcAlarmTextList", Action = "createFromMasterCopy", CopyMode = "Replace" }) != null);
    [Theory]
    [InlineData("20", "createFromMasterCopy", true)] [InlineData("21", "createFromMasterCopy", false)]
    [InlineData("20", "createFromTypeVersion", true)] [InlineData("21", "createFromTypeVersion", false)]
    [InlineData("20", "read", false)]
    public void DocumentCreationRequiresV21(string release, string action, bool refused)
        => Assert.Equal(refused, PlcB5Capabilities.Unsupported(release, new PlcB5Request { Operation = "ManagePlcDocuments", Action = action, ObjectKind = "document" }) != null);
    [Theory]
    [InlineData("F10", "plc-documents")]
    [InlineData("F14", "plc-alarms")]
    [InlineData("F15", "plc-opcua")]
    public void DispatchAndPortalAdmissionUseTheGenericModule(string family, string prefix)
    {
        Assert.Equal(new[] { "ExecutePlcB5" }, WorkerOperations.FamilyNames(family));
        Assert.True(WorkerOperations.RequiresPortal(prefix + ".ExecutePlcB5"));
        Assert.True(WorkerOperations.IsFamilyOperation(prefix + ".ExecutePlcB5"));
        Assert.False(WorkerOperations.IsFamilyOperation(prefix + ".RunAnything"));
    }
    [Fact]
    public void DocumentEvidenceSurvivesTheWorkerJsonBoundary()
    {
        var original = new PlcB5Reply { Blocks = new[] { new PlcB5Block { Name = "Main", Attributes = new() { new() { Name = "Number", Value = 1, AccessMode = "Read" } } } },
            Events = new() { new() { Kind = "native", State = "Failed", ChangesProject = true, Value = new[] { "message" }, Name = "Main" } },
            Attempted = 1, Stopped = true, MayHaveChanged = true, RequiresSessionReset = true };
        var decoded = JsonSerializer.Deserialize<PlcB5Reply>(JsonSerializer.Serialize(original))!;
        Assert.Equal("Main", Assert.Single(decoded.Blocks!).Name);
        Assert.Equal("Failed", Assert.Single(decoded.Events).State);
        Assert.Equal("message", ((JsonElement)decoded.Events[0].Value!)[0].GetString());
        Assert.True(decoded.RequiresSessionReset); Assert.True(decoded.BlockReadsAfterUncertain);
    }
}
