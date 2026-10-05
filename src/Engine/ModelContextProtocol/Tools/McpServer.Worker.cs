using ModelContextProtocol.Protocol;
using TiaMcp.Logic.V4.Inputs;
using System.ComponentModel;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;
using TiaMcpServer.Isolation;

namespace TiaMcpServer.ModelContextProtocol
{
    public static partial class McpServer
    {
        [McpServerTool(Name = "GetOpennessWorkerStatus"), Description("[L0][Diagnostics][READ] Read the local Openness worker supervisor without contacting TIA. Reports process state, generation, deadline, queue and fault. Available while the worker is hung or faulted. Isolation requires --isolate-openness; a healthy worker is not proof of native TIA stability.")]
        public static CallToolResult GetOpennessWorkerStatusV4()
            => SessionToolContract.Run("GetOpennessWorkerStatus", false, false, () => ReadOpennessWorkerStatus());

        public static ResponseMessage ReadOpennessWorkerStatus() => new ResponseMessage {
            Message = "Local worker supervisor state; no native call was made.",
            Meta = ResponseMeta.Unstamped(true, ("worker", IsolatedWorkerHost.Current?.Snapshot() ?? new JsonObject {
                ["enabled"] = false, ["state"] = "InProcess", ["enableWith"] = "--isolate-openness" }))
        };

        [McpServerTool(Name = "RestartOpennessWorker"), Description("[L0][Diagnostics][WRITE] Preview or explicitly reset an idle/faulted isolated Openness worker. confirmRestart defaults false. Refuses while calls remain active/queued. Resets all worker-held project bindings, exports and preview plans; never saves, attaches or replays a write. After confirmation, explicitly connect to the intended project again. Does not kill TIA; an interrupted native operation can have an unknown outcome.")]
        public static CallToolResult RestartOpennessWorkerV4(
            [Description("confirmRestart: false previews only; true discards the idle/faulted worker and its bindings without replaying any operation.")] bool confirmRestart = false)
            => SessionToolContract.Run("RestartOpennessWorker", confirmRestart, false, () => RestartOpennessWorker(confirmRestart));

        public static ResponseMessage RestartOpennessWorker(
            [Description("confirmRestart: false previews only; true discards the idle/faulted worker and its bindings without replaying any operation.")] bool confirmRestart = false)
        {
            var supervisor = IsolatedWorkerHost.Current;
            return new ResponseMessage { Message = supervisor == null ? "Isolated worker mode is not enabled." : "Worker reset request evaluated; no TIA project was opened or saved.",
                Meta = supervisor?.Restart(confirmRestart) ?? ResponseMeta.Unstamped(false, ("enabled", false)) };
        }
    }
}
