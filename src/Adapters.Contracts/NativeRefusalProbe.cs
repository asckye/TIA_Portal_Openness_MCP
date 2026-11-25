using System;
using System.Collections.Generic;
using System.Linq;

namespace TiaMcp.Adapters.Contracts
{
    public static class NativeRefusalProbe
    {
        // Fingerprints contain detached scalar values only. A missing snapshot cannot prove a refusal.
        public static void Run(IDictionary<string, object?> meta, Func<object?[]?> fingerprint, Action call)
            => Run(meta, fingerprint, call, null);

        public static void Run(IDictionary<string, object?> meta, Func<object?[]?> fingerprint, Action call, Func<Exception, bool>? canProveRefusal)
        {
            object?[]? before = null;
            try { before = fingerprint(); }
            catch (Exception error)
            {
                if (UncertainConnection(error)) { meta["connectionUnavailable"] = true; throw; }
                // An ordinary unavailable snapshot leaves the native operation eligible, without proof on failure.
            }

            meta["mayHaveChanged"] = true;
            try { call(); }
            catch (Exception error)
            {
                bool unchanged = false;
                if (UncertainConnection(error)) meta["connectionUnavailable"] = true;
                else if (before != null && (canProveRefusal == null || canProveRefusal(error)))
                {
                    try { unchanged = fingerprint() is { } after && before.SequenceEqual(after); }
                    catch (Exception readError)
                    {
                        if (UncertainConnection(readError)) meta["connectionUnavailable"] = true;
                    }
                }
                if (unchanged)
                {
                    meta["mayHaveChanged"] = false;
                    meta["apiCallSuccess"] = false;
                    meta["foundationKnownNoMutation"] = true;
                    error.Data["foundationKnownNoMutation"] = true;
                }
                else meta["writeOutcomeUnknown"] = true;
                throw;
            }
        }

        public static bool UncertainConnection(Exception error)
        {
            if (HmiReadPolicy.ConnectionUnavailable(error)) return true;
            for (Exception? cause = error; cause != null; cause = cause.InnerException)
            {
                if (cause is TimeoutException or OperationCanceledException or System.IO.EndOfStreamException or System.Net.Sockets.SocketException)
                    return true;
                for (var type = cause.GetType(); type != null; type = type.BaseType)
                    if (type.FullName == "System.ServiceModel.CommunicationException") return true;
            }
            return false;
        }
    }
}
