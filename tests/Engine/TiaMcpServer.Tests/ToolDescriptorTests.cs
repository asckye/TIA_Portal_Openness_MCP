using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.Logic.V4;
using TiaMcp.Logic.V4.Inputs;
using TiaMcpServer.ModelContextProtocol;
using Xunit;

namespace TiaMcpServer.Tests
{
    public sealed class ToolDescriptorTests : IDisposable
    {
        private sealed class Probe
        {
            [McpServerTool(Name = "DescriptorRequiredProbe")]
            public static CallToolResult Required(string value, bool dryRun = true)
                => McpServer.V4Result("DescriptorRequiredProbe", new JsonObject { ["value"] = value });

            [McpServerTool(Name = "DescriptorProbe"), Description("Descriptor fixture."),
                ToolClassification("L2", "Diagnostics", "WRITE", batchWrite: true)]
            public static CallToolResult Invoke(bool dryRun = true, string text = "fixture")
            {
                if (!dryRun) InvocationJournal.NativeCallStarted();
                return McpServer.V4Result("DescriptorProbe", new JsonObject { ["text"] = text });
            }
        }

        public void Dispose() => ToolBridgeFixture.Configure();

        [Fact]
        public void DescriptorCapturesDefaultsWithoutLosingBooleanTextOrSignature()
        {
            var catalog = new ToolCatalog(new[] { typeof(Probe) });
            var tool = catalog.Find("descriptorprobe")!;
            Assert.Equal("DescriptorProbe", tool.Name);
            Assert.Equal("Descriptor fixture.", tool.RawDescription);
            Assert.Equal(McpServer.CreateTool(tool.Name, typeof(Probe).GetMethod("Invoke")!).ProtocolTool.InputSchema.GetRawText(), tool.Tool.InputSchema.GetRawText());
            Assert.Equal("DescriptorProbe(dryRun?: boolean = true, text?: string = \"fixture\")", tool.Signature);
            Assert.Equal("true", tool.Parameters[0].DefaultJson);
            Assert.Equal("True", tool.Parameters[0].DefaultText);
            Assert.Equal("System.Boolean", tool.Parameters[0].ClrType);
            Assert.True(tool.DryRun.Present);
            Assert.True(tool.DryRun.Default);
            Assert.True(tool.Classification!.BatchWrite);
            Assert.Equal("L2", tool.Classification.Level);
            var required = catalog.Find("DescriptorRequiredProbe")!.Parameters[0];
            Assert.True(required.Required);
            Assert.Null(required.DefaultJson);
            Assert.Equal(typeof(Probe).GetMethod("Required")!.GetParameters()[0].DefaultValue?.ToString(), required.DefaultText);
            Assert.Equal(new[] { "candidateFamily", "classification", "dryRun", "execution", "name", "parameters", "rawDescription", "signature", "tool" },
                JsonSerializer.SerializeToElement(tool, new JsonSerializerOptions(JsonSerializerDefaults.Web)).EnumerateObject()
                    .Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal));
        }

        [Fact]
        public void InProcessInvokerBindsOnceAndKeepsNativeEvidenceInTheParent()
        {
            var catalog = new ToolCatalog(new[] { typeof(Probe) });
            var invoker = new InProcessToolInvoker(catalog);
            McpServer.ConfigureToolBridge(catalog, invoker, () => false, new HashSet<string>());
            using var native = InvocationJournal.BeginNativeCallScope();
            Assert.Null(invoker.Bind("DescriptorProbe", Args("{\"dryRun\":false}"), out var call));
            var result = call!.Invoke(false);
            Assert.True(result.NativeCallIssued);
            Assert.True(native.NativeCallIssued);
            Assert.False(result.Result.IsError);
            Assert.False(invoker.Invoke("DescriptorProbe", Args("{}"), true).NativeCallIssued);
            Assert.True(invoker.Invoke("descriptorprobe", Args("{}"), false).Result.IsError);
        }

