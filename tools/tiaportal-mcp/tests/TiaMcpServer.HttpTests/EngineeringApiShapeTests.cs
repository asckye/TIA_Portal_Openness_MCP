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
        Method(step7,"Siemens.Engineering.SW.Blocks.PlcBlockComposition","CreateInstanceDB",4);
        Method(step7,"Siemens.Engineering.SW.ExternalSources.PlcExternalSourceSystemGroup","GenerateSource",2);
        Method(step7,"Siemens.Engineering.SW.Loader.LoadableProvider","GenerateLoadable",3);
        Method(step7,"Siemens.Engineering.SW.Tags.PlcTagComposition","Create",3);
        Method(step7,"Siemens.Engineering.SW.Tags.PlcUserConstantComposition","Create",3);
        Method(core,"Siemens.Engineering.ProjectComposition","Retrieve",2);
        Method(core,"Siemens.Engineering.ProjectComposition","RetrieveWithUpgrade",2);
        Method(core,"Siemens.Engineering.LanguageAssociation","Add",1);
        Method(core,"Siemens.Engineering.LanguageAssociation","Remove",1);
        check(T(core,"Siemens.Engineering.ProjectBase").GetMethod("ExportProjectTexts",new[]{typeof(System.IO.FileInfo),typeof(System.Globalization.CultureInfo),typeof(System.Globalization.CultureInfo)})!.ReturnType == typeof(void),"Project text export returns void, not ProjectTextResult");
        check(T(core,"Siemens.Engineering.ProjectBase").GetMethod("ImportProjectTexts",new[]{typeof(System.IO.FileInfo),typeof(bool)})!.ReturnType.Name == "ProjectTextResult","Project text import returns native diagnostic result");
        Method(core,"Siemens.Engineering.Library.GlobalLibraryComposition","Retrieve",3);
        Method(core,"Siemens.Engineering.Library.GlobalLibraryComposition","Open",2);
        Method(core,"Siemens.Engineering.Library.UserGlobalLibrary","SaveAs",1);
        foreach(var folder in new[]{"Types.LibraryTypeUserFolder","MasterCopies.MasterCopyUserFolder"}) {
            Method(core,"Siemens.Engineering.Library."+folder+"Composition","Create",1);
            Method(core,"Siemens.Engineering.Library."+folder,"Delete",0);
        }
        Method(core,"Siemens.Engineering.Security.CertificateComposition","Create",1);
        Method(core,"Siemens.Engineering.Security.LocalCertificateStore","GetCertificateTemplate",1);
        Method(safety,"Siemens.Engineering.Safety.RuntimeGroupComposition","Create",1);
        Method(safety,"Siemens.Engineering.Safety.RuntimeGroup","Delete",0);
        var hmi=unified.GetType("Siemens.Engineering.HmiUnified.HmiSoftware") ?? core.GetType("Siemens.Engineering.HmiUnified.HmiSoftware");
        if(hmi == null) Console.WriteLine("CAPABILITY Unified API absent from these V20 assemblies; HMI shape checks NOT PERFORMED. This is not HMI support verification.");
        if(hmi != null) {
        check(core.GetName().Version!.Major==20 ? T(core,"Siemens.Engineering.ProjectBase").GetProperty("PlantViews")!=null : T(hmi.Assembly,"Siemens.Engineering.HmiUnified.Cpm.PlantViewsProvider").GetProperty("PlantViews")!=null,"Version-specific project PlantViews entry point");
        Method(hmi.Assembly,"Siemens.Engineering.HmiUnified.Cpm.PlantViewComposition","Create",1);
        Method(hmi.Assembly,"Siemens.Engineering.HmiUnified.Cpm.PlantViewNodeComposition","Create",2);
        Method(hmi.Assembly,"Siemens.Engineering.HmiUnified.Cpm.PlantViewNode","Delete",0);
        foreach(var property in new[]{"SystemTags","HmiSystemTextLists","AuditTrails","OpcUaAlarmTypes"})
            check(hmi.GetProperty(property)!=null,"Unified additional collection "+property);
        Method(hmi.Assembly,"Siemens.Engineering.HmiUnified.HmiTags.HmiTag","Validate",0);
        Method(hmi.Assembly,"Siemens.Engineering.HmiUnified.LoggingTags.HmiLoggingTagComposition","Create",1);
        Method(hmi.Assembly,"Siemens.Engineering.HmiUnified.HmiLogging.HmiLoggingCommon.LogDuration","SetLogDuration",5);
        Method(hmi.Assembly,"Siemens.Engineering.HmiUnified.HmiLogging.HmiLoggingCommon.SegmentDuration","SetSegmentDuration",5);
        var alarmCollection=hmi.GetProperty("OpcUaAlarmTypes")!.PropertyType;
        check(alarmCollection.GetMethod("Create",new[]{typeof(string),typeof(string),typeof(string)})!=null,"OPC UA alarm creation has three string arguments");
        check(hmi.GetProperty("HmiTextLists")!.PropertyType.GetMethod("Export",new[]{typeof(System.IO.DirectoryInfo),typeof(string)})!=null,"Native text list export directory/name signature");
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
        foreach(var name in new[]{"ManageProjectLanguage","ManageUnifiedPlantNode","UpdateUnifiedPlantObject","UpdateUnifiedObjectProperties","UpdateUnifiedMultilingualProperty","ExportUnifiedEngineeringList","ManageUnifiedLoggingTag","SetUnifiedLogDuration","ManageUnifiedOpcUaAlarmType","CreatePlcInstanceDb","GeneratePlcSourceFromBlocks","GeneratePlcLoadableFile","RetrieveProjectArchive","ExportProjectTexts","ImportProjectTexts","ManageGlobalLibrary","ManageLibraryFolder","ManagePlcTagDefinition"}) {
            var method=tools.GetMethod(name)!;
            check(Equals(method.GetParameters().Single(p=>p.Name=="dryRun").DefaultValue,true),name+" defaults to preview");
        }
        foreach(var name in new[]{"ExchangePlcSupervisions","ExchangeCfcCharts","ExchangeTestSuiteCase","RunTestSuiteCase","ExchangeMotionCamData","ConfigureMotionHardwareConnection","ManageUnifiedEvent","ManageStartdriveParameter","ManageSiVArcRule","GenerateSiVArc","ManageLibraryMasterCopy","ImportLibraryTypeDocuments","ManageDccChart"}) {
            var method=tools.GetMethod(name)!;
            check(Equals(method.GetParameters().Single(p=>p.Name=="dryRun").DefaultValue,true),name+" defaults to preview");
        }
        check(Equals(tools.GetMethod("RunTestSuiteCase")!.GetParameters().Single(p=>p.Name=="confirmExternalExecution").DefaultValue,false),"External Test Suite execution requires explicit confirmation");
        check(Equals(tools.GetMethod("ExchangeCfcCharts")!.GetParameters().Single(p=>p.Name=="deleteAtTarget").DefaultValue,false),"CFC import does not default to deletion");
        if(core.GetName().Version!.Major==21) {
            var cfc=Assembly.Load("Siemens.Engineering.CFC");
            Method(cfc,"Siemens.Engineering.SW.FunctionCharts.ChartProviderS7","CompleteExport",4);
            Method(cfc,"Siemens.Engineering.SW.FunctionCharts.ChartProviderS7","Import",5);
            var ts=Assembly.Load("Siemens.Engineering.TestSuite");
            foreach(var name in new[]{"StyleGuideGroup","ApplicationTestGroup","SystemTestGroup"})check(T(ts,"Siemens.Engineering.TestSuite.TestSuiteService").GetProperty(name)!=null,"Test Suite root "+name);
            Method(step7,"Siemens.Engineering.SW.Supervision.SupervisionProvider","ExportSupervisionsToXlsx",1);
            Method(step7,"Siemens.Engineering.SW.Supervision.SupervisionProvider","ImportSupervisionsFromXlsx",2);
            foreach(var name in new[]{"LoadCamDataBinary","SaveCamDataBinary"})Method(step7,"Siemens.Engineering.SW.TechnologicalObjects.Motion.CamDataSupport",name,1);
            var sd=Assembly.Load("Siemens.Engineering.Startdrive");
            check(T(sd,"Siemens.Engineering.MC.Drives.DriveObjectContainer").GetProperty("DriveObjects")!=null,"Offline drive container");
            var dcc=Assembly.Load("Siemens.Engineering.DCC");
            check(T(dcc,"Siemens.Engineering.MC.Drives.Dcc.DriveControlChartContainer").GetProperty("Charts")!=null,"DCC charts service");
            Method(dcc,"Siemens.Engineering.MC.Drives.Dcc.DriveControlChart","Export",1);
            Method(dcc,"Siemens.Engineering.MC.Drives.Dcc.DriveControlChart","OptimizeRunSequence",0);
            var sivarc=Assembly.Load("Siemens.Engineering.Sivarc");
            Method(sivarc,"Siemens.Engineering.SiVArc.Sivarc","Generate",3);
        }
    }
}
