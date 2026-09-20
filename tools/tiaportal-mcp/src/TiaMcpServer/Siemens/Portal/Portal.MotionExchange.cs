using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using TiaMcpServer.ModelContextProtocol;
namespace TiaMcpServer.Siemens
{
    public partial class Portal
    {
        private object ExactTechnology(string softwarePath,string objectPath,bool writing)
        {
            var plc=ExactPlcForEngineering(softwarePath,writing);var parts=EngineeringGroupOperations.Parts(objectPath);
            var group=EngineeringGroupOperations.Group(plc.TechnologicalObjectGroup,string.Join("/",parts.Take(parts.Length-1)));
            return EngineeringGroupOperations.Find(EngineeringGroupOperations.Get(group,"TechnologicalObjects"),parts.Last()) ?? throw new InvalidOperationException("Exact technology object not found.");
        }
        public ResponseMessage ExchangeMotionCamData(string softwarePath,string objectPath,string action,string filePath,string format="",string separator="",int pointCount=0,bool dryRun=true)
            =>RunHmiStepTool("ExchangeMotionCamData",meta=>{
                var method=action switch {"import"=>"LoadCamData","importBinary"=>"LoadCamDataBinary","export"=>"SaveCamData","exportBinary"=>"SaveCamDataBinary","exportPoints"=>"SaveCamDataPointList",_=>throw new ArgumentException("Invalid cam exchange action.")};
                bool write=action.StartsWith("import",StringComparison.Ordinal);
                using var access=write&&!dryRun ? AcquireHmiEditAccess() : null;
                var target=ExactTechnology(softwarePath,objectPath,write&&!dryRun);
                var service=OfficialServiceAccess.Require(target,"Siemens.Engineering.SW.TechnologicalObjects.Motion.CamDataSupport","Siemens.Engineering.Step7");
                var call=service.GetType().GetMethods().Single(m=>m.Name==method);
                var file=write ? new FileInfo(filePath) : NativeFileOutput.Plan(filePath);
                if(write&&!file.Exists)throw new FileNotFoundException("Cam input not found.");
                var parameters=call.GetParameters();var args=new object[parameters.Length];args[0]=file;
                for(int i=1;i<parameters.Length;i++)args[i]=parameters[i].ParameterType==typeof(int) ? pointCount : EngineeringScalarProperties.ConvertValue(JsonValue.Create(parameters[i].ParameterType.Name=="CamDataFormat" ? format : separator),parameters[i].ParameterType)!;
                if(action=="exportPoints"&&pointCount<1)throw new ArgumentException("Positive pointCount required.");
                meta["dryRun"]=dryRun;meta["mayHaveChanged"]=false;meta["mayHaveWrittenFiles"]=false;meta["objectPath"]=objectPath;
                if(dryRun)return "Native cam data exchange preview.";
                meta["mayHaveChanged"]=write;meta["mayHaveWrittenFiles"]=!write;
                var result=EngineeringGroupOperations.Call(service,method,parameters.Select(p=>p.ParameterType).ToArray(),args);
                OfficialServiceAccess.AttachResult(meta,result);if(result is bool ok&&!ok)meta["operationSuccess"]=false;
                if(!write)meta["file"]=NativeFileOutput.Verify(file);
                return "Native cam exchange returned; file/diagnostic evidence supplied. No save/compile/download or motion command.";
            });
        public ResponseMessage ConfigureMotionHardwareConnection(string softwarePath,string objectPath,string interfaceKind,string action,int inputBitAddress=0,int outputBitAddress=0,string connectOption="Default",int sensorIndex=0,bool dryRun=true)
            =>RunHmiStepTool("ConfigureMotionHardwareConnection",meta=>{
                if (!new[] { "read", "connect", "disconnect" }.Contains(action)) throw new ArgumentException("action must be one of: read/connect/disconnect (case-sensitive).");
                bool write=action!="read"&&!dryRun;using var access=write ? AcquireHmiEditAccess() : null;
                var target=ExactTechnology(softwarePath,objectPath,write);
                // 2.7.36: typed AxisHardwareConnectionProvider (ActorInterface / SensorInterface[i] : AxisEncoderHardwareConnectionInterface, TorqueInterface : TorqueHardwareConnectionInterface).
                global::Siemens.Engineering.SW.TechnologicalObjects.Motion.AxisHardwareConnectionProvider service=(target as global::Siemens.Engineering.IEngineeringServiceProvider)?.GetService<global::Siemens.Engineering.SW.TechnologicalObjects.Motion.AxisHardwareConnectionProvider>()
                    ?? throw new NotSupportedException("AxisHardwareConnectionProvider is not provided by "+target.GetType().Name+" (axis technology objects only).");
                object selected;
                if(interfaceKind=="actor")selected=service.ActorInterface;
                else if(interfaceKind=="torque")selected=service.TorqueInterface;
                else if(interfaceKind=="sensor") {
                    global::Siemens.Engineering.SW.TechnologicalObjects.Motion.AxisEncoderHardwareConnectionInterfaceComposition sensors=service.SensorInterface;
                    if(sensorIndex<0||sensorIndex>=sensors.Count)throw new ArgumentException("Sensor index out of range.");selected=sensors[sensorIndex];
                } else throw new ArgumentException("interfaceKind must be actor/sensor/torque.");
                meta["before"]=InterfaceRow(selected);meta["dryRun"]=dryRun;meta["mayHaveChanged"]=false;
                var method=action=="connect" ? selected.GetType().GetMethods().SingleOrDefault(m=>m.Name=="Connect"&&m.GetParameters().Length==3&&m.GetParameters()[0].ParameterType==typeof(int)&&m.GetParameters()[1].ParameterType==typeof(int)) : selected.GetType().GetMethod("Disconnect",Type.EmptyTypes);
                if(action!="read"&&method==null)throw new NotSupportedException("Native hardware connection signature unavailable.");
                object? mode=null;if(action=="connect") {
                    if(inputBitAddress<0||outputBitAddress<0)throw new ArgumentException("Nonnegative BIT addresses required; no implicit byte conversion.");
                    mode=EngineeringScalarProperties.ConvertValue(JsonValue.Create(connectOption),method!.GetParameters()[2].ParameterType);
                }
                if(!write)return "Motion hardware mapping read/preview. Addresses are bits; no drive operation performed.";
                meta["mayHaveChanged"]=true;
                if(action=="connect") {
                    var option=(global::Siemens.Engineering.SW.TechnologicalObjects.Motion.ConnectOption)mode!;
                    if(selected is global::Siemens.Engineering.SW.TechnologicalObjects.Motion.AxisEncoderHardwareConnectionInterface axisInterface)axisInterface.Connect(inputBitAddress,outputBitAddress,option);
                    else ((global::Siemens.Engineering.SW.TechnologicalObjects.Motion.TorqueHardwareConnectionInterface)selected).Connect(inputBitAddress,outputBitAddress,option);
                } else if(!DisconnectTyped(selected))EngineeringGroupOperations.Call(selected,"Disconnect",Type.EmptyTypes);
                meta["after"]=InterfaceRow(selected);meta["mappingVerified"]=IsConnectedTyped(selected)==(action=="connect");
                return "Offline motion hardware mapping changed; inspect native readback. No save/compile/download or motion command.";
            });
    }
}
