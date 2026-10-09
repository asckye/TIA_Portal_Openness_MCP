using System;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace TiaMcpServer.ModelContextProtocol
{
    internal interface IHostToolServices
    {
        Task<JsonNode?> Observe(string operation, JsonObject arguments, CancellationToken token);
        Task<JsonNode?> Call(string operation, JsonObject arguments, CancellationToken token);
    }

    // Host tools consume managed observations and adapter operations, never native objects.
    internal static class HostToolServices
    {
        private static readonly AsyncLocal<Func<string, JsonObject, JsonNode?>?> Overrides = new AsyncLocal<Func<string, JsonObject, JsonNode?>?>();
        internal static Func<string, JsonObject, JsonNode?>? Override { get => Overrides.Value; set => Overrides.Value = value; }
        internal static JsonNode? Observe(string operation, JsonObject? arguments = null)
        {
            if (Override != null) return Override(operation, arguments ?? new JsonObject());
#if TIA_ENGINE_HOST
            return ((IHostToolServices)McpServer.Worker).Observe(operation, arguments ?? new JsonObject(), McpServer.WorkerDispatchCancellation).GetAwaiter().GetResult();
#else
            throw new InvalidOperationException("Host observations are unavailable in this process.");
#endif
        }

        internal static (string TempDir, string XmlPath) ExportBlockDocument(string softwarePath, string blockPath)
        {
            if (string.IsNullOrWhiteSpace(softwarePath)) throw new ArgumentException("softwarePath is required for block-path mode.");
            if (string.IsNullOrWhiteSpace(blockPath)) throw new ArgumentException("blockPath is required for block-path mode.");
#if TIA_ENGINE_HOST
            var response = ((IHostToolServices)McpServer.Worker).Call("plc-analysis.ExportBlockDocument", new JsonObject {
                ["softwarePath"] = softwarePath, ["blockPath"] = blockPath
            }, McpServer.WorkerDispatchCancellation).GetAwaiter().GetResult()!.AsObject();
            if (response["Status"] != null) throw new Siemens.PortalException(
                (Siemens.PortalErrorCode)Enum.Parse(typeof(Siemens.PortalErrorCode), (string)response["Status"]!), (string)response["Message"]!);
            return ((string)response["TempDir"]!, (string)response["XmlPath"]!);
#else
            var response = Observe("ExportBlockDocument", new JsonObject { ["softwarePath"] = softwarePath, ["blockPath"] = blockPath })!;
            return ((string)response["TempDir"]!, (string)response["XmlPath"]!);
#endif
        }
    }
}
