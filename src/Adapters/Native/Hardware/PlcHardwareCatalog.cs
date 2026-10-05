using System;
using System.Linq;
#if PLC_HARDWARE_CATALOG
using Hardware = TiaMcp.Adapters.Hardware.HardwarePrimitives;
#endif

namespace TiaMcp.PlcFoundation
{
    public sealed partial class PlcFoundationEngine
    {
        public PlcHardwareCatalogSearchResult SearchHardwareCatalog(string keyword,int limit=50)
        {
            PlcHardwareCatalogPolicy.RequireRelease(ReleaseKey);
            PlcHardwareCatalogPolicy.Query(keyword,limit);
#if PLC_HARDWARE_CATALOG
            // Manuals require an attached portal and open project. Verify an existing explicit binding; never open or bind automatically.
            Project();
            var attached=Portal();
            return PlcHardwareCatalogPolicy.Search(ReleaseKey,keyword,limit,query=>attached.HardwareCatalog.Find(query).Select(entry=>new PlcHardwareCatalogCandidate {
                ArticleNumber=Hardware.ArticleNumber(entry),CatalogPath=Hardware.CatalogPath(entry),Description=Hardware.Description(entry),
                TypeIdentifier=Hardware.TypeIdentifier(entry),TypeIdentifierNormalized=Hardware.TypeIdentifierNormalized(entry),
                TypeName=Hardware.TypeName(entry),Version=Hardware.Version(entry)
            }));
#else
            throw new NotSupportedException("This build has no verified typed hardware catalog API.");
#endif
        }
    }
}
