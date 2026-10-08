using ModelContextProtocol.Protocol;
using TiaMcp.Logic.V4.Inputs;
using System.ComponentModel;
using System.Linq;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;
using TiaMcpServer.Isolation;

namespace TiaMcpServer.ModelContextProtocol
{
    public static partial class McpServer
    {
        [McpServerTool(Name = "GetOpennessWorkerStatus"), Description("[L0][Diagnostics][READ] Read the local Openness worker supervisor without contacting TIA. Reports process state, generation, deadline, queue and fault. Available while the worker is hung or faulted. Use --isolate-openness or --no-isolate-openness to override the build default; a healthy worker is not proof of native TIA stability.")]
        public static CallToolResult GetOpennessWorkerStatusV4()
            => SessionToolContract.Run("GetOpennessWorkerStatus", false, false, () => ReadOpennessWorkerStatus());

        public static ResponseMessage ReadOpennessWorkerStatus()
        {
#if TIA_ENGINE_HOST
            var worker = Worker.Snapshot();
#else
            var metadata = typeof(McpServer).Assembly.GetCustomAttributes<System.Reflection.AssemblyMetadataAttribute>()
                .FirstOrDefault(attribute => attribute.Key == "TiaMcpWorkerIsolationDefault")?.Value;
            var worker = IsolatedWorkerHost.Current?.Snapshot() ?? new JsonObject {
                ["enabled"] = false, ["state"] = "InProcess", ["enabledByDefault"] = string.Equals(metadata, "true", System.StringComparison.OrdinalIgnoreCase),
                ["forceOn"] = "--isolate-openness", ["forceOff"] = "--no-isolate-openness" };
            worker["enabledByDefault"] = string.Equals(metadata, "true", System.StringComparison.OrdinalIgnoreCase);
            worker["forceOn"] = "--isolate-openness";
            worker["forceOff"] = "--no-isolate-openness";
            worker["environmentReady"] = Runtime.OpennessReadiness.Ready;
            worker["environmentCause"] = Runtime.OpennessReadiness.Cause;
#endif
            return new ResponseMessage {
                Message = "Local worker and TIA environment state; no native call was made.",
                Meta = ResponseMeta.Unstamped(true, ("worker", worker))
            };
        }

        [McpServerTool(Name = "RestartOpennessWorker"), Description("[L0][Diagnostics][WRITE] Preview or explicitly reset an idle/faulted isolated Openness worker. confirmRestart defaults false. Refuses while calls remain active/queued. Resets all worker-held project bindings, exports and preview plans; never saves, attaches or replays a write. After confirmation, explicitly connect to the intended project again. An idle responsive attachment is detached and its process lease released before reset. A hung or lost worker is terminated with the lease ACTIVE; requiresTiaRestart reports whether TIA must restart before reconnecting. Does not kill TIA; an interrupted native operation can have an unknown outcome.")]
        public static CallToolResult RestartOpennessWorkerV4(
            [Description("confirmRestart: false previews only; true discards the idle/faulted worker and its bindings without replaying any operation.")] bool confirmRestart = false)
            => SessionToolContract.Run("RestartOpennessWorker", confirmRestart, false, () => RestartOpennessWorker(confirmRestart));

        public static ResponseMessage RestartOpennessWorker(
            [Description("confirmRestart: false previews only; true discards the idle/faulted worker and its bindings without replaying any operation.")] bool confirmRestart = false)
        {
#if TIA_ENGINE_HOST
            return new ResponseMessage { Message = "Worker reset request evaluated; no TIA project was opened or saved.",
                Meta = Worker.Restart(confirmRestart, System.Threading.CancellationToken.None).GetAwaiter().GetResult() };
#else
            var supervisor = IsolatedWorkerHost.Current;
            return new ResponseMessage { Message = supervisor == null ? "Isolated worker mode is not enabled." : "Worker reset request evaluated; no TIA project was opened or saved.",
                Meta = supervisor?.Restart(confirmRestart) ?? ResponseMeta.Unstamped(false, ("enabled", false)) };
#endif
        }
    }
}
