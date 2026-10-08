using System.Reflection;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.FoundationHost;
using TiaMcp.Logic.V4;
using Xunit;

public sealed class PlcRenderFoundationTests
{
    [Theory]
    [InlineData("14sp1")][InlineData("15.1")][InlineData("16")][InlineData("17")][InlineData("18")][InlineData("19")]
    public async Task Both_renderers_use_shared_logic_and_never_dispatch_a_worker(string release)
    {
        string root = Path.Combine(Path.GetTempPath(), "foundation-render-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string input = Path.Combine(root, "Main.xml");
            File.WriteAllText(input, "<Document><Engineering version=\"V21\"/><SW.Blocks.FC><AttributeList><Name>Main</Name><Number>1</Number><ProgrammingLanguage>LAD</ProgrammingLanguage></AttributeList></SW.Blocks.FC></Document>");
            var worker = new FakeWorker();
            var tools = LegacyHostToolRegistry.Create(worker, release, false);
            foreach (string name in new[] { "RenderPlcBlock", "RenderPlcProgramAtlas" })
            {
                var tool = tools.Single(t => t.ProtocolTool.Name == name);
                Assert.DoesNotContain("Native behaviorPolicy=current", tool.ProtocolTool.Description);
                Assert.Equal(new[] { "inputPath", "outputPath" }, tool.ProtocolTool.InputSchema.GetProperty("required").EnumerateArray().Select(p => p.GetString()));
                async Task<Envelope> Call(object args, CancellationToken cancellationToken = default)
                {
                    var request = new RequestContext<CallToolRequestParams>(DispatchProxy.Create<IMcpServer, ServerProxy>())
                    { Params = new() { Name = name, Arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(args)) } };
                    var result = await tool.InvokeAsync(request, cancellationToken);
                    string text = ((TextContentBlock)result.Content.Single()).Text;
                    Assert.Equal(JsonDocument.Parse(text).RootElement.GetRawText(), result.StructuredContent!.ToJsonString());
                    return V4Json.Deserialize<Envelope>(text);
                }
                var result = await Call(new { inputPath = input, outputPath = Path.Combine(root, name + ".html") });
                Assert.True(result.Ok, result.Error?.Message);
                Assert.Equal(release, result.Meta.ReleaseKey);
                Assert.Equal(BehaviorPolicy.NotApplicable, result.Meta.BehaviorPolicy);
                result = await Call(new { inputPath = input, outputPath = Path.Combine(root, name + ".html") });
                Assert.Equal(ErrorCode.AlreadyExists, result.Error!.Code);
                result = await Call(new { inputPath = 123, outputPath = Path.Combine(root, "unused.html") });
                Assert.Equal(ErrorCode.InvalidArgument, result.Error!.Code);
                Assert.Equal(Execution.NotStarted, result.Meta.Execution);
                Assert.Equal(BehaviorPolicy.NotApplicable, result.Meta.BehaviorPolicy);
                result = await Call(new { inputPath = input, outputPath = Path.Combine(root, "cancelled.html") }, new CancellationToken(true));
                Assert.Equal(ErrorCode.Cancelled, result.Error!.Code);
                Assert.Equal(BehaviorPolicy.NotApplicable, result.Meta.BehaviorPolicy);
                Assert.False(File.Exists(Path.Combine(root, "cancelled.html")));
            }
            Assert.Equal(0, worker.Calls);
        }
        finally
        {
            Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), Path.GetFullPath(root));
            Directory.Delete(root, true);
        }
    }
}
