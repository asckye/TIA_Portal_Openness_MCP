extern alias enginehost;
using System.Text.Json.Nodes;
using System.Text.Json;
using System.Reflection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.Adapters.Contracts;
using TiaMcp.FoundationHost;
using TiaMcp.WorkerChannel;
using Xunit;
using Host = enginehost::TiaMcpServer.ModelContextProtocol;
using Engine = enginehost::TiaMcp.FoundationHost;
using Service = enginehost::TiaMcpServer.Siemens.Services.OnlineDownloadService;

public sealed class OnlinePortAdmissionTests
{
    [Theory]
    [InlineData("20", "ManagePlcDataBlockSnapshot", "{\"softwarePath\":\"PLC_1\",\"blockPath\":\"DB1\",\"action\":\"createSnapshot\"}")]
    [InlineData("16", "ConnectOnlinePlc", "{\"softwarePath\":\"PLC_1\",\"userName\":\"fixture\"}")]
    [InlineData("14sp1", "ConnectOnlinePlc", "{\"softwarePath\":\"PLC_1\",\"ipAddress\":\"127.0.0.1\"}")]
    [InlineData("18", "GeneratePlcLoadableFile", "{\"softwarePath\":\"PLC_1\",\"objectPaths\":[\"Unit1\"],\"objectKind\":\"units\",\"targetOption\":\"\",\"filePath\":\"C:/fixture/unit.loadable\"}")]
    public async Task Missing_online_actions_are_refused_before_even_managed_worker_state(string release, string name, string json)
    {
        using var worker = new HardwareAddressingAdmissionTests.UnboundWorker();
        using var session = new HardwareAddressingAdmissionTests.SessionFixture(worker, release);
        var tool = LegacyHostToolRegistry.Create(worker, release, false).Single(t => t.ProtocolTool.Name == name);
        var result = await tool.InvokeAsync(new RequestContext<CallToolRequestParams>(
            DispatchProxy.Create<IMcpServer, VersionControlAdmissionTests.ServerProxy>()) {
                Params = new() { Name = name, Arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json) } });
        if (release == "18" && name == "GeneratePlcLoadableFile")
        {
            Assert.Equal("PRECONDITION_FAILED", (string?)result.StructuredContent!["error"]?["code"]);
            Assert.Equal(1, worker.Calls); // Admitted API; managed ReadState finds no project.
            Assert.False((bool)result.StructuredContent["meta"]!["requiresSessionReset"]!);
            return;
        }
        Assert.Equal("UNSUPPORTED_CAPABILITY", (string?)result.StructuredContent!["error"]?["code"]);
        Assert.False((bool)result.StructuredContent["meta"]!["requiresSessionReset"]!);
        Assert.Equal(0, worker.Calls);
    }

    [Fact]
    public void V20_parameter_upload_refuses_before_worker_dispatch()
    {
        string previous = Engine.EngineHostConfiguration.ReleaseKey;
        try
        {
            Engine.EngineHostConfiguration.ReleaseKey = "20";
            int calls = 0;
            var service = new Service((_, _) => { calls++; throw new InvalidOperationException("Unexpected dispatch"); }, () => "fixture.ap20");
            Assert.Throws<NotSupportedException>(() => service.UploadDeviceParameters("[]", "[]", "127.0.0.1"));
            Assert.Equal(0, calls);
        }
        finally { Engine.EngineHostConfiguration.ReleaseKey = previous; }
    }

    [Theory]
    [InlineData("createSnapshot")]
    [InlineData("loadSnapshotAsActualValues")]
    [InlineData("loadStartValuesAsActualValues")]
    public void V20_ValueService_actions_refuse_before_worker_dispatch(string action)
    {
        string previous = Engine.EngineHostConfiguration.ReleaseKey;
        try
        {
            Engine.EngineHostConfiguration.ReleaseKey = "20";
            int calls = 0;
            var service = new Service((_, _) => { calls++; throw new InvalidOperationException("Unexpected dispatch"); }, () => "fixture.ap20");
            Assert.Throws<NotSupportedException>(() => service.ManagePlcDataBlockSnapshot("PLC_1", "DB1", action));
            Assert.Equal(0, calls);
        }
        finally { Engine.EngineHostConfiguration.ReleaseKey = previous; }
    }

    [Theory]
    [InlineData("DownloadPlc")]
    [InlineData("UploadStationFromPlc")]
    [InlineData("UploadDeviceParameters")]
    [InlineData("GeneratePlcLoadableFile")]
    public void Online_transfer_operations_use_the_long_operation_budget(string tool)
    {
        var request = new JsonObject { ["Operation"] = tool, ["DryRun"] = false };
        var arguments = new JsonObject { ["request"] = request, ["dryRun"] = false };
        var policy = new WorkerTimeoutPolicy();
        Assert.Equal(WorkerTimeoutPolicy.Defaults["transfer"], policy.ForPlc("plc-online.ExecuteOnline", arguments));
        arguments["dryRun"] = true;
        Assert.Equal(WorkerTimeoutPolicy.Defaults["default"], policy.ForPlc("plc-online.ExecuteOnline", arguments));
    }
}
