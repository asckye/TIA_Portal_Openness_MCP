using System;
using System.Linq;
namespace TiaMcp.Adapters
{
    public sealed partial class PlcFoundationEngine
    {
        public PlcBatchDocumentImportResult ImportBlocksFromDocuments(string softwarePath,string groupPath,string importPath,string[] fileNamesWithoutExtension,
                bool overwrite=false,bool dryRun=true,string expectedPlanHash="",bool confirm=false,string expectedProjectFile="")
        {
            if(fileNamesWithoutExtension==null || fileNamesWithoutExtension.Length<1 || fileNamesWithoutExtension.Length>PlcBatchDocumentImportPolicy
                    .MaximumItems || fileNamesWithoutExtension.Distinct(StringComparer.OrdinalIgnoreCase).Count()!=fileNamesWithoutExtension.Length)
                throw new ArgumentException("Explicit ordered unique manifest of 1..16 basenames required.");
            groupPath = PlcExchangePolicy.ObjectPath(groupPath,true,"groupPath");
            var contexts=fileNamesWithoutExtension
                .Select(name=>
                    CreateDocumentImportContext(new PlcDocumentImportRequest {
                        Release=ReleaseKey,Software=softwarePath,Group=groupPath,Directory=importPath,Name=name,Overwrite=overwrite}))
                .ToArray();
            TiaOpenness.Shared.NativeExportPolicy.RequireSoftwarePath(softwarePath,contexts[0].Request.Software,true);
            var result=PlcBatchDocumentImportPolicy.Run(contexts,dryRun,expectedPlanHash,confirm,expectedProjectFile);
            return result;
        }
    }
}
