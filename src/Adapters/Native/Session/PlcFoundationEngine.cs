using System.Xml.Linq;
using System;
using TiaMcp.Adapters.Native.Plc;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using Siemens.Engineering;
using Siemens.Engineering.Compiler;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.Tags;
using Siemens.Engineering.SW.Types;
using TiaMcpServer.Siemens;
using TiaMcp.Adapters.Contracts;
#if PROJECT_BASE
using EngineeringProject = Siemens.Engineering.ProjectBase;
#else
using EngineeringProject = Siemens.Engineering.Project;
#endif

namespace TiaMcp.Adapters
{
    /// <summary>
    /// Shared typed PLC operations for the selected release.
    /// Native acceptance is tracked in docs/reference/real-machine-ledger.md.
    /// The host supplies exact assembly resolution and one release-specific owner thread.
    /// </summary>
    public sealed partial class PlcFoundationEngine : IDisposable
    {
        private readonly int ownerThread;
        private TiaPortal? portal;
        private EngineeringProject? project;
        private readonly PlcLifecycleState lifecycle=new PlcLifecycleState();
#if PROJECT_MULTIUSER
        private Siemens.Engineering.Multiuser.LocalSession? localSession;
#endif
        private bool disposed;
        private readonly PlcDisconnectState disconnect = new PlcDisconnectState();
        private bool? ownsPortal;
        public string ReleaseKey { get; }

        public PlcFoundationEngine(string releaseKey, string selectedPublicApiDirectory)
        {
            var compiled = typeof(PlcFoundationEngine).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
                .Single(a => a.Key == "TiaReleaseKey").Value!;
            PlcFoundationPolicy.RequireRelease(compiled, releaseKey);
            if (Thread.CurrentThread.GetApartmentState() != PlcLifecyclePolicy.OwnerApartment(releaseKey))
                throw new InvalidOperationException(releaseKey == "20" || releaseKey == "21"
                    ? "PLC operations require the worker's owning MTA thread." : "PLC operations require an owning STA thread.");
            var contract = OpennessReleaseContract.For(releaseKey);
            contract = OpennessReleaseContract.For(releaseKey, contract.CoreAssemblyIdentity, selectedPublicApiDirectory);
            contract.RequireAssembly(typeof(TiaPortal).Assembly);
            if (releaseKey == "21")
            {
                var expected = new AssemblyName("Siemens.Engineering.Step7, Version=21.0.0.0, Culture=neutral, PublicKeyToken=29bfe5fdf4ba5d3b");
                var actual = typeof(PlcSoftware).Assembly;
                EngineeringAssemblyIdentity.RequireMatch(expected, actual.GetName(), actual.Location);
                if (!string
                        .Equals(Path.GetDirectoryName(actual.Location), Path.GetFullPath(selectedPublicApiDirectory).TrimEnd('\\', '/'), StringComparison
                            .OrdinalIgnoreCase))
                    throw new FileLoadException("Step7 must come from the selected V21 API directory.", actual.Location);
            }
            ReleaseKey = releaseKey;
            ownerThread = Thread.CurrentThread.ManagedThreadId;
        }

        private void Check(bool allowDisconnected=false)
        {
            if (disposed) throw new ObjectDisposedException(nameof(PlcFoundationEngine));
            if (!allowDisconnected) disconnect.RequireActive();
            PlcLifecyclePolicy.RequireOwner(ReleaseKey, ownerThread);
        }
        public bool CanDetachOnUncertainShutdown => portal == null || ownsPortal == false && !workerStartedPortal;
        private bool uncertainShutdown;
        public void InvalidateUncertainShutdown()
        {
            // Set the guard before managed invalidation: even a failing callback
            // must never let Dispose close an owned portal with an unknown write.
            uncertainShutdown = true;
            if (ownsPortal == false) InvalidateSharedSession();
            else
            {
                Check(true);
                _resetWorkerFamilyState?.Invoke();
                // Keep owned/unknown-lifetime native handles alive until worker exit.
                // Their disposal could close the TIA instance whose write is uncertain.
            }
        }

