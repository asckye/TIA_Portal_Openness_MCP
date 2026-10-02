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
        public PlcBatchDocumentExportResult ExportBlocksAsDocuments(string softwarePath,string groupPath,string exportPath,bool recursive=false,int maxItems=128,bool preservePath=false,string expectedPlanHash="",bool dryRun=true)
        {
            PlcDocumentExportPolicy.RequireRelease(ReleaseKey);
            if(preservePath) throw new NotSupportedException("Batch document export uses one new tree with path-hashed block directories; preservePath is outside scope.");
#if PLC_DOCUMENT_EXPORT
            var selected=ReadSelection(softwarePath);
            if(softwarePath!=selected.ExactPath) throw new ArgumentException("Exact software path required.");
            PlcLifecyclePolicy.RequireLocalSessionExecution(lifecycle.IsLocalSession,false);
            var path=PlcExchangePolicy.ObjectPath(groupPath,true);
            if(path!=groupPath) throw new ArgumentException("Exact canonical group path required.");
            var group=BatchGroup(BlockGroups(selected.Value.BlockGroup),path);
            var groups=recursive?BlockGroups(group,path):new[]{new Located<PlcBlockGroup>(path,group)};
            var sources=BatchGroupsBounded(groups).SelectMany(g=>g.Value.Blocks.Select(block=>new PlcBatchDocumentExportSource {
                Path=Child(g.Path,block.Name),Language=block.ProgrammingLanguage.ToString(),Consistent=block.IsConsistent,Protected=block.IsKnowHowProtected,
                Export=(stage,name)=> {
                    RequireTargetOffline(selected);
                    if(!block.IsConsistent || block.IsKnowHowProtected) throw new InvalidOperationException("Block eligibility changed after preview.");
                    var result=block.ExportAsDocuments(stage,name);
                    if(result==null || result.State!=DocumentResultState.Success) throw new InvalidOperationException("Native document export did not succeed.");
                    return result.ExportedDocuments.Select(file=>file.FullName).Take(3).ToArray();
                }
            }));
            return PlcBatchDocumentExportPolicy.Run(ReleaseKey,Project().Path.FullName,softwarePath,path,recursive,exportPath,maxItems,dryRun,expectedPlanHash,sources,()=>RequireTargetOffline(selected));
#else
            throw new NotSupportedException("Exact document SDK support is not compiled for this release.");
#endif
        }
    }
}
