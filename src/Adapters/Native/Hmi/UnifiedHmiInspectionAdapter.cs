using System;
using System.Collections.Generic;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Native.Plc;
using System.Linq;

namespace TiaMcp.Adapters.Native.Hmi
{
    internal sealed class UnifiedHmiInspectionAdapter
    {
        private readonly IUnifiedHmiInspectionSession _session;

        public UnifiedHmiInspectionAdapter(IUnifiedHmiInspectionSession session) => _session = session;

        private object ExactHmiItem(string softwarePath,string screenPath,string itemName)
        {
            var screen=HmiExactAccess.Screen(_session.ResolveHmiSoftwareOrThrow(softwarePath),screenPath);
            return HmiExactAccess.Named(HmiExactAccess.Get(screen,"ScreenItems")
                ?? throw new PlcSoftwareException("NotFound","ScreenItems unavailable."),itemName);
        }
        public HmiMessage ReadUnifiedHmiButtonEvent(string softwarePath,string screenPath,string buttonName,string eventType)
            => _session.RunHmiStepTool("GetUnifiedHmiButtonEvent",meta=>{
                var handler=HmiExactAccess.Event(ExactHmiItem(softwarePath,screenPath,buttonName),eventType);
                var data=HmiExactAccess.EventDto(handler);
                meta["event"]=data;
                meta["token"]=HmiExactAccess.Token(softwarePath+":"+screenPath+":"+buttonName+":"+eventType,data);
                meta["exists"]=true;
                return "Existing event read without creating or changing it.";
            });

        public HmiMessage DeleteUnifiedHmiButtonEvent(string softwarePath,string screenPath,string buttonName,string eventType,bool dryRun=true,string expectedToken="",bool onlyIfEmpty=true)
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
                if(onlyIfEmpty&&!empty) throw new PlcSoftwareException("InvalidState","Event contains script code; deletion refused by onlyIfEmpty.");
                if(dryRun)return "Preview only; event has not been deleted.";
                HmiExactAccess.RequireToken(expectedToken,token);
                meta["mayHaveChanged"]=true;
                HmiExactAccess.Delete(handler);
                try {HmiExactAccess.Event(button,eventType);throw new InvalidOperationException("Event still exists after Delete.");}
                catch(PlcSoftwareException ex) when(ex.Code=="NotFound") {meta["exists"]=false;}
                meta["persistence"]="Not saved; only the selected event was deleted.";
                return "Selected event deleted and absence verified.";
            });

        private static object ExactDynamization(object item,string propertyName)
        {
            var list=HmiExactAccess.Items(HmiExactAccess.Get(item,"Dynamizations"));
            var matches=list.Where(x=>string.Equals(HmiExactAccess.Get(x,"PropertyName") as string,propertyName,StringComparison.Ordinal)).ToList();
            if(matches.Count!=1)throw new PlcSoftwareException(matches.Count==0?"NotFound":"InvalidParams",
                "Expected exactly one dynamization for PropertyName="+propertyName+"; matches="+matches.Count);
            return matches[0];
        }
        public HmiMessage ReadUnifiedHmiDynamization(string softwarePath,string screenPath,string itemName,string propertyName)
            => _session.RunHmiStepTool("GetUnifiedHmiDynamization",meta=>{
                var dyn=ExactDynamization(ExactHmiItem(softwarePath,screenPath,itemName),propertyName);
                var data=HmiSnapshot.Capture(dyn,8,2000);
                meta["dynamization"]=data;
                meta["token"]=HmiExactAccess.Token(softwarePath+":"+screenPath+":"+itemName+":"+propertyName,data);
                return "Selected property dynamization read; coverage is recorded in the snapshot.";
            });
        public HmiMessage DeleteUnifiedHmiDynamization(string softwarePath,string screenPath,string itemName,string propertyName,bool dryRun=true,string expectedToken="")
            => _session.RunHmiStepTool("DeleteUnifiedHmiDynamization",meta=>{
                using var exclusive = dryRun ? null : _session.AcquireHmiEditAccess();
                var item=ExactHmiItem(softwarePath,screenPath,itemName);
                var dyn=ExactDynamization(item,propertyName);
                var data=HmiSnapshot.Capture(dyn,8,2000);
                var token=HmiExactAccess.Token(softwarePath+":"+screenPath+":"+itemName+":"+propertyName,data);
                meta["before"]=data;meta["token"]=token;meta["dryRun"]=dryRun;meta["mayHaveChanged"]=false;
                if(dryRun)return "Preview only; dynamization has not been deleted.";
                if(data["incomplete"]!.GetValue<bool>())throw new PlcSoftwareException("InvalidState","Cannot delete while the current dynamization snapshot is incomplete.");
                HmiExactAccess.RequireToken(expectedToken,token);
                meta["mayHaveChanged"]=true;HmiExactAccess.Delete(dyn);
                try {ExactDynamization(item,propertyName);throw new InvalidOperationException("Dynamization still exists after Delete.");}
                catch(PlcSoftwareException ex) when(ex.Code=="NotFound") {meta["exists"]=false;}
                meta["persistence"]="Not saved; other properties and events were not targeted.";
                return "Selected property dynamization deleted and absence verified.";
            });
        public HmiMessage DeleteEmptyUnifiedHmiScreenGroup(string softwarePath,string groupPath,bool dryRun=true)
            => _session.RunHmiStepTool("DeleteEmptyUnifiedHmiScreenGroup",meta=>{
                using var exclusive = dryRun ? null : _session.AcquireHmiEditAccess();
                var group=HmiExactAccess.Group(_session.ResolveHmiSoftwareOrThrow(softwarePath),groupPath);
                var screens=HmiExactAccess.Items(HmiExactAccess.Get(group,"Screens"));
                var groups=HmiExactAccess.Items(HmiExactAccess.Get(group,"Groups"));
                meta["screens"]=new HmiArray(screens.Select(x=>HmiValues.Value(HmiExactAccess.Get(x,"Name")?.ToString())).ToArray());
                meta["groups"]=new HmiArray(groups.Select(x=>HmiValues.Value(HmiExactAccess.Get(x,"Name")?.ToString())).ToArray());
                meta["dryRun"]=dryRun;meta["mayHaveChanged"]=false;
                if(screens.Count!=0||groups.Count!=0)throw new PlcSoftwareException("InvalidState","Group is not empty; recursive deletion is not supported.");
                if(dryRun)return "Empty group preview; nothing deleted.";
                meta["mayHaveChanged"]=true;HmiExactAccess.Delete(group);
                try {HmiExactAccess.Group(_session.ResolveHmiSoftwareOrThrow(softwarePath),groupPath);throw new InvalidOperationException("Group still exists after Delete.");}
                catch(PlcSoftwareException ex) when(ex.Code=="NotFound") {meta["exists"]=false;}
                meta["persistence"]="Not saved.";return "Empty group deleted and absence verified.";
            });


    }
}
