using Microsoft.Extensions.DependencyInjection;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcp.FoundationHost;

internal static class HostToolCommand
{
    internal static async Task Run(string command, EngineHostPipeline pipeline)
    {
        var arguments = JsonNode.Parse(await Console.In.ReadLineAsync() ?? "{}")!.AsObject();
        object? result;
        if (command == "CallTool")
        {
            var name = (string)arguments["name"]!;
            var selected = pipeline.AllTools.Single(t => t.ProtocolTool.Name == name);
            var request = new global::ModelContextProtocol.Server.RequestContext<global::ModelContextProtocol.Protocol.CallToolRequestParams>(
                DispatchProxy.Create<global::ModelContextProtocol.Server.IMcpServer, CommandServer>()) {
                Params = new global::ModelContextProtocol.Protocol.CallToolRequestParams { Name = name,
                    Arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>((arguments["arguments"] ?? new JsonObject()).ToJsonString()) }
            };
            result = (await selected.InvokeAsync(request)).StructuredContent;
        }
        else if (command == "ToolMetadata")
        {
            var methods = new JsonObject();
            foreach (var method in PortedToolDeclarations.Methods(EngineHostConfiguration.ReleaseKey))
                methods[method.GetCustomAttribute<global::ModelContextProtocol.Server.McpServerToolAttribute>()!.Name!] = new JsonObject {
                    ["method"] = method.Name,
                    ["parameters"] = new JsonArray(method.GetParameters().Select(p => (JsonNode)new JsonObject {
                        ["name"] = p.Name, ["required"] = !p.IsOptional
                    }).ToArray())
                };
            result = methods;
        }
        else if (command == "OfflineReleaseValidationSuite")
            result = OfflineReleaseValidationSuite.Run((string?)arguments["workspaceRoot"] ?? "", (string?)arguments["reportDir"] ?? "");
        else
        {
            if (command is not ("RunOnlineMonitoringSafetySelfTest" or "GenerateAcceptanceReport" or "GenerateErrorReport"))
                throw new ArgumentException("Unknown host command: " + command);
            var method = typeof(HostMetaTools).GetMethod(command)!;
            var values = method.GetParameters().Select(p => arguments.TryGetPropertyValue(p.Name!, out var value)
                ? JsonSerializer.Deserialize(value!.ToJsonString(), p.ParameterType) : p.DefaultValue).ToArray();
            try { result = method.Invoke(new HostMetaTools(), values); }
            catch (TargetInvocationException error) when (error.InnerException != null)
            { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
            if (result is Task task) { await task; result = task.GetType().GetProperty("Result")!.GetValue(task); }
        }
        Console.WriteLine(JsonSerializer.Serialize(result, result?.GetType() ?? typeof(object), new JsonSerializerOptions { WriteIndented = true }));
    }

    private class CommandServer : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? method, object?[]? arguments)
        {
            if (method?.Name == "get_Services") return Services;
            throw new InvalidOperationException("The one-shot CLI has no protocol peer: " + method?.Name);
        }
        private static readonly IServiceProvider Services = new Microsoft.Extensions.DependencyInjection.ServiceCollection().BuildServiceProvider();
    }
}
