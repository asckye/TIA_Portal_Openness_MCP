// Test doubles only. Compiles the actual traversal with distinct system/user types.
// This is not Siemens SDK compatibility, export-policy validation or native acceptance.
namespace Siemens.Engineering { public enum ExportOptions { None } }
namespace Siemens.Engineering.SW.WatchAndForceTables
{
    public abstract class PlcWatchAndForceTableGroup
    {
        public string Name { get; set; } = "";
        public List<PlcWatchTable> WatchTables { get; } = new();
        public List<PlcWatchAndForceTableUserGroup> Groups { get; } = new();
    }
    public sealed class PlcWatchAndForceTableSystemGroup : PlcWatchAndForceTableGroup { }
    public sealed class PlcWatchAndForceTableUserGroup : PlcWatchAndForceTableGroup { }
    public sealed class PlcWatchTable
    {
        public string Name { get; set; } = "";
        public bool IsConsistent { get; set; } = true;
        public int ExportCalls { get; private set; }
        public void Export(FileInfo file, Siemens.Engineering.ExportOptions options) { ExportCalls++; }
    }
}
namespace Siemens.Engineering.SW.TechnologicalObjects
{
    public sealed class TechnologicalInstanceDBGroup
    {
        public string Name { get; set; } = "";
        public List<TechnologicalInstanceDB> TechnologicalObjects { get; } = new();
    }
    public sealed class TechnologicalInstanceDB
    {
        public string Name { get; set; } = "";
        public bool IsKnowHowProtected { get; set; }
        public bool IsConsistent { get; set; } = true;
    }
}
namespace TiaMcp.PlcFoundation
{
    internal sealed class FakeSoftware
    {
        public Siemens.Engineering.SW.WatchAndForceTables.PlcWatchAndForceTableSystemGroup WatchAndForceTableGroup { get; } = new();
        public Siemens.Engineering.SW.TechnologicalObjects.TechnologicalInstanceDBGroup TechnologicalObjectGroup { get; } = new();
    }
    public sealed partial class PlcFoundationEngine
    {
        internal FakeSoftware Software { get; } = new();
        private string ReleaseKey => "20";
        private (FakeSoftware Value, string ExactPath) ReadSelection(string path) => (Software, "devices/D/CPU");
        private readonly FakeLifecycle lifecycle = new();
        private sealed class FakeLifecycle { public bool IsLocalSession => true; }
        private sealed class FakeProject { public FileInfo Path => new("project.ap20"); }
        private FakeProject Project() => new();
        private void RequireTargetOffline((FakeSoftware Value, string ExactPath) selected) { }
        private static PlcTechnologyReadRow TechnologyMetadata(Siemens.Engineering.SW.TechnologicalObjects.TechnologicalInstanceDB item) => new() { Name=item.Name };
    }
    internal static class PlcLifecyclePolicy
    {
        internal static void RequireLocalSessionExecution(bool local, bool dryRun) { if(!local) throw new InvalidOperationException(); }
    }
    internal static class PlcSpecialExportPolicy
    {
        // Stub only: policy behavior has its own tests. Route to the selected fake table.
        internal static PlcSpecialExportResult Run(string kind,string release,string project,string software,string name,string output,bool consistent,bool dryRun,string hash,Action offline,Action<FileInfo> export)
        {
            if(!dryRun) { offline(); export(new FileInfo(output)); }
            return new();
        }
    }
}
