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
        public PlcBatchDocumentExportResult ExportBlocksAsDocuments(string softwarePath,string groupPath,string exportPath,bool recursive=false,int maxItems=128,bool preservePath=false,string expectedPlanHash="",bool dryRun=true)
        {
            PlcDocumentExportPolicy.RequireRelease(ReleaseKey);
            if(preservePath) throw new TiaMcp.Adapters.Contracts.AdapterPreconditionException("Batch document export uses one new tree with path-hashed block directories; preservePath is outside scope.","export-plan",false);
#if PLC_DOCUMENT_EXPORT
            var selected=ReadSelection(softwarePath);
            TiaOpenness.Shared.NativeExportPolicy.RequireSoftwarePath(softwarePath,selected.ExactPath,true);
            PlcLifecyclePolicy.RequireLocalSessionExecution(lifecycle.IsLocalSession,false);
            var path=PlcExchangePolicy.ObjectPath(groupPath,true);
            if(path!=groupPath) throw new ArgumentException("Exact canonical group path required.");
            var group=BatchGroup(BlockGroups(Documents.BlockGroup(selected.Value)),path);
            var groups=recursive?BlockGroups(group,path):new[]{new Located<PlcBlockGroup>(path,group)};
            var sources=BatchGroupsBounded(groups).SelectMany(g=>Documents.Blocks(g.Value).Select(block=>new PlcBatchDocumentExportSource {
                Path=Child(g.Path,Documents.Name(block)),Language=Documents.Language(block).ToString(),Consistent=Documents.IsConsistent(block),Protected=block.IsKnowHowProtected,
                Export=(stage,name)=> {
                    RequireTargetOffline(selected);
                    if(!Documents.IsConsistent(block) || block.IsKnowHowProtected) throw new TiaMcp.Adapters.Contracts.AdapterPreconditionException("Block eligibility changed after preview.","export-plan",false);
                    var result=Documents.Export(block,stage,name);
                    if(result==null || Documents.State(result)!=DocumentResultState.Success) throw new InvalidOperationException("Native document export did not succeed.");
                    return result.ExportedDocuments.Select(file=>file.FullName).Take(3).ToArray();
                }
            }));
            return PlcBatchDocumentExportPolicy.Run(ReleaseKey,Project().Path.FullName,softwarePath,path,recursive,exportPath,maxItems,dryRun,expectedPlanHash,sources,()=>RequireTargetOffline(selected));
#else
            throw new TiaMcp.Adapters.Contracts.AdapterPreconditionException("Exact document SDK support is not compiled for this release.","export-plan",false);
#endif
        }
    }
}
