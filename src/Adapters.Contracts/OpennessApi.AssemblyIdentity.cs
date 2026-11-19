namespace TiaMcp.Adapters.Contracts;

// Standalone consumers of TiaVersionCatalog only need the inlined constant.
#if TIA_STANDALONE_VERSION_CATALOG
internal static class OpennessApi
#else
public sealed partial class OpennessApi
#endif
{
    // The verified V21 Base, Step7, Safety and Startdrive assemblies have a different identity from Siemens.Engineering.dll.
    public const string SplitAssemblyRelease = "21";
}
