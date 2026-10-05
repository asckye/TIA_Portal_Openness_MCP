using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcpServer.ModelContextProtocol
{
    // Test seam for the application host; policy decisions are exercised separately
    // by TiaMcpServer.Tests against the real ToolVersionPolicy in both build modes.
    public static partial class McpServer
    {
        internal static CallToolResult? Refusal;
        internal static RequestContext<CallToolRequestParams>? SeenRequest;
        internal static string VersionToolProblem(string name) => name == "Hidden" ? "unsupported" : "";
        static partial void ValidateV4Admission(RequestContext<CallToolRequestParams> request, ref CallToolResult? result)
        { SeenRequest = request; result = Refusal; }
        internal static IList<McpServerTool> Build(IList<McpServerTool> tools) => WrapWithVersionPolicy(tools);
    }
}

public class ServerProxy : DispatchProxy
{
    protected override object? Invoke(MethodInfo? method, object?[]? args) => null;
}
internal sealed class Sink : McpServerTool
{
    private readonly Tool tool;
    internal int Calls;
    internal CancellationToken LastToken;
    internal Sink(string name) { tool = new Tool { Name = name, InputSchema = JsonSerializer.SerializeToElement(new { type = "object" }) }; }
    public override Tool ProtocolTool => tool;
    public override ValueTask<CallToolResult> InvokeAsync(RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken = default)
    { Calls++; LastToken = cancellationToken; return new ValueTask<CallToolResult>(new CallToolResult { Content = new List<ContentBlock> { new TextContentBlock { Text = "called" } } }); }
}
internal static class VersionPolicyTests
{
    internal static void Run(Action<bool, string> Check) => RunAsync(Check).GetAwaiter().GetResult();

    private static async Task RunAsync(Action<bool, string> Check)
    {
        var server = DispatchProxy.Create<IMcpServer, ServerProxy>();
        var sink = new Sink("Visible");
        var tools = McpServer.Build(new List<McpServerTool> { sink, new Sink("Hidden") });
        Check(tools.Count == 1, "unavailable tool filtered");
        Check(ReferenceEquals(tools[0].ProtocolTool, sink.ProtocolTool), "schema identity preserved");
        var request = new RequestContext<CallToolRequestParams>(server)
        { Params = new CallToolRequestParams { Name = "Visible", Arguments = new Dictionary<string, JsonElement> { ["ACTION"] = JsonSerializer.SerializeToElement("import") } } };
        McpServer.Refusal = new CallToolResult { IsError = true,
            Content = new List<ContentBlock> { new TextContentBlock { Text = "V4 admission refusal" } } };
        var denial = await tools[0].InvokeAsync(request);
        Check(denial.IsError == true && sink.Calls == 0, "denied before inner worker/SDK body");
        Check(ReferenceEquals(McpServer.SeenRequest, request), "original request reaches the shared admission boundary");
        Check(ReferenceEquals(denial, McpServer.Refusal), "shared refusal passes through unchanged");
        request.Params = new CallToolRequestParams { Name = "Visible", Arguments = new Dictionary<string, JsonElement>
        { ["ACTION"] = JsonSerializer.SerializeToElement("read"), ["action"] = JsonSerializer.SerializeToElement("import") } };
        Check((await tools[0].InvokeAsync(request)).IsError == true && sink.Calls == 0, "shared admission refusal never invokes the inner body");
        request.Params = new CallToolRequestParams { Name = "Visible", Arguments = new Dictionary<string, JsonElement>
        { ["action"] = JsonSerializer.SerializeToElement("read") } };
        McpServer.Refusal = null;
        using var cancel = new CancellationTokenSource();
        var success = await tools[0].InvokeAsync(request, cancel.Token);
        Check(success.IsError != true && sink.Calls == 1, "allowed dispatch once");
        Check(sink.LastToken == cancel.Token, "cancellation forwarded");
        request.Params = new CallToolRequestParams { Name = "Visible" };
        await tools[0].InvokeAsync(request);
        Check(McpServer.SeenRequest!.Params!.Arguments == null, "omitted arguments reach admission unchanged");
        try { await tools[0].InvokeAsync(null!); Check(false, "null request refused"); }
        catch (ArgumentNullException) { Check(true, "null request refused"); }
    }
}
