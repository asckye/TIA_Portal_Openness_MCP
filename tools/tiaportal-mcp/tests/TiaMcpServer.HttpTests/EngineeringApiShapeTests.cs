using System;
using System.Linq;
using System.Reflection;

internal static class EngineeringApiShapeTests
{
    internal static void Run(Assembly server, Action<bool,string> check)
    {
        Assembly Api(string split,string legacy) { try { return Assembly.Load(split); } catch(System.IO.FileNotFoundException) { return Assembly.Load(legacy); } }
        var step7=Api("Siemens.Engineering.Step7","Siemens.Engineering");
        var core=Api("Siemens.Engineering.Base","Siemens.Engineering");
        var unified=Api("Siemens.Engineering.WinCCUnified","Siemens.Engineering.Hmi");
        var safety=Api("Siemens.Engineering.Safety","Siemens.Engineering");
        Type T(Assembly a,string n)=>a.GetType(n,true)!;
        void Method(Assembly a,string t,string name,int count)
            =>check(T(a,t).GetMethods().Any(m=>m.Name==name && m.GetParameters().Length==count),t+"."+name+"/"+count);
        foreach(var family in new[]{"Blocks.PlcBlock","Types.PlcType","Tags.PlcTagTable","TechnologicalObjects.TechnologicalInstanceDB"})
        {
            Method(step7,"Siemens.Engineering.SW."+family+"UserGroupComposition","Create",1);
            Method(step7,"Siemens.Engineering.SW."+family+"UserGroup","Delete",0);
            var groupType=T(step7,"Siemens.Engineering.SW."+family+"UserGroup");
            check(groupType.GetProperty("Name")!.CanWrite || groupType.GetInterfaces().Any(i=>i.FullName=="Siemens.Engineering.IEngineeringObject" && i.GetMethod("SetAttribute")!=null),family+" group rename via property/dynamic attribute");
        }
        Method(step7,"Siemens.Engineering.SW.TechnologicalObjects.TechnologicalInstanceDBComposition","Create",3);
        Method(step7,"Siemens.Engineering.SW.WatchAndForceTables.PlcWatchTableComposition","Import",2);
        Method(step7,"Siemens.Engineering.SW.Units.PlcUnitComposition","Create",1);
        Method(step7,"Siemens.Engineering.SW.Units.PlcUnitRelationComposition","Create",2);
        foreach(var method in new[]{"CanPlugMove","CanPlugCopy","PlugMove","PlugCopy"}) Method(core,"Siemens.Engineering.HW.HardwareObject",method,2);
        Method(core,"Siemens.Engineering.Library.MasterCopies.MasterCopyComposition","Create",1);
        Method(core,"Siemens.Engineering.Library.Types.LibraryTypeVersion","Release",4);
        Method(core,"Siemens.Engineering.Library.Types.LibraryTypeVersion","SetAsDefault",0);
        Method(core,"Siemens.Engineering.Security.CertificateComposition","Create",1);
        Method(core,"Siemens.Engineering.Security.LocalCertificateStore","GetCertificateTemplate",1);
        Method(safety,"Siemens.Engineering.Safety.RuntimeGroupComposition","Create",1);
        Method(safety,"Siemens.Engineering.Safety.RuntimeGroup","Delete",0);
        var hmi=unified.GetType("Siemens.Engineering.HmiUnified.HmiSoftware") ?? core.GetType("Siemens.Engineering.HmiUnified.HmiSoftware");
        if(hmi == null) Console.WriteLine("CAPABILITY Unified API absent from these V20 assemblies; HMI shape checks NOT PERFORMED. This is not HMI support verification.");
        if(hmi != null) {
        foreach(var property in new[]{"AlarmClasses","DiscreteAlarms","AnalogAlarms","AlarmLogs","DataLogs","ScreenGroups","TagTableGroups"})
        {
            var collection=hmi.GetProperty(property)!.PropertyType;
            check(collection.GetMethod("Create",new[]{typeof(string)})!=null,"Unified "+property+" Create(string)");
        }
        foreach(var property in new[]{"HmiTextLists","HmiGraphicLists"})
        {
            if(property=="HmiGraphicLists" && core.GetName().Version!.Major==20 && hmi.GetProperty(property)==null) {
                Console.WriteLine("CAPABILITY V20 HmiGraphicLists unavailable; this category must return Unsupported, not an empty success.");
                continue;
            }
            check(hmi.GetProperty(property)!=null,"Unified "+property+" collection is readable");
            Console.WriteLine("CAPABILITY "+property+" Create(string)="+(hmi.GetProperty(property)!.PropertyType.GetMethod("Create",new[]{typeof(string)})!=null));
        }
        }
        var tools=server.GetType("TiaMcpServer.ModelContextProtocol.McpServer",true)!;
        foreach(var name in new[]{"CreatePlcTypeGroup","ManagePlcUserGroup","ManageUnifiedHmiGroup","ManageUnifiedEngineeringObject","ImportUnifiedEngineeringList","ManageTechnologyObject","ManagePlcSoftwareUnit","SetPlcUnitObjectAccess","ManageHardwareObject","ImportPlcWatchTableOffline","ManageLibraryTypeVersion","CreateLibraryMasterCopy","ManagePlcSafety","ManagePlcCertificate"})
        {
            var method=tools.GetMethod(name)!;
            var preview=method.GetParameters().Single(p=>p.Name=="dryRun");
            check(preview.HasDefaultValue && Equals(preview.DefaultValue,true),name+" defaults to preview");
        }
        check(tools.GetMethod("ReadUnifiedEngineeringObjects")!=null,"Unified reader exposed");
    }
}
