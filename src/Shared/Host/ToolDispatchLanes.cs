using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcpServer.Dispatch
{
    internal static class ToolDispatchLanes
    {
#if TIA_ENGINE_HOST
        internal interface IWorkerLane { public void Activate(); }
#endif
        private static readonly ConditionalWeakTable<object, SemaphoreSlim> Sessions = new ConditionalWeakTable<object, SemaphoreSlim>();
        private static readonly object UnavailableSession = new object();
        private static readonly SemaphoreSlim Local = new SemaphoreSlim(8, 8);
        private static readonly AsyncLocal<Lease?> Held = new AsyncLocal<Lease?>();

        internal static Task<IDisposable?> Acquire(string name, CancellationToken token)
            => AcquireForSession(name, ToolTaxonomy.UsesOpennessLane(name) && !ToolTaxonomy.DispatchesTargets(name) ? SessionKey() : UnavailableSession, token);

        internal static async Task<IDisposable?> AcquireForSession(string name, object session, CancellationToken token)
        {
            // Orchestration waits for approval and takes each target's lane itself.
            if (ToolTaxonomy.DispatchesTargets(name)) return null;
#if TIA_ENGINE_HOST
            var execution = McpServer.CatalogView.Find(name, true)?.Execution;
            // Ported Foundation tools share the same worker and native journal lane.
            bool workerTool = execution == "worker" || execution == "foundation"
                && TiaMcp.Adapters.Contracts.PortedFamilies.IsMigratedDeclaration(name)
                && (ToolTaxonomy.MayCallOpenness(name) || TiaMcp.Adapters.Contracts.PortedFamilies.ForTool(name).Name == "F04");
            var gate = workerTool && ToolTaxonomy.UsesOpennessLane(name) ? Sessions.GetValue(session, _ => new SemaphoreSlim(1, 1)) : Local;
#else
            var gate = ToolTaxonomy.UsesOpennessLane(name) ? Sessions.GetValue(session, _ => new SemaphoreSlim(1, 1)) : Local;
#endif
            for (var held = Held.Value; held != null; held = held.previous)
                if (ReferenceEquals(held.gate, gate)) return null;
#if TIA_ENGINE_HOST
            if (!ReferenceEquals(gate, Local)) return new Lease(gate, await McpServer.Worker.Acquire(token).ConfigureAwait(false));
#endif
            await gate.WaitAsync(token).ConfigureAwait(false);
            return new Lease(gate);
        }

        internal static IDisposable? Enter(string name, CancellationToken token = default)
        {
            var lease = Acquire(name, token).GetAwaiter().GetResult();
            Activate(lease);
            return lease;
        }
        // AsyncLocal changes in an awaited callee do not flow back into its caller.
        internal static void Activate(IDisposable? lease) { if (lease is Lease active) active.Activate(); }

#if TIA_ENGINE_HOST
        private static object SessionKey() => McpServer.Worker.SessionKey;
#else
        private static object SessionKey() => Runtime.OpennessReadiness.Ready ? ReadySessionKey() : UnavailableSession;
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static object ReadySessionKey() => EngineServices.Provider.GetService(typeof(Siemens.IEngineeringSession)) ?? UnavailableSession;

#endif

        private sealed class Lease : IDisposable
        {
            internal readonly SemaphoreSlim gate;
#if TIA_ENGINE_HOST
            private readonly IDisposable? workerLane;
            internal Lease(SemaphoreSlim gate, IDisposable workerLane) { this.gate = gate; this.workerLane = workerLane; }
#endif
            internal Lease? previous;
            private bool disposed;
            internal Lease(SemaphoreSlim gate) { this.gate = gate; }
            internal void Activate()
            {
                previous = Held.Value; Held.Value = this;
#if TIA_ENGINE_HOST
                if (workerLane is IWorkerLane lane) lane.Activate();
#endif
            }
            public void Dispose()
            {
                if (disposed) return;
                disposed = true;
                Held.Value = previous;
#if TIA_ENGINE_HOST
                if (workerLane != null) { workerLane.Dispose(); return; }
#endif
                gate.Release();
            }
        }
    }
}
