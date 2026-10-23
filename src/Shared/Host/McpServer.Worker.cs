#if !TIA_ENGINE_PORTED
using ModelContextProtocol.Protocol;
using TiaMcp.Logic.V4.Inputs;
using System.ComponentModel;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using ModelContextProtocol.Server;

namespace TiaMcpServer.ModelContextProtocol
{
    public static partial class McpServer
    {
        [McpServerTool(Name = "GetOpennessWorkerStatus"), Description("[L0][Diagnostics][READ] Read the local Openness worker supervisor without contacting TIA. Reports process state, generation, deadline, queue and fault. Available while the worker is hung or faulted. FoundationHost owns one engine worker per MCP session; a healthy worker is not proof of native TIA stability.")]
        [AsyncDispatch(nameof(GetOpennessWorkerStatusV4Async))]
        public static CallToolResult GetOpennessWorkerStatusV4()
            => GetOpennessWorkerStatusV4Async().GetAwaiter().GetResult();

        internal static Task<CallToolResult> GetOpennessWorkerStatusV4Async()
            => SessionToolContract.RunAsync("GetOpennessWorkerStatus", false, false, ReadOpennessWorkerStatusAsync);

        public static ResponseMessage ReadOpennessWorkerStatus()
            => ReadOpennessWorkerStatusAsync().GetAwaiter().GetResult();

        internal static async Task<ResponseMessage> ReadOpennessWorkerStatusAsync()
        {
            await BridgeWait.On(InitializeWorkerSupervisorAsync()).ConfigureAwait(false);
            var worker = Worker.Snapshot();
            return new ResponseMessage {
                Message = "Local worker and TIA environment state; no native call was made.",
                Meta = ResponseMeta.Unstamped(true, ("worker", worker))
            };
        }

        [McpServerTool(Name = "RestartOpennessWorker"), Description("[L0][Diagnostics][WRITE] Preview or explicitly reset an idle/faulted isolated Openness worker. confirmRestart defaults false. Refuses while calls remain active/queued. Resets all worker-held project bindings, exports and preview plans; never saves, attaches or replays a write. After confirmation, explicitly connect to the intended project again. An idle responsive attachment is detached and its process lease released before reset. A hung or lost worker is terminated with the lease ACTIVE; requiresTiaRestart reports whether TIA must restart before reconnecting. Does not kill TIA; an interrupted native operation can have an unknown outcome.")]
        [AsyncDispatch(nameof(RestartOpennessWorkerV4Async))]
        public static CallToolResult RestartOpennessWorkerV4(
            [Description("confirmRestart: false previews only; true discards the idle/faulted worker and its bindings without replaying any operation.")] bool confirmRestart = false)
            => RestartOpennessWorkerV4Async(confirmRestart).GetAwaiter().GetResult();

        internal static Task<CallToolResult> RestartOpennessWorkerV4Async(bool confirmRestart = false)
            => SessionToolContract.RunAsync("RestartOpennessWorker", confirmRestart, false, () => RestartOpennessWorkerAsync(confirmRestart));

        public static ResponseMessage RestartOpennessWorker(
            [Description("confirmRestart: false previews only; true discards the idle/faulted worker and its bindings without replaying any operation.")] bool confirmRestart = false)
            => RestartOpennessWorkerAsync(confirmRestart).GetAwaiter().GetResult();

        internal static async Task<ResponseMessage> RestartOpennessWorkerAsync(bool confirmRestart = false)
        {
            await BridgeWait.On(InitializeWorkerSupervisorAsync()).ConfigureAwait(false);
            return new ResponseMessage { Message = "Worker reset request evaluated; no TIA project was opened or saved.",
                Meta = await BridgeWait.On(Worker.Restart(confirmRestart, System.Threading.CancellationToken.None)).ConfigureAwait(false) };
        }
        private static Task InitializeWorkerSupervisorAsync()
        {
#if TIA_ENGINE_HOST
            if (TiaMcp.FoundationHost.ReleaseCatalogData.Releases[ReleaseKey].StartSupervisorOnRead
                && (string?)Worker.Snapshot()["state"] == "NotStarted")
                return Worker.StartForStatus(System.Threading.CancellationToken.None);
#endif
            return Task.CompletedTask;
        }
    }
}

#endif
