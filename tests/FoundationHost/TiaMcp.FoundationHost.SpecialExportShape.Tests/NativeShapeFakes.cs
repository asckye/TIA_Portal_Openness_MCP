// Test doubles only. Compiles the actual traversal with distinct system/user types.
// This is not Siemens SDK compatibility, export-policy validation or native acceptance.
namespace Siemens.Engineering
{
    public enum ExportOptions { None, WithDefaults }
    public enum ImportOptions { None, Override }
    public interface IEngineeringObject
    {
        public object GetAttribute(string name);
        public void SetAttribute(string name, object value);
    }
}
namespace Siemens.Engineering.SW.WatchAndForceTables
{
    public abstract class PlcWatchAndForceTableGroup
    {
        public string Name { get; set; } = "";
        public PlcWatchTableComposition StoredWatchTables { get; } = new();
        public Func<PlcWatchTableComposition>? ReadWatchTables { get; set; }
        public int WatchReads { get; private set; }
        public PlcWatchTableComposition WatchTables { get { WatchReads++; return ReadWatchTables?.Invoke() ?? StoredWatchTables; } }
        public PlcForceTableComposition ForceTables { get; } = new();
        public PlcWatchAndForceTableUserGroupComposition Groups { get; } = new();
    }
    public sealed class PlcWatchAndForceTableSystemGroup : PlcWatchAndForceTableGroup { }
    public sealed class PlcWatchAndForceTableUserGroup : PlcWatchAndForceTableGroup { }
    public sealed class PlcWatchTableComposition : List<PlcWatchTable>
    {
        public Func<int>? ReadCount { get; set; }
        public new int Count => ReadCount?.Invoke() ?? base.Count;
        public int CreateCalls { get; private set; }
        public int FindCalls { get; private set; }
        public int ImportCalls { get; private set; }
        public Action<PlcWatchTable>? AfterCreate { get; set; }
        public Func<string, PlcWatchTable?>? FindOverride { get; set; }
        public Action<IList<PlcWatchTable>>? AfterImport { get; set; }
        public PlcWatchTable? Find(string name)
        { FindCalls++; return FindOverride == null ? this.SingleOrDefault(table => table.Name == name) : FindOverride(name); }
        public PlcWatchTable Create(string name)
        {
            CreateCalls++; var table = new PlcWatchTable { Name = name }; table.Deleting = () => Remove(table);
            Add(table); AfterCreate?.Invoke(table); return table;
        }
        public IList<PlcWatchTable> Import(FileInfo file, Siemens.Engineering.ImportOptions options)
        {
            ImportCalls++;
            var tables = System.Xml.Linq.XDocument.Load(file.FullName).Root!.Elements()
                .Where(e => e.Name.LocalName == "SW.WatchAndForceTables.PlcWatchTable")
                .Select(e => new PlcWatchTable { Name = (string)e.Element("AttributeList")!.Element("Name")! }).ToArray();
            foreach (var table in tables) { table.Deleting = () => Remove(table); Add(table); }
            AfterImport?.Invoke(tables); return tables;
        }
    }
    public sealed class PlcForceTableComposition : List<PlcForceTable>
    {
        public int ImportCalls { get; private set; }
        public Siemens.Engineering.ImportOptions? LastImportOptions { get; private set; }
        public Func<string, PlcForceTable?>? FindOverride { get; set; }
        public PlcForceTable? Find(string name) => FindOverride == null ? this.SingleOrDefault(table => table.Name == name) : FindOverride(name);
        public IList<PlcForceTable> Import(FileInfo file, Siemens.Engineering.ImportOptions options)
        {
            ImportCalls++; LastImportOptions = options;
            var tables = System.Xml.Linq.XDocument.Load(file.FullName).Root!.Elements()
                .Where(e => e.Name.LocalName == "SW.WatchAndForceTables.PlcForceTable")
                .Select(e => new PlcForceTable { Name = (string)e.Element("AttributeList")!.Element("Name")! }).ToArray();
            AddRange(tables); return tables;
        }
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
        public int ExportCalls { get; private set; }
        public FileInfo? LastExportFile { get; private set; }
        public Siemens.Engineering.ExportOptions? LastExportOptions { get; private set; }
        public Action<FileInfo>? Exporting { get; set; }
        public void Export(FileInfo file, Siemens.Engineering.ExportOptions options)
        {
            ExportCalls++; LastExportFile = file; LastExportOptions = options;
            if (Exporting != null) Exporting(file);
            else File.WriteAllText(file.FullName, "<Document><SW.WatchAndForceTables.PlcForceTable ID=\"0\"><AttributeList><Name>" + Name
                + "</Name></AttributeList></SW.WatchAndForceTables.PlcForceTable></Document>");
        }
    }
    public sealed class PlcWatchAndForceTableUserGroupComposition : List<PlcWatchAndForceTableUserGroup> { }
    public sealed class PlcWatchTable
    {
        public string Name { get; set; } = "";
        public bool IsConsistent { get; set; } = true;
        public PlcTableCommentEntryComposition Entries { get; } = new();
        public Action? Deleting { get; set; }
        public int DeleteCalls { get; private set; }
        public void Delete() { DeleteCalls++; Deleting?.Invoke(); }
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
        public TechnologicalInstanceDB Create(string name, string typeIdentifier, Version version) {
            var db = new TechnologicalInstanceDB { Name = name }; Add(db); return db; }
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
    public sealed partial class PlcSoftware : Siemens.Engineering.CompileServiceOwner
    {
        private readonly Siemens.Engineering.SW.WatchAndForceTables.PlcWatchAndForceTableSystemGroup watchGroup = new();
        public int WatchGroupReads { get; private set; }
        public Siemens.Engineering.SW.WatchAndForceTables.PlcWatchAndForceTableSystemGroup WatchAndForceTableGroup
        { get { WatchGroupReads++; return watchGroup; } }
        public Siemens.Engineering.SW.TechnologicalObjects.TechnologicalInstanceDBGroup TechnologicalObjectGroup { get; } = new();
    }
}
namespace TiaMcp.Adapters
{
    public sealed partial class PlcFoundationEngine
    {
        internal Siemens.Engineering.SW.PlcSoftware Software { get; } = new();
        internal string ReleaseKey { get; set; } = "20";
        // Mirrors the shared selection rule: the exact path or the PLC's unique short name; anything else is refused.
        private (Siemens.Engineering.SW.PlcSoftware Value, string ExactPath, object Context) ReadSelection(string softwarePath) =>
            softwarePath is "devices/D/CPU" or "CPU" ? (Software, "devices/D/CPU", CompileDevice)
                : throw new ArgumentException("Software path not found.", nameof(softwarePath));
        private readonly FakeLifecycle lifecycle = new();
        private sealed class FakeLifecycle { public bool IsLocalSession => true; }
        private sealed class FakeProject { public FileInfo Path => new("project.ap20"); }
        private FakeProject Project() => new();
        private void RequireTargetOffline((Siemens.Engineering.SW.PlcSoftware Value, string ExactPath, object Context) selected)
        { CompileEvents.Add("target-offline"); if (TargetOnline) throw new InvalidOperationException("PLC is online."); }
        private string[] CheckProjectOfflineProviders()
        { CompileEvents.Add("project-offline"); if (ProjectOnline) throw new InvalidOperationException("Project device is online."); return new[] { "HMI" }; }
        private static void Depth(int depth) { if (depth > 64) throw new InvalidOperationException("Depth exceeded."); }
        private static PlcTechnologyReadRow TechnologyMetadata(Siemens.Engineering.SW.TechnologicalObjects.TechnologicalInstanceDB item) => new() {
            Name=item.Name };
    }
    internal static class PlcLifecyclePolicy
    {
        internal static void RequireLocalSessionExecution(bool local, bool dryRun) { if(!local) throw new InvalidOperationException(); }
    }
    internal static class PlcSpecialExportPolicy
    {
        internal static FileInfo Output(string path, bool allowPlannedDirectories)
        {
            if (File.Exists(path) || Directory.Exists(path)) throw new TiaMcp.Adapters.Contracts.AdapterPreconditionException("Choose a new XML export file.");
            return new FileInfo(path);
        }
        internal static string Hash(params string[] fields) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(string.Join("\n", fields))));
        // Stub only: policy behavior has its own tests. Route to the selected fake table.
        internal static PlcSpecialExportResult Run(string kind, string release, string project, string software, string path, string output,
            bool consistent, bool dryRun, string expectedPlanHash, Action requireOffline, Action<FileInfo> export,
            Func<FileInfo, Action<FileInfo>, string?>? publish = null, bool allowPlannedDirectories = false,
            string plannedExportRoot = "", Action<string>? createDirectory = null)
        {
            if(!dryRun) { requireOffline(); export(new FileInfo(output)); }
            return new();
        }
    }
}
