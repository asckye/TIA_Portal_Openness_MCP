using System;
using System.Collections;
using System.Linq;
using System.Text.Json.Nodes;
using Siemens.Engineering.HmiUnified.Cpm;
using TiaMcpServer.ModelContextProtocol;
namespace TiaMcpServer.Siemens
{
    public partial class Portal
    {
        private object ExactPlantViews()
        {
#if TIA_V20
            return _project!.PlantViews;
#else
            return _project!.GetService<PlantViewsProvider>()?.PlantViews
            ?? throw new NotSupportedException("Project does not expose PlantViewsProvider; check installed Unified/CPM capability.");
#endif
        }
        private object ExactPlantNode(string path)
        {
            var parts=EngineeringGroupOperations.Parts(path);
            object current=EngineeringGroupOperations.Find(ExactPlantViews(),parts[0]) ?? throw new InvalidOperationException("Exact plant view not found.");
            foreach(var part in parts.Skip(1)) current=EngineeringGroupOperations.Find(EngineeringGroupOperations.Get(current,"PlantViewNodes"),part) ?? throw new InvalidOperationException("Exact plant node not found: "+part);
            return current;
        }
        public ResponseMessage ReadUnifiedPlantObject(string plantPath="", string objectPathJson="[]", int offset=0, int limit=100)
            => RunHmiStepTool("ReadUnifiedPlantObject", meta => {
                if(offset<0 || limit<1 || limit>500) throw new ArgumentException("offset >= 0, limit 1..500 required.");
                var root=string.IsNullOrEmpty(plantPath) ? ExactPlantViews() : ExactPlantNode(plantPath);
                var target=EngineeringObjectAddress.Resolve(root,objectPathJson);
                var items=target is IEnumerable && target is not string ? EngineeringGroupOperations.Items(target).ToArray() : new[]{target};
                var rows=new JsonArray(items.Skip(offset).Take(limit).Select(x=>(JsonNode)EngineeringObjectAddress.Read(x)).ToArray());
                meta["plantPath"]=plantPath; meta["objectPath"]=EngineeringObjectAddress.Parse(objectPathJson);
                meta["records"]=rows; meta["expectedCount"]=items.Length; meta["actualCount"]=rows.Count;
                meta["nextOffset"]=offset+rows.Count<items.Length ? offset+rows.Count : (int?)null;
                meta["truncated"]=offset+rows.Count<items.Length; meta["apiCallSuccess"]=true; meta["dataComplete"]=false;
                meta["scope"]="Live page, scalar values and schema only. PlantObject/PlantObjectInterfaces and members require exact subsequent property paths; no implicit recursion.";
                return "Project plant view/CPM object read in bounded scalar scope.";
            });
        public ResponseMessage ManageUnifiedPlantNode(string plantPath, string action, string plantObjectType="", string propertiesJson="{}", bool dryRun=true)
            => RunHmiStepTool("ManageUnifiedPlantNode", meta => {
                if(!new[]{"create","update","delete"}.Contains(action)) throw new ArgumentException("action must be create/update/delete.");
                var parts=EngineeringGroupOperations.Parts(plantPath);
                using var access=dryRun ? null : AcquireHmiEditAccess();
                var collection=parts.Length==1 ? ExactPlantViews() : EngineeringGroupOperations.Get(ExactPlantNode(string.Join("/",parts.Take(parts.Length-1))),"PlantViewNodes");
                var target=EngineeringGroupOperations.Find(collection,parts.Last());
                if((action=="create")== (target!=null)) throw new InvalidOperationException(action=="create" ? "Plant object exists." : "Exact plant object not found.");
                if(plantObjectType!="" && (parts.Length==1 || action!="create")) throw new ArgumentException("plantObjectType only applies to creating a child CPM instance.");
                var changes=JsonNode.Parse(propertiesJson) as JsonObject ?? throw new ArgumentException("propertiesJson must be an object.");
                if(changes.ContainsKey("Name") || (action=="delete" && changes.Count!=0)) throw new ArgumentException("Rename and properties during delete are excluded.");
                if(action=="delete" && EngineeringGroupOperations.Items(EngineeringGroupOperations.Get(target!,"PlantViewNodes")).Any()) throw new InvalidOperationException("Only leaf nodes or empty plant views can be deleted; subtree deletion refused.");
                var signature=plantObjectType=="" ? new[]{typeof(string)} : new[]{typeof(string),typeof(string)};
                var type=target?.GetType() ?? collection.GetType().GetMethod("Create",signature)?.ReturnType ?? throw new NotSupportedException("Native plant creation unavailable.");
                var prepared=EngineeringScalarProperties.Prepare(type,changes);
                meta["plantPath"]=plantPath; meta["plantObjectType"]=plantObjectType; meta["dryRun"]=dryRun; meta["mayHaveChanged"]=false;
                if(target!=null) meta["before"]=EngineeringObjectAddress.Read(target);
                if(dryRun) return "Plant view/node preview. Native CPM type compatibility is not validated until execution.";
                meta["mayHaveChanged"]=true;
                if(action=="create") target=plantObjectType=="" ? EngineeringGroupOperations.Call(collection,"Create",signature,parts.Last()) : EngineeringGroupOperations.Call(collection,"Create",signature,parts.Last(),plantObjectType);
                if(action=="delete") {
                    EngineeringGroupOperations.Call(target!,"Delete",Type.EmptyTypes);
                    if(EngineeringGroupOperations.Find(collection,parts.Last())!=null) throw new InvalidOperationException("Plant object remains after delete.");
                    meta["verifiedAbsent"]=true;
                } else { EngineeringScalarProperties.Apply(target!,prepared,meta); meta["after"]=EngineeringObjectAddress.Read(target!); }
                return "Plant view/node operation completed. No save/compile/download; referenced CPM semantics require separate verification.";
            });
        public ResponseMessage UpdateUnifiedPlantObject(string plantPath, string objectPathJson, string propertiesJson, bool dryRun=true)
            => RunHmiStepTool("UpdateUnifiedPlantObject", meta => {
                using var access=dryRun ? null : AcquireHmiEditAccess();
                var target=EngineeringObjectAddress.Resolve(ExactPlantNode(plantPath),objectPathJson);
                var changes=JsonNode.Parse(propertiesJson) as JsonObject ?? throw new ArgumentException("propertiesJson must be an object.");
                if(changes.Count==0 || changes.ContainsKey("Name")) throw new ArgumentException("Nonempty properties required; renaming excluded.");
                var prepared=EngineeringScalarProperties.Prepare(target.GetType(),changes);
                meta["dryRun"]=dryRun; meta["mayHaveChanged"]=false; meta["plantPath"]=plantPath; meta["objectPath"]=EngineeringObjectAddress.Parse(objectPathJson);
                meta["before"]=EngineeringObjectAddress.Read(target);
                if(!dryRun) {EngineeringScalarProperties.Apply(target,prepared,meta);meta["after"]=EngineeringObjectAddress.Read(target);}
                return dryRun ? "Exact CPM scalar edit preview." : "CPM scalar properties changed and read back; no save/compile/download.";
            });
    }
}
