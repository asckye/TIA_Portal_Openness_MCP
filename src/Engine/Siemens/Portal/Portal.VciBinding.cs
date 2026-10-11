using System;
using TiaMcp.Adapters.Contracts.Candidates;

namespace TiaMcpServer.Siemens;

public partial class Portal
{
    internal Func<FallbackRequest, IFallbackAdapter>? VersionControlFallback { get; set; }
}
