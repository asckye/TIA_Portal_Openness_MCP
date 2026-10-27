#if OPTIONAL_V20_PLUS
using System;
using System.Collections;
using System.Linq;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Hardware;
using TiaMcp.Adapters.Native.Hmi;
using EngineeringGroupOperations = TiaMcp.Adapters.Native.Plc.PlcGroupOperations;
using EngineeringScalarProperties = TiaMcp.Adapters.Native.Optional.OptionalScalars;
using EngineeringObjectAddress = TiaMcp.Adapters.Native.Optional.OptionalObjectAddress;
using NativeFileOutput = TiaMcp.Adapters.Native.Safety.SafetyFileOutput;
using PlcBlockServicesLogic = TiaMcp.Adapters.Native.Safety.SafetyPrimitives;
using HardwareServicesLogic = TiaMcp.Adapters.Native.Optional.OptionalRules;
using ArgumentRules = TiaMcp.Adapters.Native.Optional.OptionalRules;
using EngineeringPath = TiaMcp.Adapters.Hardware.HardwarePath;
using TiaMcpServer.ModelContextProtocol;
namespace TiaMcp.Adapters.Native.Optional
{
    internal sealed class OptionalEngineeringAdapter
    {
        private readonly OptionalSession _session;

        public OptionalEngineeringAdapter(OptionalSession session) => _session = session;

        public HardwareAddressingReply ReadSiVArcRules(string category,string objectPathJson="[]",int offset=0,int limit=100)
            =>_session.RunHmiStepTool("ListSivarcRules",meta=>{
                EngineeringObjectAddress.Parse(objectPathJson);
                try
                {
                if(offset<0||limit<1||limit>500)throw new ArgumentException("Invalid pagination.");
                var target=EngineeringObjectAddress.Resolve(_session.ExactSiVArcRoot(category),objectPathJson);
                var items=target is IEnumerable&&target is not string ? EngineeringGroupOperations.Items(target).ToArray() : new[]{target};
                var rows=items.Skip(offset).Take(limit).Select(x=>(object)EngineeringObjectAddress.Read(x)).ToArray();
                meta["records"]=new HmiArray(rows);meta["expectedCount"]=items.Length;meta["actualCount"]=rows.Length;
                meta["nextOffset"]=offset+rows.Length<items.Length ? offset+rows.Length : (int?)null;meta["truncated"]=offset+rows.Length<items.Length;meta["dataComplete"]=false;
                return "SiVArc rule scalar properties and schema read; exact child paths required for complex collections. No generation performed.";
                }
                catch (Exception ex) { OptionalEvidence.RecordFailure(meta, ex); throw; }
            });
        public HardwareAddressingReply ManageSiVArcRule(string category,string collectionPathJson,string name,string action,string propertiesJson="{}",bool dryRun=true)
            =>_session.RunHmiStepTool("ManageSivarcRule",meta=>{
                EngineeringObjectAddress.Parse(collectionPathJson);
                var changes=OptionalInputs.Parse(propertiesJson) as HmiObject ?? throw new ArgumentException("Expected properties object.");
                if(changes.ContainsKey("Name")||(action=="delete"&&changes.Count>0))throw new ArgumentException("No renaming or changes during delete.");
                try
                {
                if(!new[]{"create","update","delete"}.Contains(action)||string.IsNullOrWhiteSpace(name))throw new ArgumentException("Exact name and create/update/delete required.");
                using var access=dryRun ? null : _session.AcquireHmiEditAccess();
                var collection=EngineeringObjectAddress.Resolve(_session.ExactSiVArcRoot(category),collectionPathJson);
                if(collection.GetType().Namespace!="Siemens.Engineering.SiVArc"||!collection.GetType().Name.EndsWith("Composition",StringComparison.Ordinal))throw new ArgumentException("Select a native SiVArc rule/folder/table collection.");
                var target=EngineeringGroupOperations.Find(collection,name);
                if((action=="create")== (target!=null))throw new InvalidOperationException(action=="create" ? "Object exists." : "Exact object not found.");
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
                    // TIA V21, 2026-09-19 (docs/reference/real-machine-ledger.md): the composition proxy used for Find/Delete
                    // is stale afterwards; enumerating it throws EngineeringObjectDisposedException although the rule / group
                    // is gone. Re-resolve the collection from the typed anchor before verifying.
                    var fresh=EngineeringObjectAddress.Resolve(_session.ExactSiVArcRoot(category),collectionPathJson);
                    if(EngineeringGroupOperations.Find(fresh,name)!=null)throw new InvalidOperationException("Rule remains after deletion.");meta["verifiedAbsent"]=true;meta["verifiedOnFreshNavigation"]=true;
                }else {EngineeringScalarProperties.Apply(target!,prepared,meta);meta["after"]=EngineeringObjectAddress.Read(target!);}
                return "SiVArc native rule operation completed; no generation, save, compile or download.";
                }
                catch (Exception ex) { OptionalEvidence.RecordFailure(meta, ex); throw; }
            });
    }
}

#endif
