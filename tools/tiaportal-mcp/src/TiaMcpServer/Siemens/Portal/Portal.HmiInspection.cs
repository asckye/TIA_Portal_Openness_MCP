using System;
using System.Linq;
using System.Text.Json.Nodes;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcpServer.Siemens
{
    public partial class Portal
    {
        public ResponseMessage ArchiveSavedProject(string archivePath,bool dryRun=true)
            => RunHmiStepTool("ArchiveSavedProject",meta=>{
                using var exclusive = dryRun ? null : AcquireHmiEditAccess();
                var result=ProjectArchive.Create(CurrentProject!,archivePath,dryRun);
                foreach(var item in result)meta[item.Key]=item.Value?.DeepClone();
                meta["operationSuccess"]=result["success"]?.DeepClone();
                return dryRun ? "Native project archive preview; no file written." : "Native archive operation completed; inspect success and validation. Retrieval is not tested.";
            });
        private object ExactHmiItem(string softwarePath,string screenPath,string itemName)
        {
            var screen=HmiExactAccess.Screen(ResolveHmiSoftwareOrThrow(softwarePath),screenPath);
            return HmiExactAccess.Named(HmiExactAccess.Get(screen,"ScreenItems")
                ?? throw new PortalException(PortalErrorCode.NotFound,"ScreenItems unavailable."),itemName);
        }
        public ResponseMessage ReadUnifiedHmiButtonEvent(string softwarePath,string screenPath,string buttonName,string eventType)
            => RunHmiStepTool("ReadUnifiedHmiButtonEvent",meta=>{
                var handler=HmiExactAccess.Event(ExactHmiItem(softwarePath,screenPath,buttonName),eventType);
                var data=HmiExactAccess.EventDto(handler);
                meta["event"]=data;
                meta["token"]=HmiExactAccess.Token(softwarePath+":"+screenPath+":"+buttonName+":"+eventType,data);
                meta["exists"]=true;
                return "Existing event read without creating or changing it.";
            });

        public ResponseMessage DeleteUnifiedHmiButtonEvent(string softwarePath,string screenPath,string buttonName,string eventType,bool dryRun=true,string expectedToken="",bool onlyIfEmpty=true)
            => RunHmiStepTool("DeleteUnifiedHmiButtonEvent",meta=>{
                using var exclusive = dryRun ? null : AcquireHmiEditAccess();
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
            => RunHmiStepTool("ReadUnifiedHmiDynamization",meta=>{
                var dyn=ExactDynamization(ExactHmiItem(softwarePath,screenPath,itemName),propertyName);
                var data=HmiSnapshot.Capture(dyn,8,2000);
                meta["dynamization"]=data;
                meta["token"]=HmiExactAccess.Token(softwarePath+":"+screenPath+":"+itemName+":"+propertyName,data);
                return "Selected property dynamization read; coverage is recorded in the snapshot.";
            });
        public ResponseMessage DeleteUnifiedHmiDynamization(string softwarePath,string screenPath,string itemName,string propertyName,bool dryRun=true,string expectedToken="")
            => RunHmiStepTool("DeleteUnifiedHmiDynamization",meta=>{
                using var exclusive = dryRun ? null : AcquireHmiEditAccess();
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
            => RunHmiStepTool("DeleteEmptyUnifiedHmiScreenGroup",meta=>{
                using var exclusive = dryRun ? null : AcquireHmiEditAccess();
                var group=HmiExactAccess.Group(ResolveHmiSoftwareOrThrow(softwarePath),groupPath);
                var screens=HmiExactAccess.Items(HmiExactAccess.Get(group,"Screens"));
                var groups=HmiExactAccess.Items(HmiExactAccess.Get(group,"Groups"));
                meta["screens"]=new JsonArray(screens.Select(x=>JsonValue.Create(HmiExactAccess.Get(x,"Name")?.ToString())).ToArray());
                meta["groups"]=new JsonArray(groups.Select(x=>JsonValue.Create(HmiExactAccess.Get(x,"Name")?.ToString())).ToArray());
                meta["dryRun"]=dryRun;meta["mayHaveChanged"]=false;
                if(screens.Count!=0||groups.Count!=0)throw new PortalException(PortalErrorCode.InvalidState,"Group is not empty; recursive deletion is not supported.");
                if(dryRun)return "Empty group preview; nothing deleted.";
                meta["mayHaveChanged"]=true;HmiExactAccess.Delete(group);
                try {HmiExactAccess.Group(ResolveHmiSoftwareOrThrow(softwarePath),groupPath);throw new InvalidOperationException("Group still exists after Delete.");}
                catch(PortalException ex) when(ex.Code==PortalErrorCode.NotFound) {meta["exists"]=false;}
                meta["persistence"]="Not saved.";return "Empty group deleted and absence verified.";
            });
        public ResponseMessage ReadHmiScreenSnapshot(string softwarePath,string screenPath,int maxDepth=6,int maxNodes=2000)
            => RunHmiStepTool("ReadHmiScreenSnapshot",meta=>{
                meta["softwarePath"]=softwarePath;meta["screenPath"]=screenPath;
                meta["readOnly"] = true;
                var trace = new HmiReadTrace();
                trace.Step("start", softwarePath + ":" + screenPath);
                try
                {
                    trace.Step("before", "resolveSoftware:" + softwarePath);
                    var software = ResolveHmiSoftwareOrThrow(softwarePath);
                    trace.Step("after", "resolveSoftware:" + softwarePath);
                    trace.Step("before", "resolveScreen:" + screenPath);
                    var screen = HmiExactAccess.Screen(software, screenPath);
                    trace.Step("after", "resolveScreen:" + screenPath);
                    var snapshot = HmiSnapshot.Capture(screen, maxDepth, maxNodes, 65536, trace.Step);
                    meta["snapshot"] = snapshot;
                    foreach (var key in new[] { "apiCallSuccess", "dataComplete", "traversalComplete", "truncated", "readFailureCount", "quarantinedCount", "connectionUnavailable", "expectedCount", "actualCount", "nextCursor" })
                        meta[key] = snapshot[key]?.DeepClone();
                    meta["operationSuccess"] = snapshot["apiCallSuccess"]?.DeepClone();
                    trace.AddTo(meta);
                    if (snapshot["connectionUnavailable"]!.GetValue<bool>())
                    {
                        meta["failurePhase"] = trace.Phase;
                        meta["failurePath"] = snapshot["failurePath"]?.DeepClone();
                        RecordHmiReadFault(meta);
                    }
                    trace.Step("complete", "apiCallSuccess=" + snapshot["apiCallSuccess"] + ";dataComplete=" + snapshot["dataComplete"]);
                    return snapshot["connectionUnavailable"]!.GetValue<bool>()
                        ? "HMI connection became unavailable; partial evidence retained and further snapshot reads blocked. No automatic retry or rebind."
                        : "Read-only screen snapshot; inspect dataComplete, quarantine and coverage. This is not an importable native backup.";
                }
                catch (Exception ex) { trace.Step("failed", trace.LastAttemptedPath ?? screenPath, ex); throw; }
                finally { trace.AddTo(meta); }
            });
        public ResponseMessage ListHmiScreenPaths(string softwarePath,int offset=0,int limit=100)
            => RunHmiStepTool("ListHmiScreenPaths",meta=>{
                if(offset<0||limit<1||limit>500)throw new PortalException(PortalErrorCode.InvalidParams,"offset >= 0 and limit 1..500 required.");
                var screens=HmiExactAccess.Screens(ResolveHmiSoftwareOrThrow(softwarePath));
                meta["total"]=screens.Count;meta["offset"]=offset;
                meta["paths"]=new JsonArray(screens.Skip(offset).Take(limit).Select(x=>JsonValue.Create(x.Path)).ToArray());
                meta["nextOffset"]=offset+limit<screens.Count?(JsonNode?)JsonValue.Create(offset+limit):null;
                return "Complete group paths, with URI-escaped name segments; leading / selects an exact path.";
            });
    }
}
