using System;
using System.IO;
using System.Linq;
using System.Reflection;

// Native members used by Portal.TechnologyMapping.cs, TechnologyObjectsService.cs and the typed technology-object operations of
// GetMotionAxisConfiguration / ManageMotionAxis / ManageTechnologyObject / ConfigureMotionHardwareConnection, verified member by
// member against the installed V20 (Siemens.Engineering) or V21 (Siemens.Engineering.Base / Siemens.Engineering.Step7) PublicAPI.
internal static class TechnologyMappingShapeChecks
{
    internal static void Run(Assembly server, Action<bool,string> check)
    {
        CheckDomainServices(server, check);
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

    private static void CheckDomainServices(Assembly server, Action<bool,string> check)
    {
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        var surface = EngineSurface.For(server);
        var provider = (IServiceProvider)server.GetType("TiaMcpServer.EngineServices", true)!.GetProperty("Provider", all)!.GetValue(null)!;
        var sessionType = server.GetType("TiaMcpServer.Siemens.IEngineeringSession", true)!;
        var session = provider.GetService(sessionType);
        var kernel = server.GetType("TiaMcpServer.Siemens.Portal", true)!;
        foreach (var domain in new[] {
            ("Alarms", "ExportAlarmClasses ImportAlarmClasses ExportAlarmTextLists ImportAlarmTextLists ExportAlarmInstanceTexts ExchangePlcAlarmTextLists ImportPlcAlarmInstanceTexts ManagePlcAlarmTextList"),
            ("OpcUa", "GetPlcOpcUaConfiguration ManageOpcUaInterface SetOpcUaInterfaceEnabled ExportOpcUaInterface ImportOpcUaInterface GenerateOpcUaModelledInterface GetOpcUaAccessControl ManageOpcUaAccessControl"),
            ("TechnologyObjects", "ListTechnologyObjects ExportTechnologyObject ExportTechnologyObjectsToDirectory ImportTechnologyObject ImportTechnologyObjectsFromDirectory GetTechnologyObjectTree ManageTechnologyObject")
        })
        {
            var service = server.GetType("TiaMcpServer.Siemens.Services." + domain.Item1 + "Service", true)!;
            var tools = server.GetType("TiaMcpServer.ModelContextProtocol." + domain.Item1 + "Tools", true)!;
            var target = provider.GetService(service);
            var toolTarget = provider.GetService(tools);
            foreach (var type in new[] { service, tools })
                check(type.IsSealed && !typeof(IDisposable).IsAssignableFrom(type)
                    && !type.GetInterfaces().Any(item => item.Name == "IAsyncDisposable"), type.Name + " is sealed and non-disposable");
            check(service.GetConstructors().Single().GetParameters().Single().ParameterType == sessionType
                && ReferenceEquals(target, provider.GetService(service)) && ReferenceEquals(toolTarget, provider.GetService(tools))
                && ReferenceEquals(service.GetField("_session", all)!.GetValue(target), session)
                && ReferenceEquals(tools.GetField("_service", all)!.GetValue(toolTarget), target),
                domain.Item1 + " singletons share the engineering session");
            foreach (var name in domain.Item2.Split(' '))
            {
                var tool = surface.Tool(name);
                string legacyName = tool.Name.Substring(0, tool.Name.Length - "V4".Length);
                var implementation = tools.GetMethod(legacyName)!;
                var method = service.GetMethod(legacyName)!;
                check(!tool.IsStatic && tool.DeclaringType == tools && ReferenceEquals(surface.Target(tool), toolTarget),
                    name + " resolves to the registered instance tool");
                check(tool.ReturnType.Name == "CallToolResult", name + " exposes the V4 envelope boundary");
                var il = implementation.GetMethodBody()!.GetILAsByteArray()!;
                bool callsService = Enumerable.Range(0, Math.Max(0, il.Length - 4)).Any(index =>
                    (il[index] == 0x28 || il[index] == 0x6f) && BitConverter.ToInt32(il, index + 1) == method.MetadataToken);
                EngineSurface.CheckIl(check, callsService, name + " retains its domain service implementation", implementation, method);
                check(surface.Method(legacyName).DeclaringType == (name == "ImportTechnologyObject" ? kernel : service),
                    name + " keeps its intended service or shared kernel owner");
            }
        }
        foreach (var name in new[] { "ParameterRow", "TechnologyObjectRow", "TypedMotionView", "InterfaceRow", "MappingRow", "ConnectTyped", "DisconnectTyped", "IsConnectedTyped" })
            check(surface.Method(name, all).DeclaringType == kernel, name + " remains on the kernel for Motion and Startdrive");
    }
}
