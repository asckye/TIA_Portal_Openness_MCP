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
        string output = Path.Combine(TiaOpenness.Shared.DataLocations.Current.OutputRoot, Path.GetFileName(root));
        Directory.CreateDirectory(output);
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
                Assert.Equal(new[] { "inputPath" },
                    tool.ProtocolTool.InputSchema.GetProperty("required").EnumerateArray().Select(p => p.GetString()));
                var properties = tool.ProtocolTool.InputSchema.GetProperty("properties");
                Assert.Equal("string", properties.GetProperty("exportFile").GetProperty("type").GetString());
                var alias = properties.GetProperty("outputPath");
                Assert.Equal("string", alias.GetProperty("type").GetString());
                Assert.Contains("Deprecated alias", alias.GetProperty("description").GetString());
                Assert.Contains("5.0", alias.GetProperty("description").GetString());
                async Task<Envelope> Call(object args, CancellationToken cancellationToken = default)
                {
                    var request = new RequestContext<CallToolRequestParams>(DispatchProxy.Create<IMcpServer, ServerProxy>())
                    { Params = new() { Name = name, Arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(args)) } };
                    var result = await tool.InvokeAsync(request, cancellationToken);
                    string text = ((TextContentBlock)result.Content.Single()).Text;
                    Assert.Equal(JsonDocument.Parse(text).RootElement.GetRawText(), result.StructuredContent!.ToJsonString());
                    return V4Json.Deserialize<Envelope>(text);
                }
                var result = await Call(new { inputPath = input, outputPath = Path.Combine(output, name + ".html") });
                Assert.True(result.Ok, result.Error?.Message);
                Assert.Equal(release, result.Meta.ReleaseKey);
                Assert.Equal(BehaviorPolicy.NotApplicable, result.Meta.BehaviorPolicy);
                Assert.Contains(result.Meta.Warnings, warning => warning.Code == WarningCode.DeprecatedParameter);
                result = await Call(new { inputPath = input, outputPath = Path.Combine(output, name + ".html") });
                Assert.Equal(ErrorCode.AlreadyExists, result.Error!.Code);
                result = await Call(new { inputPath = 123, outputPath = Path.Combine(output, "unused.html") });
                Assert.Equal(ErrorCode.InvalidArgument, result.Error!.Code);
                Assert.Equal(Execution.NotStarted, result.Meta.Execution);
                Assert.Equal(BehaviorPolicy.NotApplicable, result.Meta.BehaviorPolicy);
                result = await Call(new { inputPath = input, outputPath = Path.Combine(output, "cancelled.html") }, new CancellationToken(true));
                Assert.Equal(ErrorCode.Cancelled, result.Error!.Code);
                Assert.Equal(BehaviorPolicy.NotApplicable, result.Meta.BehaviorPolicy);
                Assert.False(File.Exists(Path.Combine(output, "cancelled.html")));
                string canonicalOutput = Path.Combine(output, name + "-canonical.html");
                result = await Call(new { inputPath = input, exportFile = canonicalOutput });
                Assert.True(result.Ok, result.Error?.Message);
                Assert.True(File.Exists(canonicalOutput));
                Assert.DoesNotContain(result.Meta.Warnings, warning => warning.Code == WarningCode.DeprecatedParameter);
                string conflictingCanonical = Path.Combine(output, name + "-conflict-canonical.html");
                string conflictingLegacy = Path.Combine(output, name + "-conflict-legacy.html");
                result = await Call(new { inputPath = input, exportFile = conflictingCanonical, outputPath = conflictingLegacy });
                Assert.Equal(ErrorCode.InvalidArgument, result.Error!.Code);
                Assert.Equal(Execution.NotStarted, result.Meta.Execution);
                Assert.False(File.Exists(conflictingCanonical));
                Assert.False(File.Exists(conflictingLegacy));
            }
            Assert.Equal(0, worker.Calls);
        }
        finally
        {
            Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), Path.GetFullPath(root));
            Directory.Delete(root, true);
            Directory.Delete(output, true);
        }
    }
}
