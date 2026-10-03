using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using TiaOpenness.Client;
using TiaOpenness.Contracts.Models;
using Xunit;

namespace TiaOpenness.Core.Tests;

public sealed class IntegrationTests
{
    // Exercises the real Studio typed client, dispatcher, HTTP request writer and response decoder.
    // The HTTP handler is in memory: no listener, engine process, Siemens DLL or TIA installation.
    [Theory]
    [InlineData(20)]
    [InlineData(21)]
    public async Task Desktop_browse_inspect_export_import_compile_and_save_flow(int major)
    {
        var handler = new EngineFixture(major);
        using var connection = new McpConnection(new Uri("http://127.0.0.1:9876/mcp"), handler: handler);
        using var bridge = new BridgeClient(connection, handler.Project, Array.Empty<string>());
        using var client = new TiaClient(bridge);
        var doctor = await client.DoctorAsync();
        Assert.True(doctor.CanRunOpenness);
        var state = await client.ConnectAsync();
        Assert.True(state.Connected); Assert.Equal(major.ToString(), state.OpennessVersion);
        Assert.Equal(handler.Project, state.OpenProject.Path);
        Assert.Equal("Line", (await client.OpenProjectAsync(handler.Project)).Name);
        var device = Assert.Single(await client.ListDevicesAsync()); Assert.Equal("PLC_1", device.Id);
        var blocks = await client.ListBlocksAsync(device.Id);
        Assert.Equal(2, blocks.Count); Assert.Equal("Program blocks/Motion/FB_Axis", blocks[1].Path);
        Assert.Equal(BlockKind.FB, blocks[1].Kind); Assert.Equal(12, blocks[1].Number);
        Assert.Equal("Engineer", blocks[1].HeaderAuthor);
        Assert.True(blocks.All(b => b.IsConsistent));
        var inspect = await client.InspectAsync(device.Id, "^(OB|FB)_");
        Assert.Equal(2, inspect.BlocksScanned); Assert.Empty(inspect.Findings);
        var exported = await client.ExportBlocksAsync(device.Id, blocks.Select(b => b.Path), handler.Output);
        Assert.Equal(2, exported.Succeeded); Assert.All(exported.Items, i => Assert.True(File.Exists(i.FilePath)));
        var source = await client.ExportBlocksAsync(device.Id, new[] { blocks[1].Path }, handler.Output, ExportFormat.Source);
        Assert.Equal(1, source.Succeeded); Assert.EndsWith(".s7dcl", source.Items[0].FilePath);
        var imported = await client.ImportBlocksAsync(device.Id, new[] { exported.Items[0].FilePath }, overwrite: true);
        Assert.Equal(1, imported.Succeeded);
        var compile = await client.CompileAsync(device.Id);
        Assert.True(compile.Succeeded); Assert.Equal("Success", compile.State); Assert.Equal(0, compile.ErrorCount);
        await client.SaveProjectAsync();
        Assert.Equal("SaveProject", handler.Calls.Last().Name);
        Assert.Equal(major == 21, await client.VcSupportedAsync());
        Assert.False(handler.Calls.Single(x => x.Name == "Doctor").Args.Value<bool>("fix"));
        Assert.DoesNotContain(handler.Calls, x => x.Name == "Connect" || x.Name == "OpenProject");
        Assert.Equal(1, handler.Initializations);
        Assert.True(handler.SawSessionHeader);
    }

    [Fact]
    public async Task V21_workspace_create_preview_map_status_sync_and_diff_flow()
    {
        var handler = new EngineFixture(21);
        using var bridge = new BridgeClient(new McpConnection(new Uri("http://localhost:9876/mcp"), handler: handler), handler.Project, new[] { "PLC_1" });
        using var client = new TiaClient(bridge);
        await client.ConnectAsync();
        var workspace = await client.VcCreateWorkspaceAsync("git", handler.Output);
        Assert.Equal("git", workspace.Name); Assert.Equal(handler.Output, workspace.RootPath);
        Assert.Single(await client.VcListWorkspacesAsync());
        var preview = await client.VcMapProjectAsync("git"); Assert.True(preview.DryRun); Assert.Equal(2, preview.Mapped);
        var mapped = await client.VcMapProjectAsync("git", dryRun: false); Assert.False(mapped.DryRun); Assert.Equal(2, mapped.Mapped);
        var status = await client.VcStatusAsync("git"); Assert.Equal(2, status.Total); Assert.Equal(1, status.Differing);
        Assert.Equal(VcCompareState.Unequal, Assert.Single(status.Items).CompareState);
        Assert.True((await client.VcSyncAsync("git")).DryRun);
        var sync = await client.VcSyncAsync("git", dryRun: false); Assert.Equal(1, sync.Synchronized); Assert.Equal(1, sync.SkippedEqual);
        var diff = await client.VcDiffAsync("git"); Assert.False(diff.Available); // Empty temp directory is not a Git repository.
        Assert.Contains(handler.Calls, x => x.Name == "SyncVersionControlWorkspace" && x.Args.Value<string>("direction") == "ProjectToWorkspace");
    }

