using System;
using System.Threading;
using TiaMcpServer.ModelContextProtocol;
using TiaMcpServer.Siemens;

namespace TiaMcpServer.Worker
{
    // One owner-thread boundary for both directions of session ownership transfer.
    // Synchronization copies handles and cached identity; it never selects a project.
    internal sealed class SharedSessionLifecycle
    {
        private readonly int owner = Thread.CurrentThread.ManagedThreadId;
        private readonly Action fromEngine, fromFoundation;
        private readonly Action<string> invalidate;
        internal Action<int, long, PortalProcessLease>? AttachProcess;
        private int depth;
        internal bool Executing { get { RequireOwner(); return depth != 0; } }
        internal string? Fault { get; private set; }

        internal SharedSessionLifecycle(Action fromEngine, Action fromFoundation, Action<string> invalidate)
        {
            this.fromEngine = fromEngine; this.fromFoundation = fromFoundation; this.invalidate = invalidate;
            RequireOwner();
        }

        private void RequireOwner()
        {
            if (Thread.CurrentThread.ManagedThreadId != owner || Thread.CurrentThread.GetApartmentState() != ApartmentState.MTA)
                throw new InvalidOperationException("Shared lifecycle operations require the worker's MTA owner thread.");
        }

        internal void Lock(string reason)
        {
            RequireOwner();
            if (Fault != null) return;
            Fault = reason;
            invalidate(reason);
        }

        internal T Engine<T>(Func<T> operation) => Execute(operation, fromEngine);
        internal T Foundation<T>(Func<T> operation) => Execute(operation, fromFoundation);
        internal T Disconnect<T>(Func<T> operation)
        {
            RequireOwner();
            if (depth != 0) throw new InvalidOperationException("Disconnect requires an idle native lane.");
            depth++;
            using var native = InvocationJournal.BeginNativeCallScope();
            try
            {
                var result = operation();
                fromFoundation();
                return result;
            }
            catch (Exception error) { Lock(error.Message); throw; }
            finally { depth--; }
        }

        private T Execute<T>(Func<T> operation, Action synchronize)
        {
            RequireOwner();
            if (Fault != null) throw new InvalidOperationException(TiaOpenness.Shared.SessionBehavior.Recovery);
            if (depth != 0) return operation();
            depth++;
            using var native = InvocationJournal.BeginNativeCallScope();
            try
            {
                var result = operation();
                if (Fault == null)
                {
                    try { synchronize(); }
                    catch (Exception error) { Lock(error.Message); throw; }
                }
                return result;
            }
            catch (Exception error)
            {
                if (native.NativeCallIssued) Lock(error.Message);
                throw;
            }
            finally { depth--; }
        }
    }
}
