using System;
using TiaMcp.Adapters.Contracts;

namespace TiaMcp.PlcWorker
{
    internal static class OnlineDownloadGuard
    {
        internal static void Require(OnlineRequest request, bool preview, Func<string, bool> isSimulator)
        {
            if (request.Operation != "DownloadPlc" || preview && request.DryRun || request.ConfirmPhysicalTarget) return;
            if (!isSimulator(request.TargetIpAddress))
                throw new AdapterPreconditionException("DownloadPlc changes a real PLC; set confirmPhysicalTarget=true to execute.", null, false);
        }
    }
}
