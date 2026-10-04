#if NET48 && PLC_SAFETY
using System;
using Siemens.Engineering;

namespace TiaMcp.Adapters
{
    // Borrowed engine project. Construction never reads, owns or caches native handles;
    // VCI shares raw operations while the host retains its policies.
    public sealed class PlcServices
    {
        private PlcServices(Func<ProjectBase> project)
        {
            PlcProgram = new ProgramSurface(project);
            PlcData = new DataSurface(project);
            WatchTechnology = new WatchTechnologySurface(project);
#if STUDIO_VCI_MODERN
            VersionControl = new VersionControlSurface(project);
#endif
        }

        public static PlcServices Over(Func<ProjectBase> project)
            => new PlcServices(project ?? throw new ArgumentNullException(nameof(project)));

        public ProgramSurface PlcProgram { get; }
        public DataSurface PlcData { get; }
        public WatchTechnologySurface WatchTechnology { get; }

        public sealed class WatchTechnologySurface
        {
            private readonly Func<ProjectBase> project;
            internal WatchTechnologySurface(Func<ProjectBase> project) { this.project = project; }
            public ProjectBase CurrentProject => project();
        }
#if STUDIO_VCI_MODERN
        public VersionControlSurface VersionControl { get; }

        // Borrow the engine's handle at the point of use; the engine retains cache lifetime,
        // refusal policy and serialization on its existing MTA thread.
        public sealed class VersionControlSurface
        {
            private readonly Func<ProjectBase> project;
            internal VersionControlSurface(Func<ProjectBase> project) { this.project = project; }
            public ProjectBase CurrentProject => project();
            // Use the exact owner already checked against the engine's service cache.
            public global::Siemens.Engineering.VersionControl.VersionControlInterface Acquire(ProjectBase owner)
                => TiaOpenness.Openness.VersionControlPrimitives.Service(owner as IEngineeringServiceProvider);
        }
#endif

        public sealed class ProgramSurface
        {
            private readonly Func<ProjectBase> project;
            internal ProgramSurface(Func<ProjectBase> project) { this.project = project; }
            internal ProjectBase CurrentProject => project();
        }

        public sealed class DataSurface
        {
            private readonly Func<ProjectBase> project;
            internal DataSurface(Func<ProjectBase> project) { this.project = project; }
            internal ProjectBase CurrentProject => project();
        }
    }
}
#endif
