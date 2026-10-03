using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
#if PLC_DOCUMENT_EXPORT
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
#endif
namespace TiaMcp.PlcFoundation
{
    public sealed partial class PlcFoundationEngine
    {
        private readonly PlcExternalSourceDeleteIdentities documentImportIdentities=new PlcExternalSourceDeleteIdentities();
        private bool documentImportOutcomeUnknown;
        public PlcDocumentImportResult ImportFromDocuments(string softwarePath,string groupPath,string importPath,string fileNameWithoutExtension,bool overwrite=false,bool dryRun=true,string expectedPlanHash="",bool confirm=false,string expectedProjectFile="")
        {
            if(documentImportOutcomeUnknown) throw new InvalidOperationException("Prior document import outcome unknown; inspect before a new explicit session. Never replay.");
            var r=new PlcDocumentImportRequest {Release=ReleaseKey,Software=softwarePath,Group=groupPath,Directory=importPath,Name=fileNameWithoutExtension,Overwrite=overwrite,DryRun=dryRun,ExpectedHash=expectedPlanHash,Confirm=confirm,ExpectedProject=expectedProjectFile};
            PlcDocumentImportPolicy.ValidateOptions(r);
            var result=CreateDocumentImportContext(r).Run();
            if(result.RequiresSessionReset)documentImportOutcomeUnknown=true;
            return result;
        }
        private PlcDocumentImportContext CreateDocumentImportContext(PlcDocumentImportRequest r)
        {
            if(documentImportOutcomeUnknown)throw new InvalidOperationException("Prior document import outcome unknown; inspect before a new explicit session. Never replay.");
            PlcDocumentImportPolicy.ValidateOptions(r);
#if PLC_DOCUMENT_EXPORT
            var softwarePath=r.Software;var groupPath=r.Group;var importPath=r.Directory;var fileNameWithoutExtension=r.Name;
            var selected=ReadSelection(softwarePath);
            if(selected.ExactPath!=softwarePath) throw new ArgumentException("Exact ordinary PLC softwarePath required.");
            PlcLifecyclePolicy.RequireLocalSessionExecution(lifecycle.IsLocalSession,false);
            var project=Project();r.Project=project.Path.FullName;r.ProcessId=lifecycle.ProcessId??throw new InvalidOperationException("Explicit process identity required.");
            var target=BatchGroup(BlockGroups(selected.Value.BlockGroup),groupPath);
            r.TargetIdentity=string.Join(":",new[]{project,(object)selected.Value,selected.Context,(object)target}.Select(documentImportIdentities.Get));
            var names=new List<string>();
            IEnumerable<string> Inventory()
            {
                names.Clear();
                foreach(var group in BatchGroupsBounded(BlockGroups(selected.Value.BlockGroup)))
                {
                    yield return PlcDocumentImportPolicy.InventoryItem(group.Path,"","group",documentImportIdentities.Get(group.Value));
                    foreach(var block in group.Value.Blocks)
                    {
                        if(string.IsNullOrEmpty(block.Name) || !object.Equals(block.Parent,group.Value)) throw new InvalidOperationException("Ordinary block inventory ownership missing.");
                        names.Add(block.Name);
                        yield return PlcDocumentImportPolicy.InventoryItem(group.Path,block.Name,block.GetType().Name,documentImportIdentities.Get(block));
                    }
                }
            }
            Action recheck=()=> {
                RequireProjectIdentity(r.Project);
                if(lifecycle.ProcessId!=r.ProcessId || !object.Equals(Project(),project))throw new InvalidOperationException("Project/process identity changed.");
                var fresh=ReadSelection(softwarePath);
                if(fresh.ExactPath!=selected.ExactPath || !object.Equals(fresh.Value,selected.Value) || !object.Equals(fresh.Context,selected.Context) || !object.Equals(BatchGroup(BlockGroups(fresh.Value.BlockGroup),groupPath),target))throw new InvalidOperationException("Exact PLC/group identity changed.");
                RequireTargetOffline(fresh);
            };
            return new PlcDocumentImportContext {Request=r,Scan=()=>PlcDocumentImportPolicy.Scan(r),Open=path=> {
                var file=new FileInfo(path);PlcDocumentImportPolicy.SafePath(file);
                return new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);
            },Inventory=Inventory,Collision=name=>names.Any(n=>string.Equals(n,name,StringComparison.OrdinalIgnoreCase)),Recheck=recheck,Import=()=> {
                // Exactly one typed native call. Exact V20/V21 SDK XML: None = "Throw if exists".
                // It does NOT promise atomicity, normalization, rollback or race-free preflight.
                var native=target.Blocks.ImportFromDocuments(new DirectoryInfo(importPath),fileNameWithoutExtension,ImportDocumentOptions.None);
                if(native==null) return null!;
                var outcome=new PlcDocumentImportNative();
                try
                {
                    outcome.State=native.State.ToString();
                    var messages=native.Messages.Select(m=>m.Message??"").Take(257).ToArray();
                    outcome.Messages=messages.Take(256).Select(m=>m.Length<=4096?m:m.Substring(0,4096)).ToArray();
                    if(messages.Length>256 || messages.Any(m=>m.Length>4096)) return outcome;
                    // Association has no Find: enumerate the exact typed returned association (bounded).
                    var imported=native.ImportedPlcBlocks.Take(2).ToArray();
                    outcome.Identities=imported.Select(b=>PlcDocumentImportPolicy.InventoryItem(object.Equals(b.Parent,target)?groupPath:"<unverified-parent>",b.Name,b.GetType().Name,"returned")).ToArray();
                    if(native.State!=DocumentResultState.Success || imported.Length!=1 || imported[0].Name!=fileNameWithoutExtension || imported[0].GetType().Name!="GlobalDB" || !object.Equals(imported[0].Parent,target))return outcome;
                    var found=target.Blocks.Find(fileNameWithoutExtension);
                    outcome.ExistsVerified=found!=null && found.Name==fileNameWithoutExtension && found.GetType().Name=="GlobalDB" && object.Equals(found.Parent,target) && object.Equals(found,imported[0]);
                    outcome.Success=true;
                }
                catch(Exception) /* swallow(native-fallback): failed inspection of the native import result leaves Success false for the policy to report uncertainty */ {outcome.Success=false;}
                return outcome;
            }};
#else
            throw new NotSupportedException("Exact V20/V21 document SDK support is not compiled for this release.");
#endif
        }
    }
}
