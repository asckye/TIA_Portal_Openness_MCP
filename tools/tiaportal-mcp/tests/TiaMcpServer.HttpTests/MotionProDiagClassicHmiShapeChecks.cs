using System;
using System.Linq;
using System.Reflection;

internal static class MotionProDiagClassicHmiShapeChecks
{
    internal static void Run(Assembly server, Action<bool,string> check)
    {
        Assembly Api(string split,string legacy) { try { return Assembly.Load(split); } catch(System.IO.FileNotFoundException) { return Assembly.Load(legacy); } }
        var step7=Api("Siemens.Engineering.Step7","Siemens.Engineering");
        var core=Api("Siemens.Engineering.Base","Siemens.Engineering");
        var wincc=Api("Siemens.Engineering.WinCC","Siemens.Engineering");
        Type T(Assembly a,string n)=>a.GetType(n,true)!;
        Type? Optional(Assembly a,string n,string capability) { var t=a.GetType(n); if(t==null) Console.WriteLine("CAPABILITY "+n+" absent from these assemblies; "+capability); return t; }
        void Method(Type t,string name,params Type[] signature)=>check(t.GetMethod(name,signature)!=null,t.FullName+"."+name+"("+string.Join(",",signature.Select(s=>s.Name))+")");
        void Property(Type t,string name,bool writable=false)=>check(t.GetProperty(name)!=null && (!writable || t.GetProperty(name)!.SetMethod?.IsPublic==true),t.FullName+"."+name+(writable?" writable":""));
        void Service(Type t)=>check(t.GetInterfaces().Any(i=>i.FullName=="Siemens.Engineering.IEngineeringService"),t.FullName+" is IEngineeringService");
        const string motion="Siemens.Engineering.SW.TechnologicalObjects.Motion.";
        var to=T(step7,"Siemens.Engineering.SW.TechnologicalObjects.TechnologicalInstanceDB");
        var deviceItem=T(core,"Siemens.Engineering.HW.DeviceItem");
        var plcTag=T(step7,"Siemens.Engineering.SW.Tags.PlcTag");
        var connectOption=T(step7,motion+"ConnectOption");
        check(to.GetInterfaces().Any(i=>i.FullName=="Siemens.Engineering.IEngineeringServiceProvider"),"TechnologicalInstanceDB provides services");
        Property(to,"Parameters");
        // Master value associations (SynchronousAxisMasterValues/ConveyorTrackingLeadingValues exist on both versions).
        var association=T(step7,"Siemens.Engineering.SW.TechnologicalObjects.TechnologicalInstanceDBAssociation");
        foreach(var name in new[]{"Add","Remove","Contains"}) Method(association,name,to);
        foreach(var svc in new[]{"SynchronousAxisMasterValues","ConveyorTrackingLeadingValues"}) {
            var t=T(step7,motion+svc); Service(t);
            foreach(var p in new[]{"SetPointCoupling","ActualValueCoupling","DelayedCoupling"}) check(t.GetProperty(p)?.PropertyType==association,svc+"."+p+" is TechnologicalInstanceDBAssociation");
        }
        var superimposing=Optional(step7,motion+"SuperimposingAxes","aspect superimposingSetPoint must return NotSupported on V20.");
        if(superimposing!=null) { Service(superimposing); check(superimposing.GetProperty("SetPointCoupling")?.PropertyType==association,"SuperimposingAxes.SetPointCoupling association"); }
        // Interpreter mappings (S7-1500T interpreter, V21 only).
        var mappings=Optional(step7,motion+"InterpreterMappings","aspects toMapping/dbMemberMapping must return NotSupported on V20.");
        if(mappings!=null) {
            Service(mappings); Property(mappings,"DBMemberMapping"); Property(mappings,"TechnologicalObjectMapping");
            var toMapping=T(step7,motion+"TOMapping"); Property(toMapping,"Alias"); Property(toMapping,"TechnologicalObject",true); Method(toMapping,"Delete");
            Method(T(step7,motion+"TOMappingComposition"),"Create",typeof(string));
            var dbMapping=T(step7,motion+"DBMemberMapping"); foreach(var p in new[]{"MemberPath","DataType","StartIndex","Readonly"}) Property(dbMapping,p,true); Method(dbMapping,"Delete");
            Method(T(step7,motion+"DBMemberMappingComposition"),"Create",typeof(string));
            check(T(step7,motion+"TOMappingComposition").GetProperty("Item")!=null && T(step7,motion+"DBMemberMappingComposition").GetProperty("Item")!=null,"Mapping compositions expose an Item indexer for property preparation");
        }
        // Hardware connection providers and interfaces.
        var axisProvider=T(step7,motion+"AxisHardwareConnectionProvider"); Service(axisProvider);
        foreach(var p in new[]{"ActorInterface","SensorInterface","TorqueInterface"}) Property(axisProvider,p);
        var encoderProvider=T(step7,motion+"EncoderHardwareConnectionProvider"); Service(encoderProvider); Property(encoderProvider,"SensorInterface");
        var axisInterface=T(step7,motion+"AxisEncoderHardwareConnectionInterface");
        Method(axisInterface,"Connect",deviceItem); Method(axisInterface,"Connect",deviceItem,deviceItem); Method(axisInterface,"Connect",deviceItem,deviceItem,connectOption);
        Method(axisInterface,"Connect",typeof(string)); Method(axisInterface,"Connect",plcTag); Method(axisInterface,"Connect",typeof(int),typeof(int),connectOption); Method(axisInterface,"Disconnect");
        Property(axisInterface,"IsConnected");
        var torqueInterface=T(step7,motion+"TorqueHardwareConnectionInterface");
        Method(torqueInterface,"Connect",deviceItem); Method(torqueInterface,"Connect",deviceItem,deviceItem); Method(torqueInterface,"Connect",deviceItem,deviceItem,connectOption);
        Method(torqueInterface,"Connect",typeof(string)); Method(torqueInterface,"Disconnect"); Property(torqueInterface,"IsConnected");
        check(torqueInterface.GetMethod("Connect",new[]{plcTag})==null,"Torque interface has no PlcTag overload (tool must refuse plcTag for torque)");
        var measuring=T(step7,motion+"MeasuringInputHardwareConnectionProvider"); Service(measuring);
        Method(measuring,"Connect",typeof(int)); Method(measuring,"Connect",deviceItem,typeof(int)); Method(measuring,"Disconnect"); Property(measuring,"IsConnected");
        var outputCam=T(step7,motion+"OutputCamHardwareConnectionProvider"); Service(outputCam);
        Method(outputCam,"Connect",typeof(int)); Method(outputCam,"Connect",plcTag); Method(outputCam,"Disconnect"); Property(outputCam,"IsConnected");
        var container=T(step7,motion+"OutputCamMeasuringInputContainer"); Service(container); Property(container,"OutputCams"); Property(container,"MeasuringInputs");
        Service(T(step7,motion+"CamDataSupport")); Service(T(step7,motion+"InterpreterProgramSupport"));
        var ident=Optional(step7,"Siemens.Engineering.SW.TechnologicalObjects.Ident.IdentTechnologicalObjectProvider","connectIdent must return NotSupported on V20.");
        if(ident!=null) { Service(ident); Method(ident,"Connect",deviceItem); Property(ident,"ConnectedIdentDevice"); }
        // ProDiag: only providers exist; no typed supervision composition on either version.
        var supervision=T(step7,"Siemens.Engineering.SW.Supervision.SupervisionProvider"); Service(supervision);
        var settings=T(step7,"Siemens.Engineering.SW.Supervision.SupervisionSettingsProvider"); Service(settings);
        Method(settings,"Export",typeof(System.IO.FileInfo)); Method(settings,"Import",typeof(System.IO.FileInfo));
        var settingsResult=T(step7,"Siemens.Engineering.SW.Supervision.SupervisionSettingsExportImportResult");
        foreach(var p in new[]{"State","ErrorCount","WarningCount","Messages"}) Property(settingsResult,p);
        check(Enum.GetNames(T(step7,"Siemens.Engineering.SW.Supervision.SupervisionSettingsExportImportResultState")).Contains("Success"),"Settings result state has Success");
        check(step7.GetType("Siemens.Engineering.SW.Supervision.Supervision")==null && step7.GetType("Siemens.Engineering.SW.Supervision.SupervisionComposition")==null,"No typed supervision composition exists; entries go through IEngineeringObject compositions only");
        // Official dynamic object API used for attributes, compositions, creation and invocation.
        var engineeringObject=T(core,"Siemens.Engineering.IEngineeringObject");
        Method(engineeringObject,"GetAttributeInfos"); Method(engineeringObject,"GetAttribute",typeof(string)); Method(engineeringObject,"SetAttribute",typeof(string),typeof(object));
        Method(engineeringObject,"GetCompositionInfos"); Method(engineeringObject,"GetComposition",typeof(string)); Method(engineeringObject,"GetInvocationInfos");
        Method(engineeringObject,"Invoke",typeof(string),typeof(System.Collections.Generic.IEnumerable<System.Collections.Generic.KeyValuePair<Type,object>>));
        var composition=T(core,"Siemens.Engineering.IEngineeringComposition");
        Method(composition,"GetCreationInfos"); Method(composition,"Create",typeof(Type),typeof(System.Collections.Generic.IEnumerable<System.Collections.Generic.KeyValuePair<string,object>>));
        foreach(var p in new[]{"Name","AccessMode"}) Property(T(core,"Siemens.Engineering.EngineeringAttributeInfo"),p);
        Property(T(core,"Siemens.Engineering.EngineeringCompositionInfo"),"Name"); Property(T(core,"Siemens.Engineering.EngineeringCreationInfo"),"Type");
        foreach(var p in new[]{"Name","ParameterInfos"}) Property(T(core,"Siemens.Engineering.EngineeringInvocationInfo"),p);
        // Classic HMI.
        var hmi=T(wincc,"Siemens.Engineering.Hmi.HmiTarget");
        foreach(var p in new[]{"VBScriptFolder","Cycles","TextLists","GraphicLists"}) Property(hmi,p);
        check(hmi.GetInterfaces().Any(i=>i.FullName=="Siemens.Engineering.IEngineeringServiceProvider"),"HmiTarget provides services");
        const string scripting="Siemens.Engineering.Hmi.RuntimeScripting.";
        foreach(var folder in new[]{"VBScriptSystemFolder","VBScriptUserFolder"}) { var t=T(wincc,scripting+folder); Property(t,"Folders"); Property(t,"VBScripts"); }
        Method(T(wincc,scripting+"VBScriptUserFolder"),"Delete"); Property(T(wincc,scripting+"VBScriptUserFolder"),"Name");
        Method(T(wincc,scripting+"VBScriptUserFolderComposition"),"Create",typeof(string));
        var script=T(wincc,scripting+"VBScript"); Property(script,"Name"); Method(script,"Delete");
        var exportOptions=T(core,"Siemens.Engineering.ExportOptions"); var importOptions=T(core,"Siemens.Engineering.ImportOptions");
        Method(script,"Export",typeof(System.IO.FileInfo),exportOptions);
        Method(T(wincc,scripting+"VBScriptComposition"),"Import",typeof(System.IO.FileInfo),importOptions);
        check(script.GetProperties().All(p=>p.Name=="Name"||p.Name=="Parent"),"VBScript exposes no typed source property (code only via export XML or advertised attributes)");
        check(T(wincc,scripting+"VBScriptComposition").GetMethod("Create",new[]{typeof(string)})==null,"VBScriptComposition has no Create(string); tool must not offer create-from-code");
        var cycle=T(wincc,"Siemens.Engineering.Hmi.Cycle.Cycle"); Property(cycle,"Name"); Property(cycle,"IsSystemObject"); Method(cycle,"Delete"); Method(cycle,"Export",typeof(System.IO.FileInfo),exportOptions);
        var cycles=T(wincc,"Siemens.Engineering.Hmi.Cycle.CycleComposition"); Method(cycles,"Import",typeof(System.IO.FileInfo),importOptions);
        check(cycles.GetMethod("Create",new[]{typeof(string)})==null,"CycleComposition has no Create(string); cycles arrive by import only");
        foreach(var list in new[]{"TextList","GraphicList"}) {
            var t=T(wincc,"Siemens.Engineering.Hmi.TextGraphicList."+list); Property(t,"Name"); Method(t,"Delete"); Method(t,"Export",typeof(System.IO.FileInfo),exportOptions);
            var c=T(wincc,"Siemens.Engineering.Hmi.TextGraphicList."+list+"Composition"); Method(c,"Import",typeof(System.IO.FileInfo),importOptions);
            check(c.GetMethod("Create",new[]{typeof(string)})==null,list+"Composition has no Create(string)");
        }
        var graphics=Optional(wincc,"Siemens.Engineering.Hmi.Globalization.GraphicsProvider","ReadClassicHmiGlobalization must return NotSupported on V20.");
        if(graphics!=null) { Service(graphics); Property(graphics,"Graphics"); Property(T(wincc,"Siemens.Engineering.Hmi.Globalization.MultiLingualGraphic"),"Name"); }
        var libraryType=T(core,"Siemens.Engineering.Library.Types.LibraryType"); Property(libraryType,"Versions");
        foreach(var kind in new[]{"Siemens.Engineering.Hmi.Faceplate.FaceplateLibraryType","Siemens.Engineering.Hmi.RuntimeScripting.VBScriptLibraryType","Siemens.Engineering.Hmi.RuntimeScripting.CScriptLibraryType"})
            check(libraryType.IsAssignableFrom(T(wincc,kind)),kind+" derives from LibraryType");
        Property(T(core,"Siemens.Engineering.Library.ProjectLibrary"),"TypeFolder");
        // Tool contract.
        var tools=server.GetType("TiaMcpServer.ModelContextProtocol.McpServer",true)!;
        foreach(var name in new[]{"ManageMotionAxis","ManagePlcSupervision","ManageClassicHmiScript","ManageClassicHmiCycle","ManageClassicHmiTextGraphicList"}) {
            var method=tools.GetMethod(name)!;
            check(Equals(method.GetParameters().Last().Name,"dryRun") && Equals(method.GetParameters().Last().DefaultValue,true),name+" defaults to preview with dryRun last");
            check(Equals(method.GetParameters().Single(p=>p.Name=="confirmDelete").DefaultValue,false),name+" deletion requires explicit confirmation");
        }
        foreach(var name in new[]{"ReadMotionAxisConfiguration","ReadClassicHmiScripts","ReadClassicHmiGlobalization","ReadClassicHmiFaceplates"})
            check(tools.GetMethod(name)!=null && tools.GetMethod(name)!.GetParameters().All(p=>p.Name!="dryRun"),name+" is a read-only tool");
    }
}
