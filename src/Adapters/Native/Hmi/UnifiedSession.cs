#if HMI_UNIFIED
using System;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Native.Plc;

namespace TiaMcp.Adapters.Native.Hmi
{
    internal sealed class UnifiedSession : IUnifiedSession
    {
        private readonly PlcFoundationEngine engine;
        private readonly HmiSession hmi;
        internal UnifiedSession(PlcFoundationEngine engine)
        {
            this.engine = engine;
            hmi = new HmiSession(engine, engine.UnifiedPlcSession, new HmiRequest());
        }
        public object? CurrentProject => engine.UnifiedPlcSession.CurrentProject;
        public object ResolveHmiSoftwareOrThrow(string path) => hmi.ResolveHmiSoftwareOrThrow(path);
        public object ExactUnifiedRoot(string path) => hmi.ExactUnifiedRoot(path);
        public object ExactPlantViews()
        {
            var project = engine.UnifiedPlcSession.CurrentProject!;
#if HMI_CPM_PROVIDER
            return project.GetService<Siemens.Engineering.HmiUnified.Cpm.PlantViewsProvider>()?.PlantViews
                ?? throw new NotSupportedException("Project does not expose PlantViewsProvider; check installed Unified/CPM capability.");
#else
            return project.PlantViews;
#endif
        }
        public IDisposable AcquireHmiEditAccess() => hmi.AcquireHmiEditAccess();
        public HmiMessage RunHmiStepTool(string tool, Func<HmiObject, string> action, bool requiresProject = true)
            => hmi.RunHmiStepTool(tool, action, requiresProject);
        public void RecordHmiReadFault(HmiObject meta) => hmi.RecordHmiReadFault(meta);
        public object? HmiReadFault => engine.UnifiedReadFault;
        public MigrationPages MigrationPages => engine.UnifiedPages;
        public string UnifiedCollection(string category) => category switch
        {
            "alarmClasses" => "AlarmClasses", "discreteAlarms" => "DiscreteAlarms", "analogAlarms" => "AnalogAlarms",
            "alarmLogs" => "AlarmLogs", "dataLogs" => "DataLogs", "textLists" => "HmiTextLists", "graphicLists" => "HmiGraphicLists",
            "systemTags" => "SystemTags", "systemTextLists" => "HmiSystemTextLists", "auditTrails" => "AuditTrails", "opcUaAlarmTypes" => "OpcUaAlarmTypes",
            _ => throw new ArgumentException("Unknown Unified collection category.")
        };
    }
}
#endif
