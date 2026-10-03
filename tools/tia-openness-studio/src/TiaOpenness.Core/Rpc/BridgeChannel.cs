using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TiaMcp.WorkerChannel;
using TiaOpenness.Contracts.Rpc;
using TiaOpenness.Core.Abstractions;

namespace TiaOpenness.Core.Rpc
{
    /// <summary>Studio payloads stay on their existing Newtonsoft boundary.</summary>
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
            var native = Path.Combine(bridgeDirectory, "adapters", "v" + releaseKey, "TiaOpenness.Openness.dll");
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
                Params = JsonConvert.DeserializeObject<JObject>(request.ArgumentsJson, BridgeJson.Settings),
            }, notification =>
            {
                var payload = notification.Params.ToObject<ProgressPayload>(JsonSerializer.Create(BridgeJson.Settings));
                var percent = payload.Total <= 0 ? 0 : (int)Math.Max(0, Math.Min(100, (long)payload.Current * 100 / payload.Total));
                request.ReportProgress(percent, JsonConvert.SerializeObject(notification.Params, BridgeJson.Settings));
            });
            if (response.Error != null)
            {
                var outcome = !dispatcher.BackendEntered ? ChannelOutcome.RejectedBeforeNative :
                    BridgeChannel.IsReadOnly(request.Method) ? ChannelOutcome.ReadFailed : ChannelOutcome.Unknown;
                return ChannelResponse.Error(new ChannelFailure(response.Error.Message, -32603, outcome,
                    rpcErrorJson: JsonConvert.SerializeObject(response.Error, BridgeJson.Settings)));
            }
            if (BridgeChannel.BindingChangeFor(request.Method) == BindingChange.Advance) epoch = checked(epoch + 1);
            if (request.Method == RpcMethods.SessionConnect) bound = true;
            if (request.Method == RpcMethods.SessionDisconnect) bound = false;
            return ChannelResponse.Success(JsonConvert.SerializeObject(response.Result, BridgeJson.Settings));
        }

        public void Dispose() => dispatcher.Dispose();
    }
}
