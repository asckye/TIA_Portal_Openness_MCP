using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Siemens.Engineering;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.Tags;
using Siemens.Engineering.SW.Types;

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
            if(!new[]{"FC","FB","OB","GlobalDB","InstanceDB"}.Contains(kind)) throw new NotSupportedException("Target contains a block kind outside the bounded ordinary inventory.");
            // Number is a required collision check here, not optional display metadata.
            var raw=((IEngineeringObject)block).GetAttribute("Number");
            if(raw==null || !int.TryParse(raw.ToString(),out var number) || number<0) throw new InvalidOperationException("Cannot establish exact existing block number.");
            return new PlcBatchImportObject {Name=block.Name,Kind=kind,Number=number,GroupPath=group};
        }
        private static PlcBatchImportObject BatchReturnedBlock(PlcBlock block,string group,object expectedGroup)
        {
            // Keep readable identity evidence even if native number inspection fails after mutation.
            var result=new PlcBatchImportObject {Name=block.Name,Kind=block.GetType().Name,GroupPath=BatchReturnedGroup(block,expectedGroup,group)};
            try {var value=((IEngineeringObject)block).GetAttribute("Number");if(value!=null && int.TryParse(value.ToString(),out var number) && number>=0)result.Number=number;}
            catch(Exception) { /* Missing number is an explicit verification failure in the policy. */ }
            return result;
        }
        private static string BatchReturnedGroup(IEngineeringObject item,object expected,string exactPath)
        {
            try {if(ReferenceEquals(item.Parent,expected)) return exactPath;}
            catch(Exception) { /* Preserve a visible ownership-verification failure. */ }
            // A traversal segment is impossible in an accepted canonical destination.
            return "../<unverified-native-owner>";
        }
        private IEnumerable<PlcBatchImportObject> BatchImportInventory(PlcSoftware software)
        {
            foreach(var group in BatchGroupsBounded(BlockGroups(software.BlockGroup)))
                foreach(var block in group.Value.Blocks) yield return BatchBlock(block,group.Path);
            foreach(var group in BatchGroupsBounded(TypeGroups(software.TypeGroup)))
                foreach(var type in group.Value.Types) yield return new PlcBatchImportObject {Name=type.Name,Kind="UDT",GroupPath=group.Path};
            foreach(var group in BatchGroupsBounded(TagGroups(software.TagTableGroup)))
                foreach(var table in group.Value.TagTables) yield return new PlcBatchImportObject {Name=table.Name,Kind="TagTable",GroupPath=group.Path};
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
            if(request.Software!=selected.ExactPath) throw new ArgumentException("Exact software path required, aliases refused.");
            PlcLifecyclePolicy.RequireLocalSessionExecution(lifecycle.IsLocalSession,false);
            request.Project=Project().Path.FullName;request.ProcessId=lifecycle.ProcessId ?? throw new InvalidOperationException("Explicit process identity required.");
            foreach(var path in new[]{request.BlockGroup,request.TypeGroup,request.TagGroup}) if(path!=PlcExchangePolicy.ObjectPath(path,true)) throw new ArgumentException("Exact canonical group path required.");
            var blocks=BatchGroup(BlockGroups(selected.Value.BlockGroup),request.BlockGroup);
            var types=request.Program ? BatchGroup(TypeGroups(selected.Value.TypeGroup),request.TypeGroup) : null;
            var tags=request.Program ? BatchGroup(TagGroups(selected.Value.TagTableGroup),request.TagGroup) : null;
            var inventory=BatchImportInventory(selected.Value).Take(4097).ToArray();
            Action check=()=>
            {
                RequireProjectIdentity(request.Project);
                if(lifecycle.ProcessId!=request.ProcessId) throw new InvalidOperationException("Process identity changed.");
                var fresh=ReadSelection(request.Software);
                if(fresh.ExactPath!=selected.ExactPath || !ReferenceEquals(fresh.Value,selected.Value) || !ReferenceEquals(fresh.Context,selected.Context)) throw new InvalidOperationException("Selected target identity changed.");
                if(!ReferenceEquals(BatchGroup(BlockGroups(fresh.Value.BlockGroup),request.BlockGroup),blocks) ||
                   (request.Program && (!ReferenceEquals(BatchGroup(TypeGroups(fresh.Value.TypeGroup),request.TypeGroup),types) || !ReferenceEquals(BatchGroup(TagGroups(fresh.Value.TagTableGroup),request.TagGroup),tags)))) throw new InvalidOperationException("Destination group identity changed.");
                RequireTargetOffline(fresh);
            };
            return PlcBatchImportPolicy.Run(request,inventory,check,(file,planned)=>
            {
                // Native None throws on existing objects. No filename-derived identity, repair, renumber or Override fallback.
                if(planned.Kind=="UDT") return types!.Types.Import(file,ImportOptions.None).Select(t=>new PlcBatchImportObject {Name=t.Name,Kind=t.GetType().Name=="PlcStruct" ? "UDT" : t.GetType().Name,GroupPath=BatchReturnedGroup(t,types!,request.TypeGroup)}).ToArray();
                if(planned.Kind=="TagTable") return tags!.TagTables.Import(file,ImportOptions.None).Select(t=>new PlcBatchImportObject {Name=t.Name,Kind=t.GetType().Name=="PlcTagTable" ? "TagTable" : t.GetType().Name,GroupPath=BatchReturnedGroup(t,tags!,request.TagGroup)}).ToArray();
                return blocks.Blocks.Import(file,ImportOptions.None).Select(b=>BatchReturnedBlock(b,request.BlockGroup,blocks)).ToArray();
            });
        }
    }
}
