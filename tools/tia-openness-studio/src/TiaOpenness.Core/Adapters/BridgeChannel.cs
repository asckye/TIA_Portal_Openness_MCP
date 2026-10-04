using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using TiaMcp.WorkerChannel;
using TiaOpenness.Contracts.Rpc;
using TiaOpenness.Core.Abstractions;

namespace TiaOpenness.Core.Rpc
{
    /// <summary>Adapts Studio DTO payloads to the shared process channel.</summary>
    public static class BridgeChannel
    {
        public static string Hash(string path)
        {
            using (var input = File.OpenRead(path))
            using (var hash = SHA256.Create())
                return BitConverter.ToString(hash.ComputeHash(input)).Replace("-", "").ToLowerInvariant();
        }

        public static string AdapterPath(string bridgeDirectory, string releaseKey, bool mock)
        {
#if TIA_SHARED_ADAPTER_PATHS
            var native = SessionFactoryLoader.AdapterPath(bridgeDirectory, releaseKey);
#else
            var native = Path.Combine(bridgeDirectory, "adapters", "v" + releaseKey, "TiaOpenness.Openness.dll");
#endif
            // Mock and unavailable installations use the factory in Core. Both sides
            // verify that exact deployed assembly, never the desktop's net10 copy.
            return !mock && File.Exists(native) ? native : Path.Combine(bridgeDirectory, "TiaOpenness.Core.dll");
        }

        public static BindingChange BindingChangeFor(string method)
        {
            switch (method)
            {
                case RpcMethods.SessionConnect:
                case RpcMethods.SessionDisconnect:
                case RpcMethods.ProjectOpen:
                case RpcMethods.ProjectClose:
                    return BindingChange.Advance;
                default:
                    return BindingChange.None;
            }
        }

        public static bool IsReadOnly(string method)
        {
            switch (method)
            {
                case RpcMethods.Ping:
                case RpcMethods.DoctorRun:
                case RpcMethods.SessionState:
                case RpcMethods.ProjectInfo:
                case RpcMethods.DeviceList:
                case RpcMethods.BlockList:
                case RpcMethods.TagTableList:
                case RpcMethods.TagList:
                case RpcMethods.InspectProject:
                case RpcMethods.VcSupported:
                case RpcMethods.VcWorkspaceList:
                case RpcMethods.VcStatus:
                case RpcMethods.VcDiff:
                    return true;
                default:
                    return false;
            }
        }
    }

    public sealed class BridgeChannelDispatcher : IDisposable
    {
        private readonly RpcDispatcher dispatcher;
        private long epoch;
        private bool bound;

        public BridgeChannelDispatcher(Func<ITiaSessionFactory> factory)
        {
            dispatcher = new RpcDispatcher(factory, null);
        }

        // This is a managed command revision; observing it never invokes GetState
        // or any Siemens property. Successful binding commands advance exactly once.
        public ChannelBinding Observe() => new ChannelBinding(epoch, bound);

        public ChannelResponse Handle(ChannelRequest request)
        {
            var response = dispatcher.Handle(new RpcRequest
            {
                Id = request.Id.ToString(CultureInfo.InvariantCulture),
                Method = request.Method,
                Params = BridgeJson.Deserialize<JsonElement>(request.ArgumentsJson),
            }, notification =>
            {
                var payload = BridgeJson.Deserialize<ProgressPayload>(notification.Params.Value);
                var percent = payload.Total <= 0 ? 0 : (int)Math.Max(0, Math.Min(100, (long)payload.Current * 100 / payload.Total));
                request.ReportProgress(percent, notification.Params.Value.GetRawText());
            });
            if (response.Error != null)
            {
                var outcome = !dispatcher.BackendEntered ? ChannelOutcome.RejectedBeforeNative :
                    BridgeChannel.IsReadOnly(request.Method) ? ChannelOutcome.ReadFailed : ChannelOutcome.Unknown;
                return ChannelResponse.Error(new ChannelFailure(response.Error.Message, -32603, outcome,
                    rpcErrorJson: BridgeJson.Serialize(response.Error)));
            }
            if (BridgeChannel.BindingChangeFor(request.Method) == BindingChange.Advance) epoch = checked(epoch + 1);
            if (request.Method == RpcMethods.SessionConnect) bound = true;
            if (request.Method == RpcMethods.SessionDisconnect) bound = false;
            return ChannelResponse.Success(response.Result?.GetRawText() ?? "null");
        }

        public void Dispose() => dispatcher.Dispose();
    }
}
