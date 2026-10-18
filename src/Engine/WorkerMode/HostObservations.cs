using System;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcpServer.ModelContextProtocol;
using TiaMcpServer.Runtime;
using TiaMcpServer.Siemens;

namespace TiaMcpServer.Worker
{
    // Managed facts and existing session primitives for host-owned diagnostics.
    // No report builder or migrated MCP entry point runs in the worker.
    internal static class HostObservations
    {
        internal static JsonNode? Read(string operation, JsonObject arguments)
        {
            bool Group()
            {
                if (operation == "group" || operation == "group.fix")
                {
                    var type = typeof(HostObservations).Assembly.GetType("TiaMcpServer.Siemens.Openness")!;
                    object? value = type.GetMethod(operation == "group" ? "IsUserInGroupNoFix" : "IsUserInGroup")!.Invoke(null, null);
                    if (value is System.Threading.Tasks.Task<bool> task) value = task.GetAwaiter().GetResult();
                    return (bool)value!;
                }
                throw new ArgumentException("Unknown group observation: " + operation);
            }
            var portalType = typeof(HostObservations).Assembly.GetType("TiaMcpServer.Siemens.Portal")!;
            return TiaMcp.PlcWorker.WorkerObservations.Read(operation, arguments, new TiaMcp.PlcWorker.WorkerObservations.Context {
                ToolNames = () => McpServer.GetMcpToolNames()
                    .Concat(TiaMcp.Adapters.Contracts.PortedFamilies.All.Where(f => f.Available(McpServer.ReleaseKey)).SelectMany(f => f.Tools))
                    .Distinct(StringComparer.Ordinal).ToArray(),
                Environment = () => JsonSerializer.SerializeToNode(EnvironmentDoctor.Run(int.Parse(McpServer.ReleaseKey),
                    Engineering.TiaMajorVersion == 0 ? Engineering.DetectTiaMajorVersion() : Engineering.TiaMajorVersion), new JsonSerializerOptions { IncludeFields = true }),
                ReflectionType = portalType,
                ReflectionProbe = portalType.GetMethod("ProbePlcMonitorOnlineCapabilities", BindingFlags.Public | BindingFlags.Instance) != null,
                Group = Group, FixGroup = Group,
                Session = (name, values) => {
                    var portal = EngineServices.Get(portalType);
                    object?[] parameters = name == "ValidateAutomationContext"
                        ? new object?[] { (string?)values["plc"], (string?)values["hmi"] } : Array.Empty<object?>();
                    try
                    {
                        var result = portalType.GetMethod(name)!.Invoke(portal, parameters);
                        return result == null ? null : JsonSerializer.SerializeToNode(result, result.GetType());
                    }
                    catch (TargetInvocationException error) when (error.InnerException != null)
                    { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
                }
            });
        }
    }
}
