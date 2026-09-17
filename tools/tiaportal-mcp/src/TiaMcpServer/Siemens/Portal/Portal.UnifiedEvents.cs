using System;
using System.Linq;
using System.Text.Json.Nodes;
using TiaMcpServer.ModelContextProtocol;
namespace TiaMcpServer.Siemens
{
    public partial class Portal
    {
        public ResponseMessage ManageUnifiedEvent(string softwarePath,string objectPathJson,string eventType,string action="read",string propertyName="",string scriptPropertiesJson="{}",string expectedToken="",bool dryRun=true)
            =>RunHmiStepTool("ManageUnifiedEvent",meta=>{
                if(!new[]{"read","create","update","delete"}.Contains(action))throw new ArgumentException("action must be read/create/update/delete.");
                if(string.IsNullOrWhiteSpace(eventType))throw new ArgumentException("Exact event type required.");
                bool write=action!="read"&&!dryRun;using var access=write ? AcquireHmiEditAccess() : null;
                var target=EngineeringObjectAddress.Resolve(ExactUnifiedRoot(softwarePath),objectPathJson);
                var handlers=EngineeringGroupOperations.Get(target,propertyName=="" ? "EventHandlers" : "PropertyEventHandlers");
                var handler=UnifiedEventOperations.Find(handlers,eventType,propertyName);
                var changes=JsonNode.Parse(scriptPropertiesJson) as JsonObject ?? throw new ArgumentException("Script properties must be an object.");
                if(changes.Any(x=>!new[]{"ScriptCode","GlobalDefinitionAreaScriptCode","Async"}.Contains(x.Key)))throw new ArgumentException("Only ScriptCode, GlobalDefinitionAreaScriptCode and Async may be edited.");
                if(action!="update"&&changes.Count!=0)throw new ArgumentException("Script edits require a separate update action.");
                if(action=="update"&&changes.Count==0)throw new ArgumentException("No script edits supplied.");
                var before=handler==null ? new JsonObject{["exists"]=false} : HmiExactAccess.EventDto(handler);
                var scope=softwarePath+"|"+EngineeringObjectAddress.Parse(objectPathJson).ToJsonString()+"|"+propertyName+"|"+eventType;
                var token=HmiExactAccess.Token(scope,before);
                meta["before"]=before;meta["token"]=token;meta["dryRun"]=dryRun;meta["mayHaveChanged"]=false;meta["exists"]=handler!=null;
                if(action=="read")return "Exact event/script read without creation; raw script text preserved.";
                if((action=="create")== (handler!=null))throw new InvalidOperationException(action=="create" ? "Event exists." : "Exact event not found.");
                var creation=action=="create" ? UnifiedEventOperations.Creation(handlers,eventType,propertyName) : default;
                var script=action=="update" ? EngineeringGroupOperations.Get(handler!,"Script") : null;
                var prepared=script==null ? null : EngineeringScalarProperties.Prepare(script.GetType(),changes);
                if(!write)return "Event preview. Execute with returned expectedToken; no script execution or syntax check.";
                HmiExactAccess.RequireToken(expectedToken,token);meta["mayHaveChanged"]=true;
                if(action=="create")handler=EngineeringGroupOperations.Call(handlers,"Create",creation.Types,creation.Values);
                else if(action=="delete")EngineeringGroupOperations.Call(handler!,"Delete",Type.EmptyTypes);
                else EngineeringScalarProperties.Apply(script!,prepared!,meta);
                var after=UnifiedEventOperations.Find(handlers,eventType,propertyName);
                if(action=="delete" ? after!=null : after==null)throw new InvalidOperationException("Event presence readback differs.");
                meta["exists"]=after!=null;if(after!=null)meta["after"]=HmiExactAccess.EventDto(after);
                return "Native event operation and readback completed. No script executed, project save/compile/download, or automatic Portal close.";
            });
    }
}
