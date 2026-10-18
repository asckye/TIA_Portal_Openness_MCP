using TiaMcpServer;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.Logic.V4;
using TiaMcp.Logic.V4.Inputs;
using TiaMcpServer.ModelContextProtocol;
using TiaOpenness.Shared;
using Xunit;

namespace TiaMcpServer.Siemens
{
    internal sealed partial class Portal
    {
        internal void EnsureBoundProjectUnchanged(string operation) { }
        internal JsonObject GetPortalProcessHealth() => new JsonObject { ["boundProcessId"] = 1, ["processAlive"] = true };
        internal JsonObject GetBindingIdentity() => JsonNode.Parse("{\"identity\":{\"tiaMajorVersion\":21,\"processId\":1,\"processStartUtc\":\"2026-01-01T00:00:00Z\",\"projectPath\":\"fixture.ap21\",\"projectName\":\"Fixture\",\"generation\":1}}")!.AsObject();
    }
}

namespace TiaMcpServer.ModelContextProtocol
{
    public static partial class McpServer { }
    internal sealed class SessionTools
    {
        internal (bool IsConnected, string Project) GetState() => (true, "Fixture");
    }
}

namespace TiaMcp.Engine.Tests
{
    public sealed class InfrastructureContractsTests : IDisposable
    {
        public sealed class BatchProbes
        {
            internal static readonly List<string> Calls = new List<string>();
            [McpServerTool(Name = "ReadFixture"), ToolClassification("L0", "Meta", "READ", batchRead: true), System.ComponentModel.Description("Read a fixture; arbitrary translated text.")]
            public static CallToolResult Read(bool success = true) => success
                ? McpServer.V4Result("ReadFixture", new JsonObject { ["summary"] = "fixture" })
                : McpServer.V4Result("ReadFixture", new JsonObject { ["summary"] = "fixture" },
                    new Error("Fixture read failed.", new InternalErrorDetails(null)), Outcome.ReadFailed, Execution.ReadOnly, Completeness.None);
            [McpServerTool(Name = "WriteFixture"), ToolClassification("L2", "Meta", "WRITE", batchWrite: true), System.ComponentModel.Description("Write a fixture; arbitrary translated text.")]
            public static CallToolResult Write(string target, bool dryRun = true, string verdict = "success")
            {
                Calls.Add(target + ":" + dryRun);
                var data = new JsonObject { ["target"] = target };
                if (dryRun || verdict == "success") return McpServer.V4Result("WriteFixture", data);
                if (verdict == "okOnly") return new CallToolResult { Content = new[] { new TextContentBlock { Text = "{\"ok\":true}" } } };
                bool unknown = verdict == "unknown";
                return McpServer.V4Result("WriteFixture", data, McpServer.InvalidInput("target"),
                    unknown ? Outcome.Unknown : Outcome.Failed, unknown ? Execution.Unknown : Execution.Completed, Completeness.None);
            }
        }

        public InfrastructureContractsTests()
        {
            BatchProbes.Calls.Clear();
            var catalog = new ToolCatalog(new[] { typeof(McpServer), typeof(ToolUsageTools), typeof(BatchProbes), typeof(ToolBridgeProbes), typeof(DcbVersionProbe) });
            McpServer.ConfigureToolBridge(catalog, () => true, new HashSet<string> { "CallTool" });
            EngineServices.SetServiceProvider(new ServiceCollection().AddEngine(false, catalog)
                .AddSingleton<TiaMcpServer.Siemens.Portal>().AddSingleton<SessionTools>().BuildServiceProvider());
        }
        public void Dispose() => ToolBridgeFixture.Configure();
        private static ToolArguments Args(string json = "{}") => new ToolArguments(JsonSerializer.Deserialize<JsonElement>(json));
        private static JsonObject Body(CallToolResult result) => McpServer.ResultBody(result)!.AsObject();
        private static string? Code(CallToolResult result) => (string?)Body(result)["error"]?["code"];
        private static ToolCall Call(string name, string args = "{}") => new ToolCall(name, Args(args));
        private static RequestContext<CallToolRequestParams> Request(string name, string json)
            => new RequestContext<CallToolRequestParams>(DispatchProxy.Create<IMcpServer, ToolCatalogServerProxy>())
            { Params = new CallToolRequestParams { Name = name, Arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json) } };
    }
}
