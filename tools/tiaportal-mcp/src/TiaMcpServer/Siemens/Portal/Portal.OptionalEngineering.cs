using System;
using System.Collections;
using System.Linq;
using System.Text.Json.Nodes;
using TiaMcpServer.ModelContextProtocol;
namespace TiaMcpServer.Siemens
{
    public partial class Portal
    {
        private object ExactOfflineDrive(string devicePathJson,string itemPathJson,ushort driveObjectNumber)
        {
            var item=ExactEngineeringHardware(devicePathJson,itemPathJson);
            var container=OfficialServiceAccess.Require(item,"Siemens.Engineering.MC.Drives.DriveObjectContainer","Siemens.Engineering.Startdrive");
            var matches=EngineeringGroupOperations.Items(EngineeringGroupOperations.Get(container,"DriveObjects")).Where(x=>Convert.ToUInt16(EngineeringGroupOperations.Get(x,"DriveObjectNumber"))==driveObjectNumber).Take(2).ToArray();
            if(matches.Length!=1)throw new InvalidOperationException("Expected exactly one offline drive object number.");
            return matches[0];
        }
        public ResponseMessage ManageStartdriveParameter(string devicePathJson,string itemPathJson,ushort driveObjectNumber,string parameter,string action="read",string valueJson="null",bool dryRun=true)
            =>RunHmiStepTool("ManageStartdriveParameter",meta=>{
                if(action!="read"&&action!="write")throw new ArgumentException("action must be read/write.");
                if(string.IsNullOrWhiteSpace(parameter))throw new ArgumentException("Exact parameter name such as p1000[0] required.");
                bool write=action=="write"&&!dryRun;using var access=write ? AcquireHmiEditAccess() : null;
                var drive=ExactOfflineDrive(devicePathJson,itemPathJson,driveObjectNumber);
                var parameters=EngineeringGroupOperations.Get(drive,action=="write" ? "Parameters" : "ReadParameters");
                // Native Find supports indexed parameter syntax; no fallback or fuzzy matching.
                var target=EngineeringGroupOperations.Call(parameters,"Find",new[]{typeof(string)},parameter) ?? throw new InvalidOperationException("Exact drive parameter not found.");
                meta["before"]=EngineeringObjectAddress.Read(target);meta["dryRun"]=dryRun;meta["mayHaveChanged"]=false;meta["online"]=false;
                if(action=="read")return "Offline drive parameter read. No OnlineDriveObject or live device access used.";
                var current=EngineeringGroupOperations.Get(target,"Value");
                var value=EngineeringScalarProperties.ConvertValue(JsonNode.Parse(valueJson),current.GetType());
                var changes=EngineeringScalarProperties.Prepare(target.GetType(),new JsonObject{["Value"]=EngineeringScalarProperties.Json(value)});
                changes[0]=(changes[0].Property,value);
                if(!write)return "Offline drive parameter preview; native limits/semantics checked on execution.";
                EngineeringScalarProperties.Apply(target,changes,meta);meta["after"]=EngineeringObjectAddress.Read(target);
                return "Offline drive parameter changed and read back; no download, online parameter write or drive command.";
            });
        // 2.7.38: the anchor is the typed Sivarc project service (Portal.Sivarc.cs); the generic path-based readers below stay for arbitrary sub-paths.
        private object ExactSiVArcRoot(string category) => Family(category).Anchor(RequireSivarc());
        public ResponseMessage ReadSiVArcRules(string category,string objectPathJson="[]",int offset=0,int limit=100)
            =>RunHmiStepTool("ReadSiVArcRules",meta=>{
                if(offset<0||limit<1||limit>500)throw new ArgumentException("Invalid pagination.");
                var target=EngineeringObjectAddress.Resolve(ExactSiVArcRoot(category),objectPathJson);
                var items=target is IEnumerable&&target is not string ? EngineeringGroupOperations.Items(target).ToArray() : new[]{target};
                var rows=items.Skip(offset).Take(limit).Select(x=>(JsonNode)EngineeringObjectAddress.Read(x)).ToArray();
                meta["records"]=new JsonArray(rows);meta["expectedCount"]=items.Length;meta["actualCount"]=rows.Length;
                meta["nextOffset"]=offset+rows.Length<items.Length ? offset+rows.Length : (int?)null;meta["truncated"]=offset+rows.Length<items.Length;meta["dataComplete"]=false;
                return "SiVArc rule scalar properties and schema read; exact child paths required for complex collections. No generation performed.";
            });
        public ResponseMessage ManageSiVArcRule(string category,string collectionPathJson,string name,string action,string propertiesJson="{}",bool dryRun=true)
            =>RunHmiStepTool("ManageSiVArcRule",meta=>{
                if(!new[]{"create","update","delete"}.Contains(action)||string.IsNullOrWhiteSpace(name))throw new ArgumentException("Exact name and create/update/delete required.");
                using var access=dryRun ? null : AcquireHmiEditAccess();
                var collection=EngineeringObjectAddress.Resolve(ExactSiVArcRoot(category),collectionPathJson);
                if(collection.GetType().Namespace!="Siemens.Engineering.SiVArc"||!collection.GetType().Name.EndsWith("Composition",StringComparison.Ordinal))throw new ArgumentException("Select a native SiVArc rule/folder/table collection.");
                var target=EngineeringGroupOperations.Find(collection,name);
                if((action=="create")== (target!=null))throw new InvalidOperationException(action=="create" ? "Object exists." : "Exact object not found.");
                var changes=JsonNode.Parse(propertiesJson) as JsonObject ?? throw new ArgumentException("Expected properties object.");
                if(changes.ContainsKey("Name")||(action=="delete"&&changes.Count>0))throw new ArgumentException("No renaming or changes during delete.");
                if(action=="delete") foreach(var child in target!.GetType().GetProperties().Where(p=>p.GetIndexParameters().Length==0&&p.GetMethod?.IsPublic==true&&p.PropertyType.Name.EndsWith("Composition",StringComparison.Ordinal))) {
                    var children=child.GetValue(target);if(children!=null&&EngineeringGroupOperations.Items(children).Any())throw new InvalidOperationException("Nonempty rule container deletion refused: "+child.Name);
                }
                var type=target?.GetType() ?? collection.GetType().GetMethod("Create",new[]{typeof(string)})?.ReturnType ?? throw new NotSupportedException("Native Create(string) unavailable.");
                var prepared=EngineeringScalarProperties.Prepare(type,changes);meta["dryRun"]=dryRun;meta["mayHaveChanged"]=false;
                if(target!=null)meta["before"]=EngineeringObjectAddress.Read(target);
                if(dryRun)return "SiVArc rule edit preview; no generation performed.";
                meta["mayHaveChanged"]=true;
                if(action=="create")target=EngineeringGroupOperations.Call(collection,"Create",new[]{typeof(string)},name);
                if(action=="delete") {
                    EngineeringGroupOperations.Call(target!,"Delete",Type.EmptyTypes);
                    if(EngineeringGroupOperations.Find(collection,name)!=null)throw new InvalidOperationException("Rule remains after deletion.");meta["verifiedAbsent"]=true;
                }else {EngineeringScalarProperties.Apply(target!,prepared,meta);meta["after"]=EngineeringObjectAddress.Read(target!);}
                return "SiVArc native rule operation completed; no generation, save, compile or download.";
            });
    }
}
