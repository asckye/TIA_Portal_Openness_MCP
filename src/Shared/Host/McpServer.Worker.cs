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
        [McpServerTool(Name = "GetCallResult"),
            Description("[L0][Diagnostics][READ] Read a running or completed call by requestId from this session. " +
                "A running call returns in-progress. Keeps the latest 16 full completed results. After eviction, summaries without the original payload " +
                "survive only for unread writes and unread unsettled IDs. Reading a summary removes it; later lookups return NOT_FOUND. " +
                "Inner unknown write IDs resolve to their enclosing batch result when retained. Reading a completed result " +
                "or summary acknowledges its related unknown writes and releases their identical-write guards. Inspect targets before repeating writes.")]
        public static CallToolResult GetCallResult([Description("Request ID returned by the original call.")] string requestId)
        {
            if (string.IsNullOrWhiteSpace(requestId)) return V4Reject("GetCallResult", InvalidInput("requestId"));
            return Worker.LateResults.Read(ReleaseKey, requestId, Worker.Unsettled)
                ?? V4Reject("GetCallResult", new TiaMcp.Logic.V4.Error("Call result was not found in this session.",
                    new TiaMcp.Logic.V4.NotFoundDetails(requestId)));
        }

        [McpServerTool(Name = "GetOpennessWorkerStatus"),
                Description("[L0][Diagnostics][READ] Read the local Openness worker supervisor without contacting TIA. Reports process state, generation, " +
                "deadline, queue and fault. Available while the worker is hung or faulted. FoundationHost owns one PLC worker per MCP session; a healthy " +
                "worker is not proof of native TIA stability.")]
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

        [McpServerTool(Name = "RestartOpennessWorker"),
                Description("[L0][Diagnostics][WRITE] Preview or explicitly reset an idle/faulted isolated Openness worker. confirmRestart defaults false. " +
                "Refuses while calls remain active/queued. Resets all worker-held project bindings, exports and preview plans; never saves, attaches or " +
                "replays a write. After confirmation, explicitly connect to the intended project again. An idle responsive attachment is detached and its " +
                "process lease released before reset. A running native call refuses reset with worker-call-running until it finishes. " +
                "A stopped lease holder leaves a record of unfinished calls for the next explicit connection. " +
                "Requests are never replayed.")]
        [AsyncDispatch(nameof(RestartOpennessWorkerV4Async))]
        public static CallToolResult RestartOpennessWorkerV4(
            [Description("confirmRestart: false previews only; true discards the idle/faulted worker and its bindings without replaying any " +
                "operation.")] bool confirmRestart =
                false)
            => RestartOpennessWorkerV4Async(confirmRestart).GetAwaiter().GetResult();

        internal static Task<CallToolResult> RestartOpennessWorkerV4Async(bool confirmRestart = false)
            => SessionToolContract.RunAsync("RestartOpennessWorker", confirmRestart, false, () => RestartOpennessWorkerAsync(confirmRestart));

        public static ResponseMessage RestartOpennessWorker(
            [Description("confirmRestart: false previews only; true discards the idle/faulted worker and its bindings without replaying any " +
                "operation.")] bool confirmRestart =
                false)
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
