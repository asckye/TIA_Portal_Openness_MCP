#if NET48 && PLC_SAFETY
using System;
using Siemens.Engineering;

namespace TiaMcp.Adapters
{
    // Contract for the engine's borrowed project. Operations and host-specific policies
    // are added in step I; construction never reads, owns or caches the native handle.
    public sealed class PlcServices
    {
        private PlcServices(Func<ProjectBase> project)
        {
            PlcProgram = new ProgramSurface(project);
            PlcData = new DataSurface(project);
        }

        public static PlcServices Over(Func<ProjectBase> project)
            => new PlcServices(project ?? throw new ArgumentNullException(nameof(project)));

        public ProgramSurface PlcProgram { get; }
        public DataSurface PlcData { get; }

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
