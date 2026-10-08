using Documents = TiaMcp.Adapters.Native.Plc.PlcDocumentPrimitives;
using System;
using TiaMcp.Adapters.Contracts;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Siemens.Engineering;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.ExternalSources;

namespace TiaMcp.Adapters
{
    public sealed partial class PlcFoundationEngine
    {
        private bool externalSourceOutcomeUnknown;
        private Action ExternalSourceTargetCheck(string softwarePath,out PlcSoftware software,out string exactPath,out string projectFile,out int processId)
        {
            if(externalSourceOutcomeUnknown) throw new AdapterPreconditionException("Prior external-source outcome is unknown; inspect the project and start a new explicit session before another write.","softwarePath",false);
            var selected=ReadSelection(softwarePath);
            exactPath=selected.ExactPath;
            PlcLifecyclePolicy.RequireLocalSessionExecution(lifecycle.IsLocalSession,false);
            software=selected.Value; projectFile=Project().Path.FullName;
            processId=lifecycle.ProcessId ?? throw new AdapterPreconditionException("An explicitly attached process is required.","softwarePath",false);
            var expectedProject=projectFile; var expectedProcess=processId;
            return ()=> {
                RequireProjectIdentity(expectedProject);
                var current=ReadSelection(softwarePath);
                if(lifecycle.ProcessId!=expectedProcess || !object.Equals(current.Value,selected.Value) || !object.Equals(current.Context,selected.Context)) throw new AdapterPreconditionException("Selected PLC identity changed.","softwarePath",false);
                RequireTargetOffline(current);
            };
        }
        public PlcExternalSourceImportResult ImportPlcExternalSource(string softwarePath,string groupPath,string filePath,bool dryRun=true,string expectedPlanHash="",bool confirm=false,string expectedProjectFile="")
        {
            PlcExternalSourceWorkflowPolicy.RequireRoot(groupPath);
            var input=new FileInfo(TiaOpenness.Shared.NativeInputPolicy.FullPath(PlcExternalSourceImportPolicy.ValidateFile(filePath)));
            var check=ExternalSourceTargetCheck(softwarePath,out var software,out var exactPath,out var projectFile,out var processId);
            var sources=Documents.Sources(Documents.ExternalSourceGroup(software));
            var result=new PlcExternalSourceImportResult {Operation="ImportPlcExternalSource",Release=ReleaseKey,ProjectFile=projectFile,ProcessId=processId,SoftwarePath=exactPath,FilePath=input.FullName,RequestedSourceName=input.Name};
            using(var stream=TiaOpenness.Shared.NativeInputPolicy.OpenRead(input.FullName,"filePath"))
                PlcExternalSourceWorkflowPolicy.Import(result,stream,dryRun,expectedPlanHash,confirm,expectedProjectFile,()=>sources.Select(x=>Documents.Name(x)),check,(name,path)=> {
                    // Exact SDK signature on all eight releases: name first, full path second.
                    var created=Documents.CreateFromFile(sources,name,path);
                    if(created==null || !object.Equals(created.Parent,Documents.ExternalSourceGroup(software)) || !object.Equals(Documents.Find(sources,Documents.Name(created)),created)) throw new InvalidOperationException("CreateFromFile did not return a source in the selected PLC root.");
                    return Documents.Name(created);
                });
            if(result.RequiresSessionReset) externalSourceOutcomeUnknown=true;
            return result;
        }
        private sealed class ExternalSourceNativeObject
        {
            internal IEngineeringObject Native=null!;
            internal PlcExternalSourceObject Details=null!;
        }
        private static ExternalSourceNativeObject[] ExternalSourceObjects(PlcSoftware software)
        {
            var objects=new List<ExternalSourceNativeObject>();
            foreach(var group in BlockGroups(Documents.BlockGroup(software))) foreach(var block in Documents.Blocks(group.Value))
                objects.Add(new ExternalSourceNativeObject {Native=block,Details=new PlcExternalSourceObject {Kind="block",Path=Child(group.Path,Documents.Name(block)),Name=Documents.Name(block),TypeName=block.GetType().Name,ProgrammingLanguage=Documents.Language(block).ToString(),IsConsistent=Documents.IsConsistent(block),ModifiedDate=block.ModifiedDate}});
            foreach(var group in TypeGroups(Documents.TypeGroup(software))) foreach(var type in group.Value.Types)
                objects.Add(new ExternalSourceNativeObject {Native=type,Details=new PlcExternalSourceObject {Kind="type",Path=Child(group.Path,Documents.Name(type)),Name=Documents.Name(type),TypeName=type.GetType().Name,IsConsistent=type.IsConsistent,ModifiedDate=type.ModifiedDate}});
            return objects.OrderBy(x=>PlcExternalSourceWorkflowPolicy.ObjectKey(x.Details),StringComparer.Ordinal).ToArray();
        }
        public PlcExternalSourceGenerationResult GenerateBlocksFromExternalSource(string softwarePath,string externalSourceName,bool dryRun=true,string expectedPlanHash="",bool confirm=false,string expectedProjectFile="")
        {
            PlcExternalSourceWorkflowPolicy.RequireSourceName(externalSourceName);
            var checkTarget=ExternalSourceTargetCheck(softwarePath,out var software,out var exactPath,out var projectFile,out var processId);
            var sources=Documents.Sources(Documents.ExternalSourceGroup(software));
            var source=Documents.Find(sources,externalSourceName) ?? throw new AdapterPreconditionException("External source not found: "+externalSourceName,"externalSourceName");
            if(Documents.Name(source)!=externalSourceName) throw new AdapterPreconditionException("Use the exact external source name including its extension.","externalSourceName");
            var result=new PlcExternalSourceGenerationResult {Operation="GenerateBlocksFromExternalSource",Release=ReleaseKey,ProjectFile=projectFile,ProcessId=processId,SoftwarePath=exactPath,SourceName=Documents.Name(source),SourceIdentity=externalDeleteIdentities.Get(source)};
            Action check=()=> { checkTarget(); if(!object.Equals(Documents.Find(sources,externalSourceName),source)) throw new AdapterPreconditionException("Selected external source changed.","softwarePath",false); };
#if PLC_SOURCE_RESULTS
            IList<IEngineeringObject>? generated=null;
#endif
            PlcExternalSourceWorkflowPolicy.Generate(result,dryRun,expectedPlanHash,confirm,expectedProjectFile,()=>ExternalSourceObjects(software).Select(x=>x.Details).ToArray(),check,()=> {
#if PLC_SOURCE_RESULTS
                generated=Documents.Generate(source,GenerateBlockOption.None);
#else
                // V14 SP1 has only the void overload. Inventory observations cannot identify
                // every generated object (e.g. overwriting a block without changed metadata).
                Documents.Generate(source);
#endif
            },()=> {
#if PLC_SOURCE_RESULTS
                var current=ExternalSourceObjects(software);
                return generated!.Select(item=>current.Single(x=>object.Equals(x.Native,item)).Details).ToArray();
#else
                return new PlcExternalSourceObject[0];
#endif
            },ex=>ex is EngineeringException);
            if(result.RequiresSessionReset) externalSourceOutcomeUnknown=true;
            return result;
        }
    }
}
