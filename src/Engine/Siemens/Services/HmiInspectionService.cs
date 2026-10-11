using System;
using System.Linq;
using System.Text.Json.Nodes;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcpServer.Siemens.Services
{
    internal sealed class HmiInspectionService
    {
        private readonly IHmiToolSession _session;

        public HmiInspectionService(IHmiToolSession session) => _session = session;

        public ResponseMessage ArchiveSavedProject(string archivePath,bool dryRun=true)
            => _session.RunHmiStepTool("ArchiveSavedProject",meta=>{
                using var exclusive = dryRun ? null : _session.AcquireHmiEditAccess();
                var result=ProjectArchive.Create(_session.CurrentProject!,archivePath,dryRun);
                foreach(var item in result)meta[item.Key]=item.Value?.DeepClone();
                meta["operationSuccess"]=result["success"]?.DeepClone();
                return dryRun ? "Native project archive preview; no file written." : "Native archive operation completed; inspect success and validation. Retrieval is not tested.";
            });
        private object ExactHmiItem(string softwarePath,string screenPath,string itemName)
        {
            var screen=HmiExactAccess.Screen(_session.ResolveHmiSoftwareOrThrow(softwarePath),screenPath);
            return HmiExactAccess.Named(HmiExactAccess.Get(screen,"ScreenItems")
                ?? throw new PortalException(PortalErrorCode.NotFound,"ScreenItems unavailable."),itemName);
        }
        public ResponseMessage ReadUnifiedHmiButtonEvent(string softwarePath,string screenPath,string buttonName,string eventType)
            => _session.RunHmiStepTool("GetUnifiedHmiButtonEvent",meta=>{
                var handler=HmiExactAccess.Event(ExactHmiItem(softwarePath,screenPath,buttonName),eventType);
                var data=HmiExactAccess.EventDto(handler);
                meta["event"]=data;
                meta["token"]=HmiExactAccess.Token(softwarePath+":"+screenPath+":"+buttonName+":"+eventType,data);
                meta["exists"]=true;
                return "Existing event read without creating or changing it.";
            });

        public ResponseMessage DeleteUnifiedHmiButtonEvent(string softwarePath,string screenPath,string buttonName,string eventType,bool dryRun=true,string expectedToken="",bool onlyIfEmpty=true)
            => _session.RunHmiStepTool("DeleteUnifiedHmiButtonEvent",meta=>{
                using var exclusive = dryRun ? null : _session.AcquireHmiEditAccess();
                var button=ExactHmiItem(softwarePath,screenPath,buttonName);
                var handler=HmiExactAccess.Event(button,eventType);
                var data=HmiExactAccess.EventDto(handler);
                var scope=softwarePath+":"+screenPath+":"+buttonName+":"+eventType;
                var token=HmiExactAccess.Token(scope,data);
                meta["before"]=data;meta["token"]=token;meta["dryRun"]=dryRun;meta["mayHaveChanged"]=false;
                var empty=!data["scriptExists"]!.GetValue<bool>() || (data["ScriptCode"]?.ToString()=="" && data["GlobalDefinitionAreaScriptCode"]?.ToString()=="");
                meta["empty"]=empty;
                if(onlyIfEmpty&&!empty) throw new PortalException(PortalErrorCode.InvalidState,"Event contains script code; deletion refused by onlyIfEmpty.");
                if(dryRun)return "Preview only; event has not been deleted.";
                HmiExactAccess.RequireToken(expectedToken,token);
                meta["mayHaveChanged"]=true;
                HmiExactAccess.Delete(handler);
                try {HmiExactAccess.Event(button,eventType);throw new InvalidOperationException("Event still exists after Delete.");}
                catch(PortalException ex) when(ex.Code==PortalErrorCode.NotFound) {meta["exists"]=false;}
                meta["persistence"]="Not saved; only the selected event was deleted.";
                return "Selected event deleted and absence verified.";
            });

        private static object ExactDynamization(object item,string propertyName)
        {
            var list=HmiExactAccess.Items(HmiExactAccess.Get(item,"Dynamizations"));
            var matches=list.Where(x=>string.Equals(HmiExactAccess.Get(x,"PropertyName") as string,propertyName,StringComparison.Ordinal)).ToList();
            if(matches.Count!=1)throw new PortalException(matches.Count==0?PortalErrorCode.NotFound:PortalErrorCode.InvalidParams,
                "Expected exactly one dynamization for PropertyName="+propertyName+"; matches="+matches.Count);
            return matches[0];
        }
        public ResponseMessage ReadUnifiedHmiDynamization(string softwarePath,string screenPath,string itemName,string propertyName)
            => _session.RunHmiStepTool("GetUnifiedHmiDynamization",meta=>{
                var dyn=ExactDynamization(ExactHmiItem(softwarePath,screenPath,itemName),propertyName);
                var data=HmiSnapshot.Capture(dyn,8,2000);
                meta["dynamization"]=data;
                meta["token"]=HmiExactAccess.Token(softwarePath+":"+screenPath+":"+itemName+":"+propertyName,data);
                return "Selected property dynamization read; coverage is recorded in the snapshot.";
            });
        public ResponseMessage DeleteUnifiedHmiDynamization(string softwarePath,string screenPath,string itemName,string propertyName,bool dryRun=true,string expectedToken="")
            => _session.RunHmiStepTool("DeleteUnifiedHmiDynamization",meta=>{
                using var exclusive = dryRun ? null : _session.AcquireHmiEditAccess();
                var item=ExactHmiItem(softwarePath,screenPath,itemName);
                var dyn=ExactDynamization(item,propertyName);
                var data=HmiSnapshot.Capture(dyn,8,2000);
                var token=HmiExactAccess.Token(softwarePath+":"+screenPath+":"+itemName+":"+propertyName,data);
                meta["before"]=data;meta["token"]=token;meta["dryRun"]=dryRun;meta["mayHaveChanged"]=false;
                if(dryRun)return "Preview only; dynamization has not been deleted.";
                if(data["incomplete"]!.GetValue<bool>())throw new PortalException(PortalErrorCode.InvalidState,"Cannot delete while the current dynamization snapshot is incomplete.");
                HmiExactAccess.RequireToken(expectedToken,token);
                meta["mayHaveChanged"]=true;HmiExactAccess.Delete(dyn);
                try {ExactDynamization(item,propertyName);throw new InvalidOperationException("Dynamization still exists after Delete.");}
                catch(PortalException ex) when(ex.Code==PortalErrorCode.NotFound) {meta["exists"]=false;}
                meta["persistence"]="Not saved; other properties and events were not targeted.";
                return "Selected property dynamization deleted and absence verified.";
            });
        public ResponseMessage DeleteEmptyUnifiedHmiScreenGroup(string softwarePath,string groupPath,bool dryRun=true)
            => _session.RunHmiStepTool("DeleteEmptyUnifiedHmiScreenGroup",meta=>{
                using var exclusive = dryRun ? null : _session.AcquireHmiEditAccess();
                var group=HmiExactAccess.Group(_session.ResolveHmiSoftwareOrThrow(softwarePath),groupPath);
                var screens=HmiExactAccess.Items(HmiExactAccess.Get(group,"Screens"));
                var groups=HmiExactAccess.Items(HmiExactAccess.Get(group,"Groups"));
                meta["screens"]=new JsonArray(screens.Select(x=>JsonValue.Create(HmiExactAccess.Get(x,"Name")?.ToString())).ToArray());
                meta["groups"]=new JsonArray(groups.Select(x=>JsonValue.Create(HmiExactAccess.Get(x,"Name")?.ToString())).ToArray());
                meta["dryRun"]=dryRun;meta["mayHaveChanged"]=false;
                if(screens.Count!=0||groups.Count!=0)throw new PortalException(PortalErrorCode.InvalidState,"Group is not empty; recursive deletion is not supported.");
                if(dryRun)return "Empty group preview; nothing deleted.";
                meta["mayHaveChanged"]=true;HmiExactAccess.Delete(group);
                try {HmiExactAccess.Group(_session.ResolveHmiSoftwareOrThrow(softwarePath),groupPath);throw new InvalidOperationException("Group still exists after Delete.");}
                catch(PortalException ex) when(ex.Code==PortalErrorCode.NotFound) {meta["exists"]=false;}
                meta["persistence"]="Not saved.";return "Empty group deleted and absence verified.";
            });


    }
}
