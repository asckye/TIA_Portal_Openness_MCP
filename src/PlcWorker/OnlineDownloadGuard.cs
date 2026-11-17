using System;
using System.Text.Json.Nodes;
using TiaMcp.Adapters.Contracts;
using TiaMcpServer.Siemens;

namespace TiaMcp.PlcWorker
{
    internal static class OnlineDownloadGuard
    {
        internal static void Require(OnlineRequest request, bool preview, Func<JsonObject?> instances, Func<string, JsonObject> network)
        {
            if (request.Operation != "DownloadPlc" || preview && request.DryRun || request.ConfirmPhysicalTarget) return;
            if (DownloadTargetPolicy.Classify(request.TargetIpAddress, request.PgPcInterface,
                    instances(), network(request.TargetIpAddress)) != "plcsim")
                throw new AdapterPreconditionException("DownloadPlc changes a real PLC; set confirmPhysicalTarget=true to execute.", null, false);
        }
    }
}
