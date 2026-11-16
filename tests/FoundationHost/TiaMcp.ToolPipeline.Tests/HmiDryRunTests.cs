using System.Text.Json.Nodes;
using TiaMcp.Adapters.Contracts;
using TiaMcpServer.Siemens.Services;
using Xunit;

public sealed class HmiDryRunTests
{
    [Fact]
    public void Legacy_untyped_worker_failure_keeps_internal_error()
    {
        int calls = 0;
        var service = new UnifiedHmiService((_, _) => { calls++; throw new InvalidOperationException("Native result unavailable."); }, () => "fixture");
        var tools = new TiaMcpServer.ModelContextProtocol.UnifiedHmiTools(service);
        var error = Assert.Throws<ModelContextProtocol.McpException>(() => tools.EnsureUnifiedHmiScreen("HMI", "Screen", dryRun: false));
        Assert.Equal(ModelContextProtocol.McpErrorCode.InternalError, error.ErrorCode);
        Assert.IsType<InvalidOperationException>(error.InnerException);
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData("screen")]
    [InlineData("table")]
    [InlineData("tag")]
    [InlineData("connection")]
    [InlineData("design")]
    public void Legacy_validation_refusal_is_invalid_params_without_dispatch(string operation)
    {
        int calls = 0, identities = 0;
        var service = new UnifiedHmiService((_, _) => { calls++; throw new InvalidOperationException("Unexpected worker call."); },
            () => { identities++; return "fixture"; });
        var tools = new TiaMcpServer.ModelContextProtocol.UnifiedHmiTools(service);
        var error = Assert.Throws<ModelContextProtocol.McpException>(() => {
            switch (operation)
            {
                case "screen": tools.EnsureUnifiedHmiScreen("HMI", "/"); break;
                case "table": tools.EnsureUnifiedHmiTagTable("HMI", "/"); break;
                case "tag": tools.EnsureUnifiedHmiTag("HMI", "Tags", "Flag", plcName: "", plcTag: "Flag"); break;
                case "connection": tools.EnsureUnifiedHmiConnection("HMI", "", "PLC"); break;
                default: tools.ApplyUnifiedHmiScreenDesignJson("HMI", "Screen", "{\"screen\":{\"Name\":\"Renamed\"},\"items\":[]}"); break;
            }
        });
        Assert.Equal(ModelContextProtocol.McpErrorCode.InvalidParams, error.ErrorCode);
        var refusal = Assert.IsType<TiaMcp.Adapters.Contracts.AdapterPreconditionException>(error.InnerException);
        Assert.True(refusal.IsArgument);
        Assert.IsType<ArgumentException>(refusal.InnerException);
        Assert.Equal((0, 0), (calls, identities));
    }

    [Fact]
    public void Legacy_services_preview_by_default_without_dispatch_or_identity_reads()
    {
        int calls = 0, identities = 0;
        var service = new UnifiedHmiService((_, args) => { calls++; Assert.False((bool)args["dryRun"]!); Assert.True((bool)args["confirm"]!); return new JsonObject { ["Value"] = new JsonObject { ["Message"] = "applied" } }; }, () => { identities++; return "project"; });
        var response = service.EnsureUnifiedHmiScreen("HMI_RT_1", "/Overview", 800, 480);
        Assert.True((bool)response.Meta!["dryRun"]!);
        Assert.False((bool)response.Meta["mayHaveChanged"]!);
        Assert.Equal(800, (int)response.Meta["plannedChanges"]!["Width"]!);
        Assert.Equal((0, 0), (calls, identities));
        foreach (var method in typeof(UnifiedHmiService).GetMethods().Where(m => UnifiedHmiInputValidation.LegacyWrite(m.Name)))
        {
            var arguments = method.GetParameters().Select(p => p.Name switch {
                "hmiSoftwarePath" => (object)"HMI_RT_1", "screenName" => "/Overview", "tagTableName" => "/Table",
                "plcName" => "ExactPLC", "plcTag" => "ExactSymbol", "designJson" => "{\"items\":[]}",
                _ => p.HasDefaultValue ? p.DefaultValue : "ExactName" }).ToArray();
            var preview = Assert.IsAssignableFrom<TiaMcpServer.ModelContextProtocol.ResponseMessage>(method.Invoke(service, arguments));
            Assert.True((bool)preview.Meta!["dryRun"]!);
            Assert.False((bool)preview.Meta["mayHaveChanged"]!);
        }
        var wrapper = new TiaMcpServer.ModelContextProtocol.UnifiedHmiTools(service);
        var connectionPreview = wrapper.EnsureUnifiedHmiConnectionV4("HMI_RT_1", "Configured", "ExactPLC");
        var envelope = Assert.IsType<JsonObject>(connectionPreview.StructuredContent);
        Assert.True((bool)envelope["data"]!["evidence"]!["dryRun"]!);
        Assert.NotNull(envelope["data"]!["evidence"]!["plannedChanges"]);
        Assert.Equal("read-only", (string?)envelope["meta"]!["execution"]);
        var tools = typeof(TiaMcpServer.ModelContextProtocol.UnifiedHmiTools).GetMethods().Where(m => m.Name.EndsWith("V4", StringComparison.Ordinal)
            && (m.Name.StartsWith("EnsureUnifiedHmi", StringComparison.Ordinal) || m.Name.StartsWith("ApplyUnifiedHmi", StringComparison.Ordinal) || m.Name.StartsWith("BindUnifiedHmi", StringComparison.Ordinal))).ToArray();
        Assert.Equal(13, tools.Length);
        foreach (var method in tools) Assert.Equal(true, method.GetParameters().Single(p => p.Name == "dryRun").DefaultValue);
        foreach (var dryRun in new[] { true, false })
        {
            Assert.Throws<AdapterPreconditionException>(() => service.ApplyUnifiedHmiScreenDesignJson("HMI_RT_1", "/Overview",
                "{\"screen\":{\"Name\":\"Renamed\"},\"items\":[]}", dryRun: dryRun));
            Assert.Throws<AdapterPreconditionException>(() => service.EnsureUnifiedHmiConnection("HMI_RT_1", "", "ExactPLC", dryRun));
        }
        Assert.Equal((0, 0), (calls, identities));
        service.EnsureUnifiedHmiScreen("HMI_RT_1", "/Overview", dryRun: false);
        Assert.Equal((1, 1), (calls, identities));
        foreach (var method in typeof(UnifiedHmiService).GetMethods().Where(m => UnifiedHmiInputValidation.LegacyWrite(m.Name)))
            Assert.Equal(true, method.GetParameters().Single(p => p.Name == "dryRun").DefaultValue);
    }

}
