using System;
namespace TiaMcpServer.Siemens
{
    internal static class MigrationRead
    {
        internal static Exception Cause(Exception error)
            => TiaMcp.Adapters.Native.Hmi.UnifiedPrimitiveBridge.Cause(error);
    }
}
