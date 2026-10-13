using System;
using TiaMcp.Adapters.Contracts;
namespace TiaMcpServer.Siemens
{
    internal static class UnifiedScriptAccess
    {
        internal static object Resolve(Func<string, object> resolveHmi, string objectPath, string softwarePath, bool collection)
        {
            try { return TiaMcp.Adapters.Native.Hmi.UnifiedPrimitiveBridge.ResolveScripts(resolveHmi, objectPath, softwarePath, collection); }
            catch (PlcSoftwareException error)
            { throw new PortalException((PortalErrorCode)Enum.Parse(typeof(PortalErrorCode), error.Code), error.Message, error.Candidates, error.InnerException); }
        }
    }
}
