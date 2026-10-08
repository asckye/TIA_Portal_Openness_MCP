// Pure test doubles. This is NOT an SDK signature check or native acceptance.
namespace TiaMcp.Adapters
{
    public sealed class FakeCatalog
    {
        public int Calls;
        public IList<PlcHardwareCatalogCandidate> Rows = new List<PlcHardwareCatalogCandidate>();
        public IEnumerable<Siemens.Engineering.HW.HardwareCatalog.CatalogEntry> Find(string query) { Calls++; return Rows.Select(row=>new Siemens.Engineering.HW.HardwareCatalog.CatalogEntry(row)); }
    }
    public sealed class FakePortal { public FakeCatalog HardwareCatalog { get; }=new(); }
    public sealed partial class PlcFoundationEngine
    {
        public string ReleaseKey { get; set; }="19";
        public bool Bound { get; set; }
        public int PortalReads { get; private set; }
        public FakePortal Attached { get; }=new();
        private Siemens.Engineering.ProjectBase Project() => Bound?new Siemens.Engineering.ProjectBase():throw new InvalidOperationException("Explicit project binding required.");
        private FakePortal Portal() { PortalReads++; return Attached; }
        private void RequireHardwareCatalogBinding() { if(!Bound) throw new TiaMcp.Adapters.Contracts.AdapterPreconditionException("Hardware catalog tools require ConnectPortal followed by AttachOpenProject first.","session",false); }
    }
}
namespace Siemens.Engineering
{
    public class ProjectBase { public HW.DeviceComposition Devices { get; }=new(); }
}
namespace Siemens.Engineering.HW
{
    public class Device { }
    public class DeviceComposition
    {
        public Device CreateWithItem(string typeIdentifier,string name,string deviceName) => throw new NotSupportedException("Catalog tests do not create devices.");
    }
}
namespace Siemens.Engineering.HW.HardwareCatalog
{
    public sealed class CatalogEntry
    {
        private readonly TiaMcp.Adapters.PlcHardwareCatalogCandidate row;
        public CatalogEntry(TiaMcp.Adapters.PlcHardwareCatalogCandidate row) { this.row=row; }
        public string ArticleNumber => row.ArticleNumber!;
        public string CatalogPath => row.CatalogPath!;
        public string Description => row.Description!;
        public string TypeIdentifier => row.TypeIdentifier!;
        public string TypeIdentifierNormalized => row.TypeIdentifierNormalized!;
        public string TypeName => row.TypeName!;
        public string Version => row.Version!;
    }
}
