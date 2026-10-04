using System;
using System.Runtime.CompilerServices;

namespace TiaMcpServer.ModelContextProtocol
{
    internal static partial class InvocationJournal
    {
        static InvocationJournal()
        {
            try { ConnectAdapter(); }
            catch (Exception ex) /* swallow(logging-failure): without the adapter assembly the adapter keeps its own journal file; engine logging and tools must keep working */
            { TiaMcp.Shared.SwallowedExceptions.Note("InvocationJournal.ConnectAdapter", ex); }
        }

        // Separate, non-inlined method: a missing adapter assembly fails when this method is compiled,
        // which the caller's handler can catch.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ConnectAdapter()
            => TiaMcp.Adapters.Diagnostics.AdapterJournal.ConfigureOutput(WriteLine, () => CorrelationId);
    }
}
