using System;
using System.Collections.Generic;
using System.Linq;

namespace TiaMcp.Adapters.Contracts
{
    public static class NativeRefusalProbe
    {
        public static bool DisposedTarget<T>(T target, Exception error) where T : class
        {
            var type = target.GetType();
            if (error.InnerException != null && UncertainConnection(error.InnerException)) return false;
            if (error is ObjectDisposedException disposed)
                return disposed.ObjectName == typeof(T).Name || disposed.ObjectName == typeof(T).FullName
                    || disposed.ObjectName == type.Name || disposed.ObjectName == type.FullName;
            if (!HmiReadPolicy.DisposedObjectOnly(error)) return false;
            return new[] { typeof(T).FullName, type.FullName, type.Name, typeof(T).Name }
                .Any(name => error.Message.IndexOf("disposed object of type '" + name + "'", StringComparison.OrdinalIgnoreCase) >= 0);
        }

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

        // For callers that already captured the exact target immediately before the write.
        public static bool ProvesUnchanged(Exception failure, Func<bool> readback)
        {
            if (!AllowsRefusalProof(failure)) return false;
            try { return readback(); }
            catch (Exception) /* swallow(probe-optional): preserve the original write failure when its readback is unavailable. */
            { return false; }
        }

        internal static bool ProvesAbsent(Exception failure, string? name, Func<string, bool> exists)
            => !string.IsNullOrWhiteSpace(name) && name == name!.Trim() && ProvesUnchanged(failure, () => !exists(name));

        public static bool AllowsRefusalProof(Exception failure)
        {
            if (UncertainConnection(failure)) return false;
            for (Exception? cause = failure; cause != null; cause = cause.InnerException)
            {
                if (cause is ObjectDisposedException) return false;
                // These Siemens types can be subclassed. The contracts assembly has no SDK reference.
                for (Type? type = cause.GetType(); type != null; type = type.BaseType)
                    if (type.Name is "EngineeringUserAbortException" or "EngineeringObjectDisposedException" or "NonRecoverableException")
                        return false;
            }
            return true;
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
