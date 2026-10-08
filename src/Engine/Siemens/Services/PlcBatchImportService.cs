using System;
using System.Text.Json;
using System.Linq;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using TiaMcp.Logic.V4;
using TiaMcp.Adapters;
using TiaMcp.Adapters.Contracts;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcpServer.Siemens.Services
{
    internal static class PlcBatchImportService
    {
        internal static CallToolResult Run(IEngineeringSession session, string tool, PlcBatchImportRequest request, string softwarePath, bool dryRun)
        {
            PlcBatchImportResult? outcome = null;
            try
            {
                var project = session.CurrentProject;
                if (project == null) throw new AdapterPreconditionException("Bind a project before importing.", "expectedProjectFile", false);
                var software = session.ExactPlcForEngineering(softwarePath, false);
                session.VerifyBinding(tool);
                var binding = session.GetBindingIdentity()["identity"]!.AsObject();
                int processId = binding["processId"]!.GetValue<int>();
                string generation = binding["generation"]!.GetValue<string>();
                void Check()
                {
                    session.VerifyBinding(tool);
                    if (!object.Equals(project, session.CurrentProject) || session.GetBindingIdentity()["identity"]!["generation"]!.GetValue<string>() != generation
                        || !object.Equals(software, session.ExactPlcForEngineering(softwarePath, true))) throw new AdapterPreconditionException("Project/PLC binding changed.", "softwarePath", false);
                }
                var result = outcome = PlcBatchImportRunner.Run(request, software, Check, McpServer.ReleaseKey, project.Path.FullName, processId);
                var data = JsonSerializer.SerializeToNode(result, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })!.AsObject();
                var mapped = McpResult.From(PlcBatchImportResultMapping.Result(data, McpServer.ReleaseKey, tool, Meta.Correlate(InvocationJournal.CorrelationId), dryRun));
                return new CallToolResult { IsError = mapped.IsError, StructuredContent = JsonNode.Parse(mapped.StructuredContent.GetRawText()), Content = new[] { new TextContentBlock { Text = mapped.Content[0].Text } } };
            }
            catch (AdapterPreconditionException ex)
            { return McpServer.TargetFailure(tool, ex, false); }
            catch (Exception ex)
            {
                var failed = McpServer.TargetFailure(tool, ex, outcome != null && outcome.Items.Any(x => x.Attempted));
                if (outcome == null || string.IsNullOrEmpty(outcome.RecoveryDirectory)) return failed;
                var body = McpServer.ResultBody(failed)!.DeepClone().AsObject();
                body["data"]!["recoveryDirectory"] = outcome.RecoveryDirectory;
                if (body["error"]?["details"]?["evidence"] is JsonObject evidence) evidence["recoveryDirectory"] = outcome.RecoveryDirectory;
                return new CallToolResult { IsError = true, StructuredContent = body, Content = new[] { new TextContentBlock { Text = body.ToJsonString() } } };
            }
        }
    }
}
