using System;
using System.Linq;
using System.Text.Json.Nodes;
using Siemens.Engineering.SW.Tags;
using TiaMcpServer.ModelContextProtocol;
namespace TiaMcpServer.Siemens
{
    public partial class Portal
    {
        public ResponseMessage ManagePlcTagDefinition(string softwarePath, string tablePath, string name, string kind, string action, string dataType="", string addressOrValue="", string propertiesJson="{}", bool dryRun=true)
            => RunHmiStepTool("ManagePlcTagDefinition", meta => {
                if (kind!="tag" && kind!="constant") throw new ArgumentException("kind must be tag/constant.");
                if (!new[]{"read","create","update","delete"}.Contains(action)) throw new ArgumentException("Invalid action.");
                if(string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Exact name required.");
                bool writing=action!="read" && !dryRun;
                using var access=writing ? AcquireHmiEditAccess() : null;
                var plc=ExactPlcForEngineering(softwarePath,writing);
                var parts=EngineeringGroupOperations.Parts(tablePath);
                var group=EngineeringGroupOperations.Group(plc.TagTableGroup,string.Join("/",parts.Take(parts.Length-1)));
                var table=(PlcTagTable)(EngineeringGroupOperations.Find(EngineeringGroupOperations.Get(group,"TagTables"),parts.Last()) ?? throw new InvalidOperationException("Exact PLC tag table not found."));
                object collection=kind=="tag" ? (object)table.Tags : table.UserConstants;
                var target=EngineeringGroupOperations.Find(collection,name);
                if((action=="create")== (target!=null)) throw new InvalidOperationException(action=="create" ? "Definition exists." : "Exact definition not found.");
                var changes=JsonNode.Parse(propertiesJson) as JsonObject ?? throw new ArgumentException("propertiesJson must be an object.");
                if(changes.ContainsKey("Name")) throw new ArgumentException("Renaming is not supported by this tool.");
                if((action=="read" || action=="delete") && changes.Count!=0) throw new ArgumentException("No properties allowed for read/delete.");
                if(action=="create" && (string.IsNullOrWhiteSpace(dataType) || string.IsNullOrWhiteSpace(addressOrValue))) throw new ArgumentException("Creation requires explicit dataType and addressOrValue.");
                var nativeType=kind=="tag" ? typeof(PlcTag) : typeof(PlcUserConstant);
                var prepared=EngineeringScalarProperties.Prepare(nativeType,changes);
                meta["dryRun"]=dryRun; meta["mayHaveChanged"]=false; meta["tablePath"]=tablePath; meta["name"]=name; meta["kind"]=kind;
                if(target!=null) meta["before"]=EngineeringScalarProperties.Read(target);
                if(!writing) return "PLC tag/constant read or preview. Native field validation occurs on execution; no modification.";
                meta["mayHaveChanged"]=true;
                if(action=="create") target=kind=="tag" ? (object)table.Tags.Create(name,dataType,addressOrValue) : table.UserConstants.Create(name,dataType,addressOrValue);
                if(action=="delete") {
                    EngineeringGroupOperations.Call(target!,"Delete",Type.EmptyTypes);
                    if(EngineeringGroupOperations.Find(collection,name)!=null) throw new InvalidOperationException("Definition remains after delete.");
                    meta["verifiedAbsent"]=true;
                } else { EngineeringScalarProperties.Apply(target!,prepared,meta); meta["after"]=EngineeringScalarProperties.Read(target!); }
                return "Native PLC tag/constant operation completed and read back. No save/compile/download or runtime value write.";
            });
    }
}
