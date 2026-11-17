using System;

namespace TiaMcpServer.Siemens
{
    public static class DownloadTargetPolicy
    {
        public static string OnlineTargetKind(string? pgPcInterface)
            => pgPcInterface?.IndexOf("PLCSIM", StringComparison.OrdinalIgnoreCase) >= 0 ? "plcsim" : "physical";
    }
}
