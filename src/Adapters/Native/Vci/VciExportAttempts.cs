using System;
using System.Collections.Generic;
using TiaMcp.Adapters.Contracts;

namespace TiaMcp.Adapters.Native.Vci;

// One native export can affect both the mapping and workspace files before it throws.
internal sealed class VciExportAttempts
{
    internal int Issued { get; private set; }
    internal int Unconfirmed { get; private set; }
    internal int MappedAfterFailure { get; private set; }
    internal int UnsupportedContainers { get; private set; }
    internal int QueryRefusals { get; private set; }
    internal string? NativeMessage { get; private set; }

    internal void QueryRefused(Exception error)
    {
        QueryRefusals++;
        NativeMessage ??= Diagnostic(error);
    }

    // No direct format on a container still permits a walk of its children. A null result stops this branch.
    internal IList<string>? ReadFormats(Func<IList<string>?> read, bool descendable, bool ungroupedDevice, Action<Exception> onRefusal)
    {
        try { return read() ?? Array.Empty<string>(); }
        catch (Exception error)
        {
            bool walkChildren = descendable && !ungroupedDevice;
            if (walkChildren) UnsupportedContainers++;
            else QueryRefused(error);
            onRefusal(error);
            return walkChildren ? Array.Empty<string>() : null;
        }
    }

    internal bool Export(Action write, Func<bool> freshMapping, Action<string> report)
    {
        Issued++;
        Unconfirmed++;
        try { write(); }
        catch (Exception error)
        {
            NativeMessage = Diagnostic(error);
            report("FAILED: " + NativeMessage);
            if (freshMapping())
            {
                MappedAfterFailure++; Unconfirmed--;
                report("mapping present after failed export; inspect workspace files");
            }
            return false;
        }
        Unconfirmed--;
        return true;
    }

    private static string Diagnostic(Exception error) =>
        NativeFailureText.Compose(error.GetBaseException().Message, null, Array.Empty<string>());
}
