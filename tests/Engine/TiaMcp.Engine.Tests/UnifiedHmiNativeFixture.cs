using TiaMcp.Adapters.Contracts;
namespace TiaMcp.Adapters.Native.Hmi
{
    internal static class HmiEventAccess
    {
        internal static object Resolve(object handlers, string type, bool createIfMissing = false)
        {
            try { return TiaMcpServer.Siemens.HmiEventAccess.Resolve(handlers, type, createIfMissing); }
            catch (TiaMcpServer.Siemens.PortalException error) { throw new PlcSoftwareException(error.Code.ToString(), error.Message); }
        }
    }
}