    [Fact]
    public async Task Mock_typed_client_opens_fixture_exports_compiles_and_inspects_without_http()
    {
        using var bridge = new BridgeClient(Array.Empty<string>());
        bridge.Start(forceMock: true);
        using var client = new TiaClient(bridge);
        Assert.Equal(SessionMode.Mock, (await client.ConnectAsync()).Mode);
        await client.OpenProjectAsync(Path.Combine(Path.GetTempPath(), "studio-functional-" + Guid.NewGuid().ToString("N"), "Line.ap21"));
        var plc = (await client.ListDevicesAsync()).First(d => d.Category == "Plc");
        Assert.NotEmpty(await client.ListBlocksAsync(plc.Id));
        var output = Path.Combine(Path.GetTempPath(), "studio-export-test-" + Guid.NewGuid().ToString("N"));
        var exported = await client.ExportBlocksAsync(plc.Id, Array.Empty<string>(), output);
        Assert.True(exported.Succeeded > 0); Assert.All(exported.Items.Where(i => i.Succeeded), i => Assert.True(File.Exists(i.FilePath)));
        Assert.NotNull(await client.CompileAsync(plc.Id));
        Assert.True((await client.InspectAsync(plc.Id)).BlocksScanned > 0);
        await client.SaveProjectAsync();
        await client.CloseProjectAsync();
        Assert.Null((await client.StateAsync()).OpenProject);
    }

    [Fact]
    public async Task Import_without_overwrite_is_rejected_before_native_dispatch()
    {
        var handler = new EngineFixture(21);
        using var bridge = new BridgeClient(new McpConnection(new Uri("http://127.0.0.1:9876/mcp"), handler: handler), handler.Project, new[] { "PLC_1" });
        using var client = new TiaClient(bridge);
        await Assert.ThrowsAsync<NotSupportedException>(() => client.ImportBlocksAsync("PLC_1", new[] { Path.Combine(handler.Output, "FB.xml") }));
        Assert.DoesNotContain(handler.Calls, x => x.Name == "ImportBlock");
    }

