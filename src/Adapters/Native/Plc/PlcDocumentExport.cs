using Documents = TiaMcp.Adapters.Native.Plc.PlcDocumentPrimitives;
using System;
using System.Linq;
using Siemens.Engineering.SW.Blocks;
#if PLC_DOCUMENT_EXPORT
using Siemens.Engineering.SW;
#endif
namespace TiaMcp.Adapters
{
    public sealed partial class PlcFoundationEngine
    {
        public PlcDocumentExportResult ExportAsDocuments(string softwarePath,string blockPath,string exportPath,bool preservePath=false,string expectedPlanHash="",bool dryRun=true)
        {
            PlcDocumentExportPolicy.RequireRelease(ReleaseKey);
            if(preservePath) throw new TiaMcp.Adapters.Contracts.AdapterPreconditionException("This paired export requires a new explicit output directory; preservePath is outside the bounded contract.","export-plan",false);
#if PLC_DOCUMENT_EXPORT
            var selected=ReadSelection(softwarePath);
            PlcLifecyclePolicy.RequireLocalSessionExecution(lifecycle.IsLocalSession,false);
            var blocks=BlockGroups(Documents.BlockGroup(selected.Value)).Select(g=>Documents.Blocks(g.Value).Select(b=>new Located<PlcBlock>(Child(g.Path,Documents.Name(b)),b)));
            var target=PlcDocumentExportPolicy.Exact(blocks,x=>x.Path,blockPath).Value;
            if(target.IsKnowHowProtected) throw new TiaMcp.Adapters.Contracts.AdapterPreconditionException("Protected blocks are outside this document export scope.","export-plan",false);
            return PlcDocumentExportPolicy.Run(ReleaseKey,Project().Path.FullName,selected.ExactPath,blockPath,exportPath,Documents.Language(target).ToString(),Documents.IsConsistent(target),dryRun,expectedPlanHash,()=>RequireTargetOffline(selected),(stage,name)=> {
                RequireTargetOffline(selected);
                if(!Documents.IsConsistent(target) || target.IsKnowHowProtected) throw new TiaMcp.Adapters.Contracts.AdapterPreconditionException("Block eligibility changed after preview.","export-plan",false);
                // Same native overload and success criterion as the full V20/V21 engine; no old-file deletion.
                var result=Documents.Export(target,stage,name);
                var state=result==null ? (object?)null : Documents.State(result);
                if(!TiaMcp.Adapters.Contracts.NativeResultStates.Succeeded(state))
                    throw new TiaMcp.Adapters.Contracts.NativeResultException(TiaMcp.Adapters.Contracts.NativeResultStates.Documents,state);
                return result.ExportedDocuments.Select(f=>f.FullName).Take(3).ToArray();
            });
#else
            throw new TiaMcp.Adapters.Contracts.AdapterPreconditionException("Exact document SDK support is not compiled for this release.","export-plan",false);
#endif
        }
    }
}
