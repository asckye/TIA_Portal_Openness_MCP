// Pure test doubles. This is NOT an SDK signature check or native acceptance.
namespace TiaMcp.PlcFoundation;
public sealed class FakeCatalog
{
    public int Calls;
    public IList<PlcHardwareCatalogCandidate> Rows = new List<PlcHardwareCatalogCandidate>();
    public IList<PlcHardwareCatalogCandidate> Find(string query) { Calls++; return Rows; }
}
public sealed class FakePortal { public FakeCatalog HardwareCatalog { get; }=new(); }
public sealed partial class PlcFoundationEngine
{
    public string ReleaseKey { get; set; }="19";
    public bool Bound { get; set; }
    public int PortalReads { get; private set; }
    public FakePortal Attached { get; }=new();
    private object Project() => Bound?new object():throw new InvalidOperationException("Explicit project binding required.");
    private FakePortal Portal() { PortalReads++; return Attached; }
}
