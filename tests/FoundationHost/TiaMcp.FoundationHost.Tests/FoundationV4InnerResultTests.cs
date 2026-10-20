using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.FoundationHost;
using TiaMcp.Logic.V4;
using TiaOpenness.Shared;
using Xunit;

public sealed class FoundationV4InnerResultTests : IDisposable
{
    private readonly string root = Path.Combine(AppContext.BaseDirectory, "inner-results", Guid.NewGuid().ToString("N"));
    private const string PrivateMessage = @"Private failure at C:\Users\Private\package.json; token=private-secret";

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }

    private static RequestContext<CallToolRequestParams> Request(string name)
        => new(DispatchProxy.Create<IMcpServer, ServerProxy>()) {
            Params = new CallToolRequestParams { Name = name, Arguments = new Dictionary<string, JsonElement>() } };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Plain_text_inner_results_return_internal_failure_and_complete_journal_and_audit(bool isError)
    {
        var log = new AuditLog(root);
        using var audit = AuditInvocation.UseLog(log);
        var rows = new List<string>();
        TiaMcpServer.ModelContextProtocol.InvocationJournal.ConfigureOutput(rows.Add);
        try
        {
            var inner = new ResultTool("EnsureOpennessUserGroup", new CallToolResult { IsError = isError,
                Content = new[] { new TextContentBlock { Text = PrivateMessage } } });
            var tool = new FoundationV4Tool(inner, "19", null, () => new ApprovalSettings(false, 1));
            var result = await tool.InvokeAsync(Request(inner.ProtocolTool.Name));
            // EnsureOpennessUserGroup discloses the current native behavior policy on every envelope.
            AssertInternalFailure(result, inner.ProtocolTool.Name, "current");
            Assert.Equal(1, inner.Calls);
            string id = (string)result.StructuredContent!["meta"]!["requestId"]!;
            var projected = rows.Select(row => JsonNode.Parse(row)!).Where(row => (int?)row["callProjection"] == 1).ToArray();
            Assert.Equal(new[] { "BEFORE", "RETURNED" }, projected.Select(row => (string?)row["phase"]));
            Assert.All(projected, row => Assert.Equal(id, (string?)row["requestId"]));
            Assert.Equal("INTERNAL_ERROR", (string?)projected[1]["errorCode"]);
            Assert.Equal("read-failed", (string?)projected[1]["outcome"]);
            var recorded = log.Read();
            Assert.Equal(new[] { "request", "start", "end" }, recorded.Select(row => row.Event));
            Assert.All(recorded, row => Assert.Equal(id, row.RequestId));
            Assert.Equal("read-failed", recorded.Last().Outcome);
            Assert.True(log.Verify().Passed);
            Assert.DoesNotContain("private-secret", string.Join("\n", rows));
        }
        finally { TiaMcpServer.ModelContextProtocol.InvocationJournal.ConfigureOutput(null); }
    }

    [Fact]
    public async Task Sdk_hidden_generation_exception_is_named_only_in_test_failure_diagnostics()
    {
        using var diagnostics = new GenerationFailureDiagnostics();
        var inner = new GenerationTool("ListStandardPackages", "19", () => throw new InvalidOperationException(PrivateMessage));
        var tool = new FoundationV4Tool(inner, "19", null, () => new ApprovalSettings(true, 1));
        var result = await tool.InvokeAsync(Request(inner.ProtocolTool.Name));
        AssertInternalFailure(result, inner.ProtocolTool.Name, "not-applicable");
        string failure = diagnostics.Failure(inner.ProtocolTool.Name, result);
        Assert.Contains("ListStandardPackages", failure);
        Assert.Contains(typeof(InvalidOperationException).FullName!, failure);
        Assert.Contains(PrivateMessage, failure);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Json_inner_results_keep_the_existing_host_mapping(bool isError)
    {
        var raw = new JsonObject { ["success"] = !isError, ["summary"] = "JSON result", ["items"] = new JsonArray(1, 2) };
        var inner = new ResultTool("GetToolUsage", new CallToolResult { IsError = isError,
            Content = new[] { new TextContentBlock { Text = raw.ToJsonString() } } });
        var tool = new FoundationV4Tool(inner, "19", null, () => new ApprovalSettings(true, 1));
        var result = await tool.InvokeAsync(Request(inner.ProtocolTool.Name));
        string id = (string)result.StructuredContent!["meta"]!["requestId"]!;
        var expected = FoundationV4Result.Host("19", inner.ProtocolTool.Name, id, raw, false, isError,
            new Dictionary<string, JsonElement>());
        // Only the timestamp differs between independently constructed envelopes.
        expected.StructuredContent!["meta"]!["timestamp"] = result.StructuredContent["meta"]!["timestamp"]?.DeepClone();
        Assert.Equal(expected.IsError, result.IsError);
        Assert.True(JsonNode.DeepEquals(expected.StructuredContent, result.StructuredContent), result.StructuredContent.ToJsonString());
    }

    [Fact]
    public async Task Generation_diagnostics_are_isolated_between_concurrent_calls_and_stop_after_disposal()
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var result = new CallToolResult { Content = Array.Empty<ContentBlock>() };
        async Task<string> Capture(string message)
        {
            var diagnostics = new GenerationFailureDiagnostics();
            using (diagnostics)
            {
                await ready.Task;
                Assert.Throws<InvalidOperationException>((Action)(() => throw new InvalidOperationException(message)));
            }
            Assert.Throws<InvalidOperationException>((Action)(() => throw new InvalidOperationException("after-disposal")));
            return diagnostics.Failure("fixture", result);
        }
        var first = Capture("first-caller");
        var second = Capture("second-caller");
        ready.SetResult();
        var failures = await Task.WhenAll(first, second);
        Assert.Contains("first-caller", failures[0]);
        Assert.DoesNotContain("second-caller", failures[0]);
        Assert.Contains("second-caller", failures[1]);
        Assert.DoesNotContain("first-caller", failures[1]);
        Assert.All(failures, failure => Assert.DoesNotContain("after-disposal", failure));
    }

    private static void AssertInternalFailure(CallToolResult result, string name, string behaviorPolicy)
    {
        Assert.True(result.IsError);
        var body = result.StructuredContent!;
        Assert.False((bool)body["ok"]!);
        Assert.Null(body["data"]);
        Assert.Equal("INTERNAL_ERROR", (string?)body["error"]!["code"]);
        Assert.Equal("Foundation host operation failed.", (string?)body["error"]!["message"]);
        var expected = FoundationV4Result.HostFailure("19", name, (string)body["meta"]!["requestId"]!);
        Assert.True(JsonNode.DeepEquals(expected.StructuredContent!["error"], body["error"]));
        Assert.Equal("19", (string?)body["meta"]!["releaseKey"]);
        Assert.Equal(name, (string?)body["meta"]!["tool"]);
        Assert.Equal("read-failed", (string?)body["meta"]!["outcome"]);
        Assert.Equal("read-only", (string?)body["meta"]!["execution"]);
        Assert.Equal("none", (string?)body["meta"]!["completeness"]);
        Assert.Equal(behaviorPolicy, (string?)body["meta"]!["behaviorPolicy"]);
        Assert.Equal((string?)expected.StructuredContent["meta"]!["behaviorPolicy"], behaviorPolicy);
        string text = body.ToJsonString();
        Assert.Equal(text, Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text);
        Assert.DoesNotContain("Private", text);
        Assert.DoesNotContain("private-secret", text);
        Assert.DoesNotContain("An error occurred invoking", text);
        V4Json.Deserialize<Envelope>(text);
    }

    private sealed class ResultTool : McpServerTool
    {
        private readonly Tool tool;
        private readonly CallToolResult result;
        internal int Calls;
        internal ResultTool(string name, CallToolResult result)
        {
            this.result = result;
            tool = new Tool { Name = name, InputSchema = JsonSerializer.SerializeToElement(new {
                type = "object", properties = new { }, required = Array.Empty<string>(), additionalProperties = false }) };
        }
        public override Tool ProtocolTool => tool;
        public override ValueTask<CallToolResult> InvokeAsync(RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken = default)
        { Calls++; return ValueTask.FromResult(result); }
    }
}

// The SDK catches tool-body exceptions before returning its generic text result.
// Observe first-chance exceptions only in this test's async flow, without changing the client response.
internal sealed class GenerationFailureDiagnostics : IDisposable
{
    private static readonly AsyncLocal<GenerationFailureDiagnostics?> Current = new();
    private readonly GenerationFailureDiagnostics? previous = Current.Value;
    private readonly ConcurrentQueue<Exception> exceptions = new();

    internal GenerationFailureDiagnostics()
    {
        Current.Value = this;
        AppDomain.CurrentDomain.FirstChanceException += Observe;
    }
    private void Observe(object? sender, FirstChanceExceptionEventArgs args)
    { if (ReferenceEquals(Current.Value, this)) exceptions.Enqueue(args.Exception); }

    internal string Failure(string name, CallToolResult result)
        => name + ": " + string.Join("\n", result.Content.OfType<TextContentBlock>().Select(block => block.Text))
            + "\nInner exceptions (test only):\n" + string.Join("\n", exceptions.Select(ex => ex.GetType().FullName + ": " + ex.Message));

    public void Dispose()
    {
        AppDomain.CurrentDomain.FirstChanceException -= Observe;
        Current.Value = previous;
    }
}
