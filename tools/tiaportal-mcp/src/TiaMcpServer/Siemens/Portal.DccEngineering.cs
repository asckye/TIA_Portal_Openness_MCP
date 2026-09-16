using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using TiaMcpServer.ModelContextProtocol;
namespace TiaMcpServer.Siemens
{
    public partial class Portal
    {
        public ResponseMessage ManageDccChart(string devicePathJson,string itemPathJson,ushort driveObjectNumber,string chartName,string action,string filePath="",string importOptions="",string propertiesJson="{}",bool dryRun=true)
            =>RunHmiStepTool("ManageDccChart",meta=>{
                if(!new[]{"read","create","update","delete","export","import","optimizeSequence"}.Contains(action))throw new ArgumentException("Invalid DCC action.");
                if(string.IsNullOrWhiteSpace(chartName))throw new ArgumentException("Exact chart name required.");
                bool writing=action!="read"&&action!="export"&&!dryRun;using var access=writing ? AcquireHmiEditAccess() : null;
                var drive=ExactOfflineDrive(devicePathJson,itemPathJson,driveObjectNumber);
                var service=OfficialServiceAccess.Require(drive,"Siemens.Engineering.MC.Drives.Dcc.DriveControlChartContainer","Siemens.Engineering.DCC");
                var charts=EngineeringGroupOperations.Get(service,"Charts");var chart=EngineeringGroupOperations.Find(charts,chartName);
                if(action=="create"&&chart!=null)throw new InvalidOperationException("Chart exists.");
                if(action!="create"&&action!="import"&&chart==null)throw new InvalidOperationException("Exact chart not found.");
                var changes=JsonNode.Parse(propertiesJson) as JsonObject ?? throw new ArgumentException("Expected properties object.");
                if(changes.ContainsKey("Name")||((action!="create"&&action!="update")&&changes.Count>0))throw new ArgumentException("Renaming excluded; properties only on create/update.");
                var type=chart?.GetType() ?? charts.GetType().GetMethod("Create",new[]{typeof(string)})!.ReturnType;
                var prepared=EngineeringScalarProperties.Prepare(type,changes);
                FileInfo? file=null;System.Reflection.MethodInfo? import=null;object? mode=null;
                if(action=="export")file=NativeFileOutput.Plan(filePath);
                if(action=="import") {
                    file=new FileInfo(filePath);if(!file.Exists)throw new FileNotFoundException("DCC import file missing.");
                    import=charts.GetType().GetMethods().Single(m=>m.Name=="Import"&&m.GetParameters().Length==2);mode=EngineeringScalarProperties.ConvertValue(JsonValue.Create(importOptions),import.GetParameters()[1].ParameterType);
                }
                meta["dryRun"]=dryRun;meta["mayHaveChanged"]=false;meta["mayHaveWrittenFiles"]=false;
                if(chart!=null)meta["before"]=EngineeringObjectAddress.Read(chart);
                if(action=="read") {meta["runSequence"]=OfficialServiceAccess.Result(EngineeringGroupOperations.Call(chart!,"GetRunSequence",Type.EmptyTypes));return "Exact offline DCC chart and native run sequence read; nested block/pin content not implied complete.";}
                if(dryRun)return "DCC operation preview. Delete removes the chart and descendants; import follows native replacement options. No online drive operation.";
                meta["mayHaveChanged"]=action!="export";meta["mayHaveWrittenFiles"]=action=="export";
                switch(action) {
                    case "create":chart=EngineeringGroupOperations.Call(charts,"Create",new[]{typeof(string)},chartName);EngineeringScalarProperties.Apply(chart,prepared,meta);break;
                    case "update":EngineeringScalarProperties.Apply(chart!,prepared,meta);break;
                    case "delete":EngineeringGroupOperations.Call(chart!,"Delete",Type.EmptyTypes);break;
                    case "export":EngineeringGroupOperations.Call(chart!,"Export",new[]{typeof(string)},file!.FullName);meta["file"]=NativeFileOutput.Verify(file);break;
                    case "import":OfficialServiceAccess.AttachResult(meta,EngineeringGroupOperations.Call(charts,"Import",import!.GetParameters().Select(p=>p.ParameterType).ToArray(),file!.FullName,mode!));break;
                    case "optimizeSequence":OfficialServiceAccess.AttachResult(meta,EngineeringGroupOperations.Call(chart!,"OptimizeRunSequence",Type.EmptyTypes));break;
                }
                var after=EngineeringGroupOperations.Find(charts,chartName);
                if(action=="delete" ? after!=null : after==null)throw new InvalidOperationException("Expected chart presence not verified after native operation.");
                if(after!=null)meta["after"]=EngineeringObjectAddress.Read(after);meta["presenceVerified"]=true;
                return "Native DCC operation returned and chart presence checked; no save, download or online drive command.";
            });
        public ResponseMessage ReadDccObject(string devicePathJson,string itemPathJson,ushort driveObjectNumber,string objectPathJson="[]",int offset=0,int limit=100)
            =>RunHmiStepTool("ReadDccObject",meta=>{
                if(offset<0||limit<1||limit>500)throw new ArgumentException("Invalid pagination.");
                var drive=ExactOfflineDrive(devicePathJson,itemPathJson,driveObjectNumber);
                var root=OfficialServiceAccess.Require(drive,"Siemens.Engineering.MC.Drives.Dcc.DriveControlChartContainer","Siemens.Engineering.DCC");
                var target=EngineeringObjectAddress.Resolve(root,objectPathJson);
                var items=target is System.Collections.IEnumerable&&target is not string ? EngineeringGroupOperations.Items(target).ToArray() : new[]{target};
                var rows=items.Skip(offset).Take(limit).Select(x=>(JsonNode)EngineeringObjectAddress.Read(x)).ToArray();
                meta["records"]=new JsonArray(rows);meta["expectedCount"]=items.Length;meta["actualCount"]=rows.Length;meta["nextOffset"]=offset+rows.Length<items.Length ? offset+rows.Length : (int?)null;meta["truncated"]=offset+rows.Length<items.Length;meta["dataComplete"]=false;
                return "Offline DCC objects (charts/blocks/pins/libraries) read at exact property path; scalar scope and live pagination only.";
            });
    }
}
