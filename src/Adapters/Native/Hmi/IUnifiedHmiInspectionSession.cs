using System;

namespace TiaMcp.Adapters.Native.Hmi
{
    internal interface IUnifiedHmiInspectionSession : IHmiInspectionSession
    {
        public IDisposable AcquireHmiEditAccess();
    }
}
