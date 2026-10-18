using System;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Adapters;
using TiaMcp.WorkerChannel;
using TiaMcpServer.Runtime;
using TiaMcpServer.Siemens;

namespace TiaMcp.PlcWorker;

// Managed facts and existing adapter primitives, on the worker's original owning lane.
internal static class WorkerObservations
{
    internal static ChannelResponse Reply(Func<JsonNode?> read)
    {
        try { return ChannelResponse.Success(read()?.ToJsonString() ?? "null"); }
        catch (Exception error) { return ChannelResponse.Error(new ChannelFailureException(error.Message, -32603, ChannelOutcome.ReadFailed)); }
    }
    internal sealed class Context
    {
        internal Func<JsonNode?> Environment = null!;
        internal Func<bool> Group = null!, FixGroup = null!;
        internal Type ReflectionType = null!;
        internal bool ReflectionProbe;
        internal Func<string[]> ToolNames = null!;
        internal Func<string, JsonObject, JsonNode?> Session = null!;
    }
    internal static JsonNode? Read(string operation, JsonObject arguments, Context context)
    {
        switch (operation)
        {
            case "environment": return context.Environment();
            case "group": return JsonValue.Create(context.Group());
            case "group.fix": return JsonValue.Create(context.FixGroup());
            case "tool-names": return JsonSerializer.SerializeToNode(context.ToolNames());
            case "assemblies":
                return JsonSerializer.SerializeToNode(AppDomain.CurrentDomain.GetAssemblies()
                    .Where(a => (a.GetName().Name ?? "").StartsWith("Siemens.Engineering", StringComparison.OrdinalIgnoreCase))
                    .Select(a => new { name = a.GetName().Name, version = a.GetName().Version?.ToString(), location = a.Location }).ToArray());
            case "reflection":
                var type = context.ReflectionType;
                return new JsonObject { ["guard"] = type.GetMethod("GetHardDeniedReflectionReason", BindingFlags.NonPublic | BindingFlags.Static) != null,
                    ["DescribeService"] = type.GetMethod("DescribeService") != null, ["InvokeService"] = type.GetMethod("InvokeService") != null,
                    ["InvokeObject"] = type.GetMethod("InvokeObject") != null, ["probe"] = context.ReflectionProbe };
            case "reflection-deny":
                return JsonValue.Create(context.ReflectionType.GetMethod("GetHardDeniedReflectionReason", BindingFlags.NonPublic | BindingFlags.Static)!
                    .Invoke(null, new object[] { new object(), (string)arguments["kind"]!, (string)arguments["path"]!, (string)arguments["method"]! }) as string);
            default:
                if (operation.StartsWith("session.", StringComparison.Ordinal)) return context.Session(operation.Substring("session.".Length), arguments);
                throw new NotSupportedException("Unknown worker observation: " + operation);
        }
    }
    internal static JsonNode? Read(PlcFoundationEngine engine, string operation, JsonObject arguments, Func<long>? processStart = null)
    {
        JsonObject CachedStatus()
        {
            var state = engine.ReadState();
            var cached = JsonSerializer.SerializeToNode(engine.ReadProjectLifecycleBinding());
            var identity = cached?["identity"];
            bool alive = false;
            if (state.IsAttached && state.ProcessId.HasValue && processStart?.Invoke() is > 0 and var ticks)
            {
                try
                {
                    using var observed = System.Diagnostics.Process.GetProcessById(state.ProcessId.Value);
                    alive = !observed.HasExited && observed.StartTime.ToUniversalTime().Ticks == ticks;
                }
                catch (Exception error) when (error is ArgumentException || error is InvalidOperationException
                    || error is System.ComponentModel.Win32Exception || error is System.Security.SecurityException)
                { /* swallow(env-probe): unavailable process identity is reported as disconnected. */ }
            }
            string project = (string?)identity?["projectName"] ?? "-";
            return new JsonObject { ["binding"] = identity?.DeepClone(),
                ["session"] = new JsonObject { ["isConnected"] = alive && cached?["fault"] == null,
                    ["project"] = project, ["session"] = state.IsLocalSession ? project : "-" },
                ["readiness"] = new JsonObject { ["ready"] = OpennessReadiness.Ready, ["cause"] = OpennessReadiness.Cause,
                    ["groupOk"] = OpennessReadiness.GroupOk, ["recommendedFix"] = OpennessReadiness.FixEn, ["recommendedFixZh"] = OpennessReadiness.FixZh } };
        }
        if (operation == "status") return CachedStatus();
        return Read(operation, arguments, new Context {
            Environment = () => JsonSerializer.SerializeToNode(EnvironmentDoctor.Run(Engineering.TiaMajorVersion, Engineering.TiaMajorVersion), new JsonSerializerOptions { IncludeFields = true }),
            Group = EnvironmentDoctor.CurrentUserInOpennessGroup, FixGroup = engine.EnsureOpennessUserGroup,
            ReflectionType = typeof(PlcFoundationEngine).Assembly.GetType("TiaMcp.Adapters.Reflection.OpennessReflection")!, ReflectionProbe = typeof(PlcFoundationEngine).GetMethod("ExecuteOnline") != null,
            ToolNames = () => throw new NotSupportedException("The host owns the tool roster."),
            Session = (name, values) => {
                switch (name)
                {
                    case "GetState":
                        var state = CachedStatus()["session"]!;
                        return new JsonObject { ["IsConnected"] = state["isConnected"]?.DeepClone(),
                            ["Project"] = state["project"]?.DeepClone(), ["Session"] = state["session"]?.DeepClone() };
                    // The frozen engine observation resolves two ConnectPortal overloads by name.
                    case "ConnectPortal": throw new AmbiguousMatchException();
                    case "GetProjectTree": return JsonValue.Create(engine.ReadProjectTree());
                    case "ValidateAutomationContext": return JsonSerializer.SerializeToNode(engine.ValidateAutomationContext((string?)values["plc"]!, (string?)values["hmi"]!));
                    default: throw new NotSupportedException("Unknown adapter session observation: " + name);
                }
            }
        });
    }
}
