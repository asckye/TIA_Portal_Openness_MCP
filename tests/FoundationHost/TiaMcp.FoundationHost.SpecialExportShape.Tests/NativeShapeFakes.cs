// Test doubles only. Compiles the actual traversal with distinct system/user types.
// This is not Siemens SDK compatibility, export-policy validation or native acceptance.
namespace Siemens.Engineering
{
    public enum ExportOptions { None }
    public enum ImportOptions { None }
    public interface IEngineeringObject
    {
        object GetAttribute(string name);
        void SetAttribute(string name, object value);
    }
}
namespace Siemens.Engineering.SW.WatchAndForceTables
{
    public abstract class PlcWatchAndForceTableGroup
    {
        public string Name { get; set; } = "";
        public PlcWatchTableComposition WatchTables { get; } = new();
        public PlcWatchAndForceTableUserGroupComposition Groups { get; } = new();
    }
    public sealed class PlcWatchAndForceTableSystemGroup : PlcWatchAndForceTableGroup { }
    public sealed class PlcWatchAndForceTableUserGroup : PlcWatchAndForceTableGroup { }
    public sealed class PlcWatchTableComposition : List<PlcWatchTable>
    {
        public PlcWatchTable? Find(string name) => this.SingleOrDefault(table => table.Name == name);
        public IList<PlcWatchTable> Import(FileInfo file, Siemens.Engineering.ImportOptions options) => Array.Empty<PlcWatchTable>();
    }
    public sealed class PlcTableCommentEntryComposition : List<PlcTableCommentEntry>
    {
        public PlcTableCommentEntry Create() { var entry = new PlcTableCommentEntry(); Add(entry); return entry; }
    }
    public sealed class PlcTableCommentEntry { public void Delete() { } }
    public sealed class PlcForceTable
    {
        public string Name { get; set; } = "";
        public bool IsConsistent { get; set; } = true;
        public PlcTableCommentEntryComposition Entries { get; } = new();
    }
    public sealed class PlcWatchAndForceTableUserGroupComposition : List<PlcWatchAndForceTableUserGroup> { }
    public sealed class PlcWatchTable
    {
        public string Name { get; set; } = "";
        public bool IsConsistent { get; set; } = true;
        public PlcTableCommentEntryComposition Entries { get; } = new();
        public void Delete() { }
        public int ExportCalls { get; private set; }
        public void Export(FileInfo file, Siemens.Engineering.ExportOptions options) { ExportCalls++; }
    }
}
namespace Siemens.Engineering.SW.TechnologicalObjects
{
    public sealed class TechnologicalInstanceDBGroup
    {
        public string Name { get; set; } = "";
        public TechnologicalInstanceDBComposition TechnologicalObjects { get; } = new();
    }
    public sealed class TechnologicalInstanceDBComposition : List<TechnologicalInstanceDB>
    {
        public TechnologicalInstanceDB Create(string name, string typeIdentifier, Version version) { var db = new TechnologicalInstanceDB { Name = name }; Add(db); return db; }
        public TechnologicalInstanceDB? Find(string name) => this.SingleOrDefault(db => db.Name == name);
    }
    public sealed class TechnologicalParameter { }
    public sealed class TechnologicalParameterComposition : List<TechnologicalParameter>
    {
        public TechnologicalParameter? Find(string name) => null;
    }
    public sealed class TechnologicalInstanceDB
    {
        public string Name { get; set; } = "";
        public TechnologicalParameterComposition Parameters { get; } = new();
        public bool IsKnowHowProtected { get; set; }
        public bool IsConsistent { get; set; } = true;
    }
}
namespace Siemens.Engineering.SW
{
    public sealed class PlcSoftware
    {
        public Siemens.Engineering.SW.WatchAndForceTables.PlcWatchAndForceTableSystemGroup WatchAndForceTableGroup { get; } = new();
        public Siemens.Engineering.SW.TechnologicalObjects.TechnologicalInstanceDBGroup TechnologicalObjectGroup { get; } = new();
    }
}
namespace TiaMcp.Adapters
{
    public sealed partial class PlcFoundationEngine
    {
        internal Siemens.Engineering.SW.PlcSoftware Software { get; } = new();
        private string ReleaseKey => "20";
        // Mirrors the shared selection rule: the exact path or the PLC's unique short name; anything else is refused.
        private (Siemens.Engineering.SW.PlcSoftware Value, string ExactPath) ReadSelection(string path) =>
            path is "devices/D/CPU" or "CPU" ? (Software, "devices/D/CPU") : throw new ArgumentException("Software path not found.", "softwarePath");
        private readonly FakeLifecycle lifecycle = new();
        private sealed class FakeLifecycle { public bool IsLocalSession => true; }
        private sealed class FakeProject { public FileInfo Path => new("project.ap20"); }
        private FakeProject Project() => new();
        private void RequireTargetOffline((Siemens.Engineering.SW.PlcSoftware Value, string ExactPath) selected) { }
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
