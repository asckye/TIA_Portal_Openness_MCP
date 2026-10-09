#if TIA_FOUNDATION_TEST_HOST
extern alias enginehost;
using GenerationTools = enginehost::TiaMcpServer.ModelContextProtocol.GenerationTools;
using DeclaredToolMetadata = enginehost::TiaMcpServer.ModelContextProtocol.DeclaredToolMetadata;
#endif
using System.Reflection;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.Logic.Generation;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcp.FoundationHost;

internal sealed class GenerationTool : McpServerTool
{
    private readonly McpServerTool inner;
    private readonly Tool tool;
    internal GenerationTool(string name, string release, Func<StandardPackageStore>? store = null)
    {
        var target = new GenerationTools(release, store);
        var method = typeof(GenerationTools).GetMethods().Single(m => m.GetCustomAttribute<McpServerToolAttribute>()?.Name == name);
        inner = DeclaredToolMetadata.Create(name, method, _ => target, out _);
        var schema = System.Text.Json.Nodes.JsonNode.Parse(inner.ProtocolTool.InputSchema.GetRawText())!.AsObject();
        if (schema["properties"]?["machine"] is System.Text.Json.Nodes.JsonObject machine) machine["type"] = "object";
        if (schema["properties"]?["action"] is System.Text.Json.Nodes.JsonObject action)
            action["enum"] = name == "ManageStandardPackage" ? new System.Text.Json.Nodes.JsonArray("import", "export", "copy", "fork", "remove")
                : new System.Text.Json.Nodes.JsonArray("validate", "import", "export", "exportTemplate");
        if (schema["properties"]?["format"] is System.Text.Json.Nodes.JsonObject format) format["enum"] = new System.Text.Json.Nodes.JsonArray("json", "csv");
        tool = new Tool { Name = name, Description = inner.ProtocolTool.Description, InputSchema = JsonSerializer.SerializeToElement(schema) };
    }
    internal static IEnumerable<McpServerTool> Create(string release)
    {
        yield return new GenerationTool("ListStandardPackages", release);
        yield return new GenerationTool("ManageStandardPackage", release);
        yield return new GenerationTool("ValidateStandardPackage", release);
        yield return new GenerationTool("DescribeStandardPackage", release);
        yield return new GenerationTool("ManageMachineDescription", release);
    }
    internal bool ApprovalWrite(System.Text.Json.Nodes.JsonObject arguments) => ProtocolTool.Name == "ManageStandardPackage"
        || ProtocolTool.Name == "ManageMachineDescription" && (string?)arguments["action"] != "validate"
            && !string.IsNullOrEmpty((string?)arguments["outputPath"]);
    public override Tool ProtocolTool => tool;
    public override ValueTask<CallToolResult> InvokeAsync(RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken = default)
    { cancellationToken.ThrowIfCancellationRequested(); return inner.InvokeAsync(request, cancellationToken); }
}
