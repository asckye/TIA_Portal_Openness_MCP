using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Logic.V4.Inputs;
using TiaMcp.FoundationHost;
using TiaMcpServer.ModelContextProtocol;
using Xunit;

public sealed partial class PipelineTests
{
    [Fact]
    public async Task Native_batch_item_masks_its_own_password_after_dispatch_scope_exits()
    {
        // Reuse the declared credential input for a synthetic read-only native backend.
        using var fixture = new Fixture(all: true, workerTool: "GetPlcBlockFingerprints");
        using var parent = InvocationJournal.UseCorrelation("batch-credential-parent");
        using var diagnostic = TiaMcp.Logic.V4.NativeFailureDiagnostics.Begin("batch-credential-parent",
            JsonSerializer.SerializeToElement(new { previewToken = "stored-plan" }));
        fixture.Worker.NativeFailure = true;
        fixture.Worker.NativeFailureMessage = "Invalid password 'Hunter2' for user X";
        var binding = new JsonObject { ["identity"] = new JsonObject { ["tiaMajorVersion"] = 21, ["processId"] = 123,
            ["processStartUtc"] = "2026-11-13T00:00:00Z", ["projectPath"] = "C:/Fixture.ap21",
            ["projectName"] = "Fixture", ["generation"] = 1 } };
        fixture.Worker.BatchStatus = new JsonObject { ["session"] = new JsonObject { ["isConnected"] = true, ["project"] = "Fixture" },
            ["binding"] = binding, ["readiness"] = new JsonObject { ["ready"] = true } };
        var args = JsonSerializer.SerializeToElement(new { softwarePath = "PLC", targetIpAddress = "192.0.2.1",
            password = "Hunter2", dryRun = true });
        ModelContextProtocol.Protocol.CallToolResult preview;
        using (McpServer.BeginReadOnlyApprovalPreview())
            preview = await McpServer.CallToolAsync("GetPlcBlockFingerprints", new ToolArguments(args));
        Assert.True((bool?)preview.StructuredContent?["ok"], preview.StructuredContent?.ToJsonString());
        string token = EngineHostConfiguration.Current.BatchPlans.Add(new BatchPlanStore.Plan {
            Project = "Fixture", State = BatchPlanStore.BindingState(binding), Operations = new JsonArray(new JsonObject {
                ["name"] = "GetPlcBlockFingerprints", ["arguments"] = JsonSerializer.SerializeToNode(args) }),
            Previews = new JsonArray(preview.StructuredContent!.DeepClone()) }, DateTime.UtcNow);
        using var nativeCall = InvocationJournal.BeginNativeCallScope();
        InvocationJournal.NativeCallStarted();
        var result = await McpServer.ApplyToolBatchAsync(token);
        var child = result.StructuredContent!["data"]!["items"]![0]!["result"]!;
        Assert.Equal("NATIVE_OPERATION_FAILED", (string?)child["error"]?["code"]);
        Assert.Contains("Invalid password", (string?)child["error"]?["details"]?["nativeMessage"]);
        Assert.DoesNotContain("Hunter2", result.StructuredContent.ToJsonString());
        Assert.All(result.Content.OfType<ModelContextProtocol.Protocol.TextContentBlock>(), text => Assert.DoesNotContain("Hunter2", text.Text));
        Assert.Equal((string?)child["meta"]?["requestId"], (string?)child["error"]?["innererror"]?["diagnosticId"]);
    }

    [Theory]
    [InlineData("14sp1"), InlineData("15.1"), InlineData("16"), InlineData("17")]
    [InlineData("18"), InlineData("19"), InlineData("20"), InlineData("21")]
    public async Task Native_batch_failures_keep_each_child_correlation(string release)
    {
        // This fixture uses the shared CreatePlcTypeGroup input on every release.
        using var fixture = new Fixture(all: true);
        EngineHostConfiguration.ReleaseKey = release;
        using var parent = InvocationJournal.UseCorrelation("batch-parent");
        fixture.Worker.NativeFailure = true;
        fixture.Worker.Native = true;
        int major = TiaMcp.Versioning.TiaVersionCatalog.Get(release).MajorVersion;
        var binding = new JsonObject { ["identity"] = new JsonObject { ["tiaMajorVersion"] = major, ["processId"] = 123,
            ["processStartUtc"] = "2026-11-13T00:00:00Z", ["projectPath"] = "C:/Fixture.ap" + major,
            ["projectName"] = "Fixture", ["generation"] = 1 } };
        fixture.Worker.BatchStatus = new JsonObject { ["session"] = new JsonObject { ["isConnected"] = true, ["project"] = "Fixture" },
            ["binding"] = binding, ["readiness"] = new JsonObject { ["ready"] = true } };
        var arguments = JsonSerializer.SerializeToElement(new { softwarePath = "PLC", groupPath = "Fixture", dryRun = true });
        ModelContextProtocol.Protocol.CallToolResult preview;
        using (McpServer.BeginReadOnlyApprovalPreview())
            preview = await McpServer.CallToolAsync("CreatePlcTypeGroup", new ToolArguments(arguments));
        Assert.True((bool?)preview.StructuredContent?["ok"], preview.StructuredContent?.ToJsonString());
        var operation = new JsonObject { ["name"] = "CreatePlcTypeGroup", ["arguments"] = JsonSerializer.SerializeToNode(arguments) };
        string token = EngineHostConfiguration.Current.BatchPlans.Add(new BatchPlanStore.Plan {
            Project = "Fixture", State = BatchPlanStore.BindingState(binding),
            Operations = new JsonArray(operation.DeepClone(), operation.DeepClone()),
            Previews = new JsonArray(preview.StructuredContent!.DeepClone(), preview.StructuredContent.DeepClone()) }, DateTime.UtcNow);
        var result = await McpServer.ApplyToolBatchAsync(token);
        Assert.Equal("NATIVE_OPERATION_FAILED", (string?)result.StructuredContent?["data"]?["items"]?[0]?["result"]?["error"]?["code"]);
        var children = result.StructuredContent!["data"]!["items"]!.AsArray().Select(row => row!["result"]!).ToArray();
        Assert.Equal(2, children.Length);
        var ids = new List<string>();
        foreach (var child in children)
        {
            string id = (string)child["meta"]!["requestId"]!;
            if ((string?)child["error"]?["code"] == "NATIVE_OPERATION_FAILED")
                Assert.Equal(id, (string?)child["error"]?["innererror"]?["diagnosticId"]);
            else Assert.Equal("NOT_EXECUTED", (string?)child["error"]?["code"]);
            Assert.NotEqual("batch-parent", id);
            ids.Add(id);
        }
        Assert.Equal(2, ids.Distinct().Count());
    }
}
