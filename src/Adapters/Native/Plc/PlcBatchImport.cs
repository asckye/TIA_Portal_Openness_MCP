using PlcNative = TiaMcp.Adapters.Native.Plc.PlcBlockPrimitives;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Siemens.Engineering;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.Tags;
using Siemens.Engineering.SW.Types;
using TiaMcp.Adapters.Contracts;

namespace TiaMcp.PlcFoundation
{
    public sealed partial class PlcFoundationEngine
    {
        public PlcBatchImportResult ImportBlocksFromDirectory(string softwarePath,string groupPath,string dir,string regexName="",bool overwrite=false,bool dryRun=true,string[]? importOrder=null,string expectedPlanHash="",bool confirm=false,string expectedProjectFile="",int maxItems=128)
        {
            return BatchImport(new PlcBatchImportRequest {Software=softwarePath,BlockGroup=groupPath,Directory=dir,Regex=regexName,Overwrite=overwrite,DryRun=dryRun,Order=importOrder ?? new string[0],ExpectedHash=expectedPlanHash,Confirm=confirm,ExpectedProject=expectedProjectFile,MaxItems=maxItems,Release=ReleaseKey});
        }
        public PlcBatchImportResult ImportPlcProgramFromDirectory(string softwarePath,string sourceDir,string typeGroupPath="",string tagFolderPath="",string technologyFolderPath="",string blockGroupPath="",string regexName="",bool compileAfter=false,bool stopOnImportFailure=true,bool dryRun=true,string[]? importOrder=null,string expectedPlanHash="",bool confirm=false,string expectedProjectFile="",bool overwrite=false,int maxItems=128)
        {
            return BatchImport(new PlcBatchImportRequest {Software=softwarePath,BlockGroup=blockGroupPath,TypeGroup=typeGroupPath,TagGroup=tagFolderPath,TechnologyGroup=technologyFolderPath,Directory=sourceDir,Program=true,Regex=regexName,CompileAfter=compileAfter,StopOnImportFailure=stopOnImportFailure,Overwrite=overwrite,DryRun=dryRun,Order=importOrder ?? new string[0],ExpectedHash=expectedPlanHash,Confirm=confirm,ExpectedProject=expectedProjectFile,MaxItems=maxItems,Release=ReleaseKey});
        }
        private static PlcBatchImportObject BatchBlock(PlcBlock block,string group)
        {
            var kind=block.GetType().Name;
            if(!new[]{"FC","FB","OB","GlobalDB","InstanceDB"}.Contains(kind)) throw new AdapterPreconditionException("Target contains a block kind outside the bounded ordinary inventory.","softwarePath",false);
            // Number is a required collision check here, not optional display metadata.
            var raw=((IEngineeringObject)block).GetAttribute("Number");
            if(raw==null || !int.TryParse(raw.ToString(),out var number) || number<0) throw new AdapterPreconditionException("Cannot establish exact existing block number.","softwarePath",false);
            return new PlcBatchImportObject {Name=PlcNative.Name(block),Kind=kind,Number=number,GroupPath=group};
        }
        private static PlcBatchImportObject BatchReturnedBlock(PlcBlock block,string group,object expectedGroup)
        {
            // Keep readable identity evidence even if native number inspection fails after mutation.
            var result=new PlcBatchImportObject {Name=PlcNative.Name(block),Kind=block.GetType().Name,GroupPath=BatchReturnedGroup(block,expectedGroup,group)};
            try {var value=((IEngineeringObject)block).GetAttribute("Number");if(value!=null && int.TryParse(value.ToString(),out var number) && number>=0)result.Number=number;}
            catch(Exception) /* swallow(native-fallback): an unreadable returned block number stays missing so the batch policy rejects verification */ { /* Missing number is an explicit verification failure in the policy. */ }
            return result;
        }
        private static string BatchReturnedGroup(IEngineeringObject item,object expected,string exactPath)
        {
            try {if(ReferenceEquals(PlcNative.Parent(item),expected)) return exactPath;}
            catch(Exception) /* swallow(native-fallback): unreadable native ownership falls through to the sentinel rejected by the batch policy */ { /* Preserve a visible ownership-verification failure. */ }
            // A traversal segment is impossible in an accepted canonical destination.
            return "../<unverified-native-owner>";
        }
        private IEnumerable<PlcBatchImportObject> BatchImportInventory(PlcSoftware software)
        {
            foreach(var group in BatchGroupsBounded(BlockGroups(PlcNative.BlockGroup(software))))
                foreach(var block in PlcNative.Blocks(group.Value)) yield return BatchBlock(block,group.Path);
            foreach(var group in BatchGroupsBounded(TypeGroups(PlcNative.TypeGroup(software))))
                foreach(var type in PlcNative.Types(group.Value)) yield return new PlcBatchImportObject {Name=PlcNative.Name(type),Kind="UDT",GroupPath=group.Path};
            foreach(var group in BatchGroupsBounded(TagGroups(software.TagTableGroup)))
                foreach(var table in PlcNative.TagTables(group.Value)) yield return new PlcBatchImportObject {Name=table.Name,Kind="TagTable",GroupPath=group.Path};
        }
        private PlcBatchImportResult BatchImport(PlcBatchImportRequest request)
        {
            // Reject unsupported switches before any filesystem/native lookup.
            if(request.Overwrite || request.CompileAfter || !request.StopOnImportFailure || request.TechnologyGroup!="")
            {
                var validation=new PlcBatchImportRequest {Release=ReleaseKey,Overwrite=request.Overwrite,CompileAfter=request.CompileAfter,StopOnImportFailure=request.StopOnImportFailure,TechnologyGroup=request.TechnologyGroup};
                PlcBatchImportPolicy.ValidateOptions(validation);
            }
            var selected=ReadSelection(request.Software);
            if(request.Software!=selected.ExactPath) throw new AdapterPreconditionException("Exact software path required, aliases refused.","softwarePath");
            PlcLifecyclePolicy.RequireLocalSessionExecution(lifecycle.IsLocalSession,false);
            request.Project=Project().Path.FullName;request.ProcessId=lifecycle.ProcessId ?? throw new InvalidOperationException("Explicit process identity required.");
            foreach(var item in new[]{(Path:request.BlockGroup,Parameter:"groupPath"),(Path:request.TypeGroup,Parameter:"typeGroupPath"),(Path:request.TagGroup,Parameter:"tagFolderPath")})
                if(item.Path!=PlcExchangePolicy.ObjectPath(item.Path,true,item.Parameter)) throw new AdapterPreconditionException("Exact canonical group path required.",item.Parameter);
            var blocks=BatchGroup(BlockGroups(PlcNative.BlockGroup(selected.Value)),request.BlockGroup);
            var types=request.Program ? BatchGroup(TypeGroups(PlcNative.TypeGroup(selected.Value)),request.TypeGroup) : null;
            var tags=request.Program ? BatchGroup(TagGroups(selected.Value.TagTableGroup),request.TagGroup) : null;
            var inventory=BatchImportInventory(selected.Value).Take(4097).ToArray();
            Action check=()=>
            {
                RequireProjectIdentity(request.Project);
                if(lifecycle.ProcessId!=request.ProcessId) throw new InvalidOperationException("Process identity changed.");
                var fresh=ReadSelection(request.Software);
                if(fresh.ExactPath!=selected.ExactPath || !ReferenceEquals(fresh.Value,selected.Value) || !ReferenceEquals(fresh.Context,selected.Context)) throw new InvalidOperationException("Selected target identity changed.");
                if(!ReferenceEquals(BatchGroup(BlockGroups(PlcNative.BlockGroup(fresh.Value)),request.BlockGroup),blocks) ||
                   (request.Program && (!ReferenceEquals(BatchGroup(TypeGroups(PlcNative.TypeGroup(fresh.Value)),request.TypeGroup),types) || !ReferenceEquals(BatchGroup(TagGroups(fresh.Value.TagTableGroup),request.TagGroup),tags)))) throw new InvalidOperationException("Destination group identity changed.");
                RequireTargetOffline(fresh);
            };
            return PlcBatchImportPolicy.Run(request,inventory,check,(file,planned)=>
            {
                // Native None throws on existing objects. No filename-derived identity, repair, renumber or Override fallback.
                if(planned.Kind=="UDT") return PlcNative.Types(types!).Import(file,ImportOptions.None).Select(t=>new PlcBatchImportObject {Name=PlcNative.Name(t),Kind=t.GetType().Name=="PlcStruct" ? "UDT" : t.GetType().Name,GroupPath=BatchReturnedGroup(t,types!,request.TypeGroup)}).ToArray();
                if(planned.Kind=="TagTable") return PlcNative.TagTables(tags!).Import(file,ImportOptions.None).Select(t=>new PlcBatchImportObject {Name=t.Name,Kind=t.GetType().Name=="PlcTagTable" ? "TagTable" : t.GetType().Name,GroupPath=BatchReturnedGroup(t,tags!,request.TagGroup)}).ToArray();
                return PlcNative.Import(PlcNative.Blocks(blocks),file,ImportOptions.None).Select(b=>BatchReturnedBlock(b,request.BlockGroup,blocks)).ToArray();
            });
        }
    }
}