    [Fact]
    public async Task Lost_write_response_is_not_replayed_and_does_not_become_success()
    {
        var handler = new EngineFixture(21) { LoseSaveResponse = true };
        using var bridge = new BridgeClient(new McpConnection(new Uri("http://127.0.0.1:9876/mcp"), handler: handler), handler.Project, new[] { "PLC_1" });
        using var client = new TiaClient(bridge);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.SaveProjectAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.SaveProjectAsync());
        Assert.Single(handler.Calls, x => x.Name == "SaveProject");
    }

    [Fact]
    public async Task Corrected_tool_failure_does_not_require_restarting_the_desktop()
    {
        var handler = new EngineFixture(21) { RejectSaveOnce = true };
        using var bridge = new BridgeClient(new McpConnection(new Uri("http://127.0.0.1:9876/mcp"), handler: handler), handler.Project, new[] { "PLC_1" });
        using var client = new TiaClient(bridge);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.SaveProjectAsync());
        await client.SaveProjectAsync();
        Assert.Equal(2, handler.Calls.Count(x => x.Name == "SaveProject"));
    }

    [Fact]
    public void Bridge_dispatch_success_does_not_hide_operation_failure()
    {
        var inner = new JObject { ["message"] = "Compile failed", ["meta"] = new JObject { ["success"] = false } };
        var result = Envelope(inner);
        Assert.Throws<InvalidOperationException>(() => McpConnection.DecodeToolResult(result));
    }

    internal static JObject Envelope(JObject inner) => new JObject {
        ["content"] = new JArray(new JObject { ["type"] = "text", ["text"] = new JObject {
            ["message"] = inner.ToString(), ["meta"] = new JObject { ["bridgeSuccess"] = true, ["operationSuccess"] = true }
        }.ToString() })
    };

    private sealed class EngineFixture : HttpMessageHandler
    {
        public string Project { get; } = Path.Combine(Path.GetTempPath(), "StudioFunctional", "Line.ap21");
        public string Output { get; } = Path.Combine(Path.GetTempPath(), "studio-wire-test-" + Guid.NewGuid().ToString("N"));
        public readonly List<(string Name, JObject Args)> Calls = new();
        public int Initializations;
        public bool SawSessionHeader;
        public bool LoseSaveResponse;
        public bool RejectSaveOnce;
        private readonly int major;
        public EngineFixture(int major) { this.major = major; Directory.CreateDirectory(Output); }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var frame = JObject.Parse(await request.Content.ReadAsStringAsync(ct));
            var method = frame.Value<string>("method");
            if (method == "notifications/initialized") return new HttpResponseMessage(HttpStatusCode.Accepted);
            JObject result;
            if (method == "initialize") { Initializations++; result = new JObject { ["protocolVersion"] = "2024-11-05", ["capabilities"] = new JObject(), ["serverInfo"] = new JObject { ["name"] = "fixture", ["version"] = "1" } }; }
            else
            {
                Assert.Equal("tools/call", method); Assert.Equal("CallTool", frame["params"].Value<string>("name"));
                SawSessionHeader |= request.Headers.Contains("Mcp-Session-Id");
                var call = (JObject)frame["params"]["arguments"];
                var name = call.Value<string>("name"); var p = (JObject)call["argumentsJson"];
                Calls.Add((name, p));
                if (name == "SaveProject" && LoseSaveResponse) throw new HttpRequestException("Connection lost after dispatch.");
                result = Envelope(Respond(name, p));
            }
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(new JObject { ["jsonrpc"] = "2.0", ["id"] = frame["id"], ["result"] = result }.ToString(), Encoding.UTF8, "application/json") };
            response.Headers.Add("Mcp-Session-Id", "studio-test-session"); return response;
        }
        private JObject Respond(string name, JObject p)
        {
            var r = new JObject { ["message"] = "ok", ["meta"] = new JObject { ["success"] = true } };
            var meta = (JObject)r["meta"];
            switch (name)
            {
                case "Doctor": r["checks"] = JArray.FromObject(new[] { new { name = "Runtime", ok = true, detail = "Ready" } }); break;
                case "GetState": r["isConnected"] = true; meta["binding"] = new JObject { ["identity"] = new JObject {
                    ["tiaMajorVersion"] = major, ["projectPath"] = Project, ["projectName"] = "Line", ["generation"] = "g1", ["processId"] = 42, ["processStartUtc"] = "2026-10-02T00:00:00Z" } }; break;
                case "GetDevices": meta["plcSoftwareNames"] = new JArray("PLC_1"); break;
                case "GetSoftwareInfo": r["name"] = "PLC_1"; break;
                case "GetBlocksWithHierarchy":
                    meta["dataComplete"] = true;
                    r["root"] = new JObject { ["name"] = "Program blocks", ["blocks"] = new JArray(Block("OB_Main", "OB", 1)),
                        ["groups"] = new JArray(new JObject { ["name"] = "Motion", ["blocks"] = new JArray(Block("FB_Axis", "FB", 12)), ["groups"] = new JArray() }) }; break;
                case "ExportBlock": case "ExportAsDocuments":
                    var dir = p.Value<string>("exportPath"); Directory.CreateDirectory(dir);
                    var file = Path.Combine(dir, name == "ExportBlock" ? "block.xml" : "block.s7dcl"); File.WriteAllText(file, "fixture"); meta["exportedFile"] = file; break;
                case "ImportBlock": meta["verified"] = true; break;
                case "CompileAndDiagnosePlc": r["state"] = "Success"; r["errorCount"] = 0; r["warningCount"] = 0; r["errors"] = new JArray(); r["warnings"] = new JArray(); break;
                case "SaveProject":
                    if (RejectSaveOnce) { RejectSaveOnce = false; meta["success"] = false; r["message"] = "Save rejected before execution by the fixture."; }
                    break;
                case "CreateVersionControlWorkspace": break;
                case "GetVersionControlWorkspaces": meta["workspaces"] = JArray.FromObject(new[] { new { name = "git", rootPath = Output, mappedObjectCount = 2, language = "en-US" } }); break;
                case "GetVersionControlStatus":
                    meta["workspaceName"] = "git"; meta["rootPath"] = Output; meta["total"] = 2; meta["differing"] = 1;
                    meta["objects"] = JArray.FromObject(new[] { new { name = "FB_Axis", filePath = Path.Combine(Output, "FB_Axis"), fileFormat = "s7dcl", compareState = "Unequal" } }); break;
                case "ConnectProjectToWorkspace":
                    meta["workspaceName"] = "git"; meta["rootPath"] = Output; meta["dryRun"] = p["dryRun"]; meta["mapped"] = 2; meta["visited"] = 3; meta["failed"] = 0; r["items"] = new JArray("PLC_1 | mapped"); break;
                case "SyncVersionControlWorkspace":
                    meta["workspaceName"] = "git"; meta["rootPath"] = Output; meta["dryRun"] = p["dryRun"]; meta["synchronized"] = p.Value<bool>("dryRun") ? 0 : 1; meta["skippedEqual"] = 1; meta["failed"] = 0; break;
                default: throw new InvalidOperationException("Unplanned tool dispatch: " + name);
            }
            return r;
        }
        private static JObject Block(string name, string type, int number) => JObject.FromObject(new { name, typeName = type,
            programmingLanguage = "SCL", isConsistent = true, isKnowHowProtected = false,
            attributes = new[] { new { name = "Number", value = number.ToString() }, new { name = "HeaderAuthor", value = "Engineer" } } });
    }
}
