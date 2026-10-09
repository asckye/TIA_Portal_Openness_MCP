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
            var portalType = typeof(HostObservations).Assembly.GetType("TiaMcpServer.Siemens.Portal")!;
            if (operation == "tool-names")
                return JsonSerializer.SerializeToNode(McpServer.GetMcpToolNames()
                    .Concat(TiaMcp.Adapters.Contracts.PortedFamilies.All.Where(f => f.Available(McpServer.ReleaseKey)).SelectMany(f => f.Tools))
                    .Distinct(StringComparer.Ordinal).ToArray());
            if (operation == "environment")
                return JsonSerializer.SerializeToNode(EnvironmentDoctor.Run(int.Parse(McpServer.ReleaseKey),
                    Engineering.TiaMajorVersion == 0 ? Engineering.DetectTiaMajorVersion() : Engineering.TiaMajorVersion),
                    new JsonSerializerOptions { IncludeFields = true });
            if (operation == "assemblies")
                return JsonSerializer.SerializeToNode(AppDomain.CurrentDomain.GetAssemblies()
                    .Where(a => (a.GetName().Name ?? "").StartsWith("Siemens.Engineering", StringComparison.OrdinalIgnoreCase))
                    .Select(a => new { name = a.GetName().Name, version = a.GetName().Version?.ToString(), location = a.Location }).ToArray());
            if (operation == "reflection")
                return new JsonObject {
                    ["guard"] = portalType.GetMethod("GetHardDeniedReflectionReason", BindingFlags.NonPublic | BindingFlags.Static) != null,
                    ["DescribeService"] = portalType.GetMethod("DescribeService", BindingFlags.Public | BindingFlags.Instance) != null,
                    ["InvokeService"] = portalType.GetMethod("InvokeService", BindingFlags.Public | BindingFlags.Instance) != null,
                    ["InvokeObject"] = portalType.GetMethod("InvokeObject", BindingFlags.Public | BindingFlags.Instance) != null,
                    ["probe"] = portalType.GetMethod("ProbePlcMonitorOnlineCapabilities", BindingFlags.Public | BindingFlags.Instance) != null
                };
            if (operation == "reflection-deny")
                return JsonValue.Create(portalType.GetMethod("GetHardDeniedReflectionReason", BindingFlags.NonPublic | BindingFlags.Static)!
                    .Invoke(null, new object[] { new object(), (string)arguments["kind"]!, (string)arguments["path"]!, (string)arguments["method"]! }) as string);
            if (operation == "group" || operation == "group.fix")
            {
                var type = typeof(HostObservations).Assembly.GetType("TiaMcpServer.Siemens.Openness")!;
                object? value = type.GetMethod(operation == "group" ? "IsUserInGroupNoFix" : "IsUserInGroup")!.Invoke(null, null);
                if (value is System.Threading.Tasks.Task<bool> task) value = task.GetAwaiter().GetResult();
                return JsonValue.Create((bool)value!);
            }
            if (operation.StartsWith("session.", StringComparison.Ordinal))
            {
                var portal = EngineServices.Get(portalType);
                var name = operation.Substring("session.".Length);
                object?[] values = name == "ValidateAutomationContext"
                    ? new object?[] { (string?)arguments["plc"], (string?)arguments["hmi"] } : Array.Empty<object?>();
                try
                {
                    var result = portalType.GetMethod(name)!.Invoke(portal, values);
                    return result == null ? null : JsonSerializer.SerializeToNode(result, result.GetType());
                }
                catch (TargetInvocationException error) when (error.InnerException != null)
                { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
            }
            throw new ArgumentException("Unknown host observation: " + operation);
        }
    }
}