        public void InvalidateSharedSession()
        {
            Check(true);
            _resetWorkerFamilyState?.Invoke();
            portal=null; project=null; lifecycle.Detached();
#if PROJECT_MULTIUSER
            localSession=null;
#endif
        }
        private TiaPortal Portal() {
            Check(); disconnect.RequireActive(); RequirePortalAlive(); return portal
                ?? throw new InvalidOperationException("Attach to an explicitly selected TIA process first.");
        }
        private EngineeringProject Project() {
            Portal(); lifecycle.RequireBound(); return project ?? throw new InvalidOperationException("Bind, open or create an explicit project first."); }

        private void RequireHardwareCatalogBinding()
        {
            Check();
            if (portal == null || project == null)
                throw new AdapterPreconditionException("Hardware catalog tools require ConnectPortal followed by AttachOpenProject first.", "session", false);
        }

        // No process launch, automatic process selection, kill, project upgrade or UI interaction.
        public PlcConnectionResult Attach(int processId)
        {
            Check();
            disconnect.RequireActive();
            lifecycle.RequireAttach(processId);
            var process = FindProcess(processId);
            portal = OpennessRefusalMapping.Run("attach", false, () => process.Attach());
            ObservePortal(portal);
            lifecycle.Attached(processId);
            ownsPortal=false; workerStartedPortal=false;
            return new PlcConnectionResult { AttemptedPids=new[]{processId} };
        }
        public PlcConnectionResult AttachScoped(int processId, string recordedSecondaryProjectFile = "", int recordedSecondaryProcessId = 0,
            long recordedSecondaryProcessStartTicks = 0)
        {
            Check();
            string? reference = ValidateSecondaryRecovery(recordedSecondaryProjectFile, recordedSecondaryProcessId, recordedSecondaryProcessStartTicks);
            var result = Attach(processId);
            RestoreSecondaryRecord(reference, recordedSecondaryProcessId, recordedSecondaryProcessStartTicks);
            return result;
        }
        public void RequireProjectIdentity(string expectedProjectFile)
        {
            var current=Project();
            PlcLifecyclePolicy.RequireLocalSessionExecution(lifecycle.IsLocalSession,false);
            MutationIdentityPolicy.RequireSameProject(expectedProjectFile,current.Path.FullName);
        }
        public void UnbindProject() { Check(); lifecycle.Unbound(); project = null;
#if PROJECT_MULTIUSER
            localSession=null;
#endif
        }

        // A portal this worker started is disposed (closing a headless TIA Portal) only when no project is open in it;
        // an attached portal is only detached. The refusal comes before any native call and keeps the session usable.
        public PlcDisconnectResult Disconnect() => DisconnectCore(true);
        private PlcDisconnectResult DisconnectCore(bool userRequested)
        {
            Check(true);
            if (userRequested) RequireSecondaryClosed("DisconnectPortal");
            bool started = ownsPortal==true && workerStartedPortal && lifecycle.ProcessId.HasValue && disconnect.Result==null;
            var result=disconnect.Execute(lifecycle.ProcessId,ownsPortal,()=>{ ReleasePortalEvents(); portal!.Dispose(); },false,workerStartedPortal,
                started && OwnedPortalHasOpenProjects());
            _resetWorkerFamilyState?.Invoke();
            portal=null; project=null; lifecycle.Detached();
#if PROJECT_MULTIUSER
            localSession=null;
#endif
            return result;
        }
        public bool DisconnectAttempted => disconnect.Attempted;
        // Never throws: worker shutdown must finish. Never retries an attempted detach, including one whose acknowledgement
        // was lost; an owned portal is disposed once.
        public void Dispose()
        {
            if (disposed) return;
            try
            {
                PlcLifecyclePolicy.RequireOwner(ReleaseKey, ownerThread);
                if (uncertainShutdown) return;
                try { if (!disconnect.Attempted) DisconnectCore(false); }
                // Shutdown with a project still open in a worker-started portal: dispose it once anyway.
                catch (AdapterPreconditionException refused) when (!disconnect.Attempted)
                {
                    Console.Error.WriteLine("PlcWorker shutdown: " + refused.Message + " Disposing the TIA Portal this worker started once.");
                    ShutdownPortal();
                }
            }
            catch (Exception error) /* swallow(teardown): shutdown continues; the TIA Portal state needs inspection and the failure is reported on stderr */
            { Console.Error.WriteLine("PlcWorker shutdown could not end the TIA Portal connection: " + error.GetBaseException().Message); }
            finally { disposed=true; }
        }
    }
}
