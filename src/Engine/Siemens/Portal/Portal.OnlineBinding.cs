using System;
using TiaMcp.Adapters.Contracts.Candidates;

namespace TiaMcpServer.Siemens;

public partial class Portal
{
    internal Func<FallbackRequest, string, IFallbackAdapter>? OnlineFallback { get; set; }
    internal Func<string, string>? OnlineFallbackOfflineState { get; set; }
}
