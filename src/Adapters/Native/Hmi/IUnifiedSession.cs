using System;
using TiaMcp.Adapters.Contracts;
namespace TiaMcp.Adapters.Native.Hmi
{
    internal interface IUnifiedSession
    {
        public object? CurrentProject { get; }
        public object? HmiReadFault { get; }
        public MigrationPages MigrationPages { get; }
        public object ExactUnifiedRoot(string path);
        public object ExactPlantViews();
        public object ResolveHmiSoftwareOrThrow(string path);
        public string UnifiedCollection(string category);
        public IDisposable AcquireHmiEditAccess();
        public void RecordHmiReadFault(HmiObject meta);
        public HmiMessage RunHmiStepTool(string tool, Func<HmiObject, string> action, bool requiresProject = true);
    }
}
