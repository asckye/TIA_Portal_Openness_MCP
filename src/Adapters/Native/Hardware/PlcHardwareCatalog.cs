using System;
using System.Linq;
#if PLC_HARDWARE_CATALOG
using NativeHardware = TiaMcp.Adapters.Hardware.HardwarePrimitives;
#endif

namespace TiaMcp.Adapters
{
    public sealed partial class PlcFoundationEngine
    {
        public PlcHardwareCatalogSearchResult SearchHardwareCatalog(string keyword,int limit=50)
        {
            PlcHardwareCatalogPolicy.RequireRelease(ReleaseKey);
            PlcHardwareCatalogPolicy.Query(keyword,limit);
#if PLC_HARDWARE_CATALOG
            // Manuals require an attached portal and open project. Verify an existing explicit binding; never open or bind automatically.
            RequireHardwareCatalogBinding();
            Project();
            var attached=Portal();
            return PlcHardwareCatalogPolicy.Search(ReleaseKey,keyword,limit,query=>attached.HardwareCatalog.Find(query).Select(entry=>new PlcHardwareCatalogCandidate {
                ArticleNumber=NativeHardware.ArticleNumber(entry),CatalogPath=NativeHardware.CatalogPath(entry),Description=NativeHardware.Description(entry),
                TypeIdentifier=NativeHardware.TypeIdentifier(entry),TypeIdentifierNormalized=NativeHardware.TypeIdentifierNormalized(entry),
                TypeName=NativeHardware.TypeName(entry),Version=NativeHardware.Version(entry)
            }));
#else
            throw new NotSupportedException("This build has no verified typed hardware catalog API.");
#endif
        }
    }
}
