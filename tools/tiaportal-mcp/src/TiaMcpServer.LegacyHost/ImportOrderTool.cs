using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaOpenness.Shared;

namespace TiaMcp.LegacyHost;

internal sealed class ImportOrderTool : McpServerTool
{
    private static readonly Tool Tool = new() {
        Name = "PlanArtifactImportOrder",
        Description = "Offline dependency-first import planning shared across every release. artifactsJson is an array of Id, Target, Priority, Dependencies; 1..256 unique IDs. Missing/cyclic dependencies return no order. Explicit dependencies override priority. Does not infer dependencies, access files/TIA or import. Reuses the MIT EidoTiaWorkbench planner.",
        InputSchema = JsonSerializer.SerializeToElement(new JsonObject { ["type"]="object", ["additionalProperties"]=false,
            ["properties"]=new JsonObject { ["artifactsJson"]=new JsonObject { ["type"]="string", ["maxLength"]=1024*1024 } }, ["required"]=new JsonArray("artifactsJson") })
    };
    public override Tool ProtocolTool => Tool;
    public override ValueTask<CallToolResult> InvokeAsync(RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try {
            var args=request.Params?.Arguments;
            if(args == null || args.Count != 1 || !args.TryGetValue("artifactsJson", out var value) || value.ValueKind != JsonValueKind.String) throw new ArgumentException("artifactsJson string is required.");
            var json=value.GetString()!;
            if(json.Length > 1024*1024) throw new ArgumentException("Provide at most one MiB of JSON.");
            var plan=ImportDependencyPlanner.Build(JsonSerializer.Deserialize<ImportOrderItem[]>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!);
            return ValueTask.FromResult(new CallToolResult { Content = new List<ContentBlock> { new TextContentBlock { Text=JsonSerializer.Serialize(plan) } } });
        } catch(Exception ex) when(ex is ArgumentException or JsonException) {
            return ValueTask.FromResult(new CallToolResult { IsError=true, Content=new List<ContentBlock> { new TextContentBlock { Text=ex.Message } } });
        }
    }
}
