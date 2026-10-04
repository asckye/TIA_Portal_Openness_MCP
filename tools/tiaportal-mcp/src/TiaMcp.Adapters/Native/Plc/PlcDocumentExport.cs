using Documents = TiaMcp.Adapters.Native.Plc.PlcDocumentPrimitives;
using System;
using System.Linq;
using Siemens.Engineering.SW.Blocks;
#if PLC_DOCUMENT_EXPORT
using Siemens.Engineering.SW;
#endif
namespace TiaMcp.PlcFoundation
{
    public sealed partial class PlcFoundationEngine
    {
        public PlcDocumentExportResult ExportAsDocuments(string softwarePath,string blockPath,string exportPath,bool preservePath=false,string expectedPlanHash="",bool dryRun=true)
        {
            PlcDocumentExportPolicy.RequireRelease(ReleaseKey);
            if(preservePath) throw new NotSupportedException("This paired export requires a new explicit output directory; preservePath is outside the bounded contract.");
#if PLC_DOCUMENT_EXPORT
            var selected=ReadSelection(softwarePath);
            if(softwarePath!=selected.ExactPath) throw new ArgumentException("Exact software path required.");
            PlcLifecyclePolicy.RequireLocalSessionExecution(lifecycle.IsLocalSession,false);
            var blocks=BlockGroups(Documents.BlockGroup(selected.Value)).Select(g=>Documents.Blocks(g.Value).Select(b=>new Located<PlcBlock>(Child(g.Path,Documents.Name(b)),b)));
            var target=PlcDocumentExportPolicy.Exact(blocks,x=>x.Path,blockPath).Value;
            if(target.IsKnowHowProtected) throw new NotSupportedException("Protected blocks are outside this document export scope.");
            return PlcDocumentExportPolicy.Run(ReleaseKey,Project().Path.FullName,softwarePath,blockPath,exportPath,Documents.Language(target).ToString(),Documents.IsConsistent(target),dryRun,expectedPlanHash,()=>RequireTargetOffline(selected),(stage,name)=> {
                RequireTargetOffline(selected);
                if(!Documents.IsConsistent(target) || target.IsKnowHowProtected) throw new InvalidOperationException("Block eligibility changed after preview.");
                // Same native overload and success criterion as the full V20/V21 engine; no old-file deletion.
                var result=Documents.Export(target,stage,name);
                if(result==null || Documents.State(result)!=DocumentResultState.Success) throw new InvalidOperationException("Native document export did not succeed.");
                return result.ExportedDocuments.Select(f=>f.FullName).Take(3).ToArray();
            });
#else
            throw new NotSupportedException("Exact document SDK support is not compiled for this release.");
#endif
        }
    }
}
