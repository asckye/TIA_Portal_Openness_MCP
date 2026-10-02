using System;
using System.Linq;

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
                ArticleNumber=entry.ArticleNumber,CatalogPath=entry.CatalogPath,Description=entry.Description,
                TypeIdentifier=entry.TypeIdentifier,TypeIdentifierNormalized=entry.TypeIdentifierNormalized,
                TypeName=entry.TypeName,Version=entry.Version
            }));
#else
            throw new NotSupportedException("This build has no verified typed hardware catalog API.");
#endif
        }
    }
}