        [Fact]
        public void BridgeDiscoveryUsageAndApprovalWorkWithADescriptorOnlyCatalog()
        {
            var source = new ToolCatalog(new[] { typeof(Probe) }).Find("DescriptorProbe")!;
            var published = new Tool { Name = "CreatePlcTag", Description = source.Tool.Description, InputSchema = source.Tool.InputSchema };
            source = new ToolDescriptor("CreatePlcTag", source.RawDescription, published, source.Classification,
                source.Signature.Replace("DescriptorProbe(", "CreatePlcTag("), source.Parameters, source.DryRun, null, "worker");
            var view = new DescriptorView(source);
            var invoker = new DescriptorInvoker();
            McpServer.ConfigureToolBridge(view, invoker, () => true, new HashSet<string>());
            Assert.Contains("CreatePlcTag", McpServer.FindTools("CreatePlcTag").Items!.First());
            Assert.Equal(1, (int)McpServer.ListToolCategories().Meta!["toolCount"]!);
            Assert.False(McpServer.ApprovalWrite("CreatePlcTag", "{}"));
            Assert.True(McpServer.ApprovalWrite("CreatePlcTag", "{\"dryRun\":false}"));
            Assert.False(new ToolUsageTools().GetToolUsage("CreatePlcTag").IsError);
            Assert.False(McpServer.PreviewToolCall("CreatePlcTag").IsError);
            var result = McpServer.CallTool("CreatePlcTag");
            Assert.Same(invoker.Result, result);
            Assert.Equal(1, invoker.Invocations);
            Assert.Equal(2, invoker.Bindings);
            Assert.Single(McpServer.GetAllTools());
            Assert.Single(McpServer.GetLiteTools());
        }

        [Fact]
        public void ExecutionOwnershipIsExplicitAndDistinctFromReadiness()
        {
            Assert.Equal(491, ToolExecution.Table.Count);
            Assert.Equal(45, ToolExecution.Table.Count(pair => pair.Value == "host"));
            foreach (var name in new[] { "CallTool", "GetExportContent", "StageImportFiles", "RestartOpennessWorker", "BuildStructuredText", "RenderPlcBlock" })
                Assert.Equal("host", ToolExecution.Table[name]);
            foreach (var name in new[] { "GetOpennessCompatibility", "InitializeEnvironment", "GetEnvironmentDiagnostics", "CheckProductUpdate", "BuildAndImportPlcArtifact", "BuildDeviceAmlDocument" })
                Assert.Equal("worker", ToolExecution.Table[name]);
            Assert.Equal(BehaviorCapabilities.Table(typeof(ToolCatalog).Assembly, McpServer.ReleaseKey).ToJsonString(), new ToolCatalog(new[] { typeof(Probe) }).BehaviorCapabilities.ToJsonString());
        }

        private static ToolArguments Args(string json) => new ToolArguments(JsonDocument.Parse(json).RootElement.Clone());

        private sealed class DescriptorView : IToolCatalogView
        {
            public IReadOnlyDictionary<string, ToolDescriptor> All { get; }
            public IReadOnlyDictionary<string, ToolDescriptor> IncludingUnavailable => All;
            public IReadOnlyList<ToolDescriptor> Lite { get; }
            public JsonArray BehaviorCapabilities => new JsonArray();
            internal DescriptorView(ToolDescriptor tool)
            { All = new Dictionary<string, ToolDescriptor>(StringComparer.OrdinalIgnoreCase) { [tool.Name] = tool }; Lite = new[] { tool }; }
            public ToolDescriptor? Find(string name, bool includeUnavailable = false) => All.TryGetValue(name, out var tool) ? tool : null;
        }

        private sealed class DescriptorInvoker : IToolInvoker, IBoundToolCall
        {
            internal int Bindings, Invocations;
            internal readonly CallToolResult Result = McpServer.V4Result("DescriptorProbe", new JsonObject { ["fixture"] = true });
            public Error? Bind(string name, ToolArguments arguments, out IBoundToolCall? call) { Bindings++; call = this; return null; }
            public ToolInvocationResult Invoke(string name, ToolArguments arguments, bool preview) => Invoke(preview);
            public ToolInvocationResult Invoke(bool preview) { Invocations++; return new ToolInvocationResult(Result, false); }
            public Error? ValidateArguments(ToolDescriptor tool, JsonElement arguments, JsonElement schema, bool typedFamiliesOnly = false) => null;
            public McpServerTool CreateTool(ToolDescriptor tool) => new DescriptorRuntimeTool(tool.Tool);
        }

        private sealed class DescriptorRuntimeTool : McpServerTool
        {
            private readonly Tool tool;
            internal DescriptorRuntimeTool(Tool tool) { this.tool = tool; }
            public override Tool ProtocolTool => tool;
            public override System.Threading.Tasks.ValueTask<CallToolResult> InvokeAsync(RequestContext<CallToolRequestParams> request,
                System.Threading.CancellationToken cancellationToken = default) => throw new InvalidOperationException("Registration fixture only.");
        }
    }
}
