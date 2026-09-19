using System;
using System.IO;
using System.Linq;
using System.Reflection;

// Native members used by the 2.7.36 Step7 sub-batch 3 (Portal.TechnologyMapping.cs and the typed technology-object retrofits of
// ReadMotionAxisConfiguration / ManageMotionAxis / ManageTechnologyObject / ConfigureMotionHardwareConnection), verified member by
// member against the installed V20 (Siemens.Engineering) or V21 (Siemens.Engineering.Base / Siemens.Engineering.Step7) PublicAPI.
internal static class TechnologyMappingShapeChecks
{
    internal static void Run(Assembly server, Action<bool,string> check)
    {
        Assembly Api(string split,string legacy) { try { return Assembly.Load(split); } catch(FileNotFoundException) { return Assembly.Load(legacy); } }
        var step7=Api("Siemens.Engineering.Step7","Siemens.Engineering");
        var core=Api("Siemens.Engineering.Base","Siemens.Engineering");
        bool v20=core.GetName().Version!.Major==20;
        Type T(Assembly a,string n)=>a.GetType(n,true)!;
        void Method(Assembly a,string t,string name,Type[] signature,string? returns=null)
        {
            var m=T(a,t).GetMethod(name,signature);
            check(m!=null && (returns==null || m.ReturnType.Name==returns),t+"."+name+"("+string.Join(",",signature.Select(x=>x.Name))+")"+(returns==null?"":" -> "+returns));
        }
        void Property(Assembly a,string t,string name,string? type=null,bool? writable=null)
        {
            var p=T(a,t).GetProperty(name);
            check(p!=null && (type==null || p.PropertyType.Name==type) && (writable==null || p.CanWrite==writable),t+"."+name+(type==null?"":" : "+type)+(writable==null?"":writable.Value?" (writable)":" (read-only)"));
        }
        void Enum(Assembly a,string t,params string[] names)
        {
            var type=T(a,t); var missing=names.Where(n=>!System.Enum.IsDefined(type,n)).ToArray();
            check(type.IsEnum && missing.Length==0,t+" defines "+string.Join("/",names)+(missing.Length==0?"":" (missing "+string.Join(",",missing)+")"));
        }
        var engineeringService=T(core,"Siemens.Engineering.IEngineeringService"); var serviceProvider=T(core,"Siemens.Engineering.IEngineeringServiceProvider");
        void Service(string t) => check(engineeringService.IsAssignableFrom(T(step7,t)),t+" is an IEngineeringService");
        const string to="Siemens.Engineering.SW.TechnologicalObjects.", motion="Siemens.Engineering.SW.TechnologicalObjects.Motion.";
        var text=typeof(string); var integer=typeof(int);
        var deviceItem=T(core,"Siemens.Engineering.HW.DeviceItem"); var channel=T(core,"Siemens.Engineering.HW.Channel"); var plcTag=T(step7,"Siemens.Engineering.SW.Tags.PlcTag");
        var instanceDb=T(step7,to+"TechnologicalInstanceDB"); var option=T(step7,motion+"ConnectOption");

        // ---- technology object tree ----
        Property(step7,"Siemens.Engineering.SW.PlcSoftware","TechnologicalObjectGroup","TechnologicalInstanceDBGroup");
        check(T(step7,to+"TechnologicalInstanceDBSystemGroup").IsSubclassOf(T(step7,to+"TechnologicalInstanceDBGroup")) && T(step7,to+"TechnologicalInstanceDBUserGroup").IsSubclassOf(T(step7,to+"TechnologicalInstanceDBGroup")),"system / user TO groups derive from TechnologicalInstanceDBGroup");
        Property(step7,to+"TechnologicalInstanceDBGroup","Name","String"); Property(step7,to+"TechnologicalInstanceDBGroup","TechnologicalObjects","TechnologicalInstanceDBComposition"); Property(step7,to+"TechnologicalInstanceDBGroup","Groups","TechnologicalInstanceDBUserGroupComposition");
        check(serviceProvider.IsAssignableFrom(T(step7,to+"TechnologicalInstanceDBGroup")),"TechnologicalInstanceDBGroup is a service provider");
        Property(step7,to+"TechnologicalInstanceDBUserGroup","Name","String",true); Method(step7,to+"TechnologicalInstanceDBUserGroup","Delete",Type.EmptyTypes,"Void");
        Method(step7,to+"TechnologicalInstanceDBComposition","Create",new[]{text,text,typeof(Version)},"TechnologicalInstanceDB"); Method(step7,to+"TechnologicalInstanceDBComposition","Find",new[]{text},"TechnologicalInstanceDB");
        check(instanceDb.IsSubclassOf(T(step7,"Siemens.Engineering.SW.Blocks.InstanceDB")),"TechnologicalInstanceDB derives from InstanceDB (Number / IsConsistent / Delete)");
        Property(step7,to+"TechnologicalInstanceDB","Parameters","TechnologicalParameterComposition"); Property(step7,to+"TechnologicalInstanceDB","OfSystemLibElement","String"); Property(step7,to+"TechnologicalInstanceDB","OfSystemLibVersion","Version");
        Method(step7,to+"TechnologicalParameterComposition","Find",new[]{text},"TechnologicalParameter");
        Property(step7,to+"TechnologicalParameter","Name","String",false); Property(step7,to+"TechnologicalParameter","Value","Object",true);
        Method(step7,to+"TechnologicalInstanceDBAssociation","Add",new[]{instanceDb},"Void"); Method(step7,to+"TechnologicalInstanceDBAssociation","Remove",new[]{instanceDb},"Boolean"); Method(step7,to+"TechnologicalInstanceDBAssociation","Contains",new[]{instanceDb},"Boolean");

        // ---- axis / encoder hardware interfaces ----
        Service(motion+"AxisHardwareConnectionProvider");
        Property(step7,motion+"AxisHardwareConnectionProvider","ActorInterface","AxisEncoderHardwareConnectionInterface"); Property(step7,motion+"AxisHardwareConnectionProvider","SensorInterface","AxisEncoderHardwareConnectionInterfaceComposition"); Property(step7,motion+"AxisHardwareConnectionProvider","TorqueInterface","TorqueHardwareConnectionInterface");
        Service(motion+"EncoderHardwareConnectionProvider"); Property(step7,motion+"EncoderHardwareConnectionProvider","SensorInterface","AxisEncoderHardwareConnectionInterface");
        const string axisIf=motion+"AxisEncoderHardwareConnectionInterface";
        foreach(var p in new[]{("IsConnected","Boolean"),("ConnectOption","ConnectOption"),("InputAddress","Int32"),("OutputAddress","Int32"),("PathToDBMember","String"),("SensorIndexInActorTelegram","Int32"),("InputModule","DeviceItem"),("OutputModule","DeviceItem"),("InputOutputModule","DeviceItem"),("OutputTag","PlcTag"),("Channel","Channel")}) Property(step7,axisIf,p.Item1,p.Item2);
        foreach(var sig in new[]{new[]{channel},new[]{deviceItem},new[]{plcTag},new[]{text},new[]{deviceItem,deviceItem},new[]{deviceItem,deviceItem,option},new[]{integer,integer,option}}) Method(step7,axisIf,"Connect",sig,"Void");
        Method(step7,axisIf,"Disconnect",Type.EmptyTypes,"Void");
        const string torqueIf=motion+"TorqueHardwareConnectionInterface";
        foreach(var p in new[]{("IsConnected","Boolean"),("ConnectOption","ConnectOption"),("InputAddress","Int32"),("OutputAddress","Int32"),("PathToDBMember","String"),("InputModule","DeviceItem"),("OutputModule","DeviceItem"),("InputOutputModule","DeviceItem")}) Property(step7,torqueIf,p.Item1,p.Item2);
        foreach(var sig in new[]{new[]{deviceItem},new[]{text},new[]{deviceItem,deviceItem},new[]{deviceItem,deviceItem,option},new[]{integer,integer,option}}) Method(step7,torqueIf,"Connect",sig,"Void");
        Method(step7,torqueIf,"Disconnect",Type.EmptyTypes,"Void");
        check(T(step7,torqueIf).GetMethod("Connect",new[]{channel})==null && T(step7,torqueIf).GetMethod("Connect",new[]{plcTag})==null,"TorqueHardwareConnectionInterface has no Connect(Channel) / Connect(PlcTag) (ManageMotionAxis refuses those targets for torque)");
        Enum(step7,motion+"ConnectOption","Default","AllowAllModules");
        if(v20) Console.WriteLine("CAPABILITY V20 adds Connect(Telegram[, ConnectOption]) overloads (reflective fallback only; Telegram is absent on V21)"); else check(T(step7,axisIf).GetMethods().All(m=>m.Name!="Connect" || m.GetParameters().All(p=>p.ParameterType.Name!="Telegram")),"V21 has no Telegram overloads");

        // ---- measuring input / output cam ----
        Service(motion+"MeasuringInputHardwareConnectionProvider");
        foreach(var sig in new[]{new[]{channel},new[]{integer},new[]{deviceItem,integer}}) Method(step7,motion+"MeasuringInputHardwareConnectionProvider","Connect",sig,"Void");
        Method(step7,motion+"MeasuringInputHardwareConnectionProvider","Disconnect",Type.EmptyTypes,"Void");
        foreach(var p in new[]{("IsConnected","Boolean"),("InputAddress","Int32"),("ChannelIndex","Int32"),("InputModule","DeviceItem"),("Channel","Channel")}) Property(step7,motion+"MeasuringInputHardwareConnectionProvider",p.Item1,p.Item2);
        Service(motion+"OutputCamHardwareConnectionProvider");
        foreach(var sig in new[]{new[]{channel},new[]{plcTag},new[]{integer}}) Method(step7,motion+"OutputCamHardwareConnectionProvider","Connect",sig,"Void");
        Method(step7,motion+"OutputCamHardwareConnectionProvider","Disconnect",Type.EmptyTypes,"Void");
        foreach(var p in new[]{("IsConnected","Boolean"),("OutputAddress","Int32"),("OutputTag","PlcTag"),("Channel","Channel")}) Property(step7,motion+"OutputCamHardwareConnectionProvider",p.Item1,p.Item2);
        Method(core,"Siemens.Engineering.HW.ChannelComposition","Find",new[]{T(core,"Siemens.Engineering.HW.ChannelType"),T(core,"Siemens.Engineering.HW.ChannelIoType"),integer},"Channel");

        // ---- master values / containers ----
        foreach(var s in new[]{"SynchronousAxisMasterValues","ConveyorTrackingLeadingValues"}) { Service(motion+s); foreach(var p in new[]{"SetPointCoupling","ActualValueCoupling","DelayedCoupling"}) Property(step7,motion+s,p,"TechnologicalInstanceDBAssociation"); }
        Service(motion+"OutputCamMeasuringInputContainer"); Property(step7,motion+"OutputCamMeasuringInputContainer","OutputCams","TechnologicalInstanceDBComposition"); Property(step7,motion+"OutputCamMeasuringInputContainer","MeasuringInputs","TechnologicalInstanceDBComposition");
        Service(motion+"CamDataSupport"); Service(motion+"InterpreterProgramSupport");

        // ---- V21-only: interpreter mappings, superimposing axes, ident ----
        if(v20)
        {
            Console.WriteLine("CAPABILITY InterpreterMappings / TOMapping / DBMemberMapping / SuperimposingAxes / IdentTechnologicalObjectProvider absent on V20 (typed view notes it; ManageMotionAxis mapping aspects answer NotSupported there)");
            check(step7.GetType(motion+"TOMapping")==null && step7.GetType(motion+"InterpreterMappings")==null && step7.GetType(motion+"SuperimposingAxes")==null,"V20 has no interpreter mapping / superimposing types");
        }
        else
        {
            Service(motion+"InterpreterMappings"); Property(step7,motion+"InterpreterMappings","TechnologicalObjectMapping","TOMappingComposition"); Property(step7,motion+"InterpreterMappings","DBMemberMapping","DBMemberMappingComposition");
            Method(step7,motion+"TOMappingComposition","Create",new[]{text},"TOMapping"); Method(step7,motion+"TOMappingComposition","Find",new[]{text},"TOMapping");
            Property(step7,motion+"TOMapping","Alias","String",true); Property(step7,motion+"TOMapping","TechnologicalObject","TechnologicalInstanceDB",true); Method(step7,motion+"TOMapping","Delete",Type.EmptyTypes,"Void");
            Method(step7,motion+"DBMemberMappingComposition","Create",new[]{text},"DBMemberMapping"); Method(step7,motion+"DBMemberMappingComposition","Find",new[]{text},"DBMemberMapping");
            foreach(var p in new[]{("Alias","String"),("MemberPath","String"),("DataType","String"),("Readonly","Boolean"),("StartIndex","Int32")}) Property(step7,motion+"DBMemberMapping",p.Item1,p.Item2,true);
            Method(step7,motion+"DBMemberMapping","Delete",Type.EmptyTypes,"Void");
            Service(motion+"SuperimposingAxes"); Property(step7,motion+"SuperimposingAxes","SetPointCoupling","TechnologicalInstanceDBAssociation");
            Service(to+"Ident.IdentTechnologicalObjectProvider"); Property(step7,to+"Ident.IdentTechnologicalObjectProvider","ConnectedIdentDevice","DeviceItem"); Method(step7,to+"Ident.IdentTechnologicalObjectProvider","Connect",new[]{deviceItem},"Void");
        }
    }
}
