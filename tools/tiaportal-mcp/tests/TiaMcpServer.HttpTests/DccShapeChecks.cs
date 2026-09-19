using System;
using System.IO;
using System.Linq;
using System.Reflection;

// Native members used by the 2.7.39 phase 6 ⑥-② SINAMICS DCC option-package tools (Portal.Dcc.cs), verified member by member against
// the installed V20 (Siemens.Engineering) or V21 (Siemens.Engineering.DCC) PublicAPI. V21 adds DcbLibraryImporter; the 39 DccException
// classes are identical on both versions and every one is named in Portal.KnownDccExceptions.
internal static class DccShapeChecks
{
    internal static void Run(Assembly server, Action<bool,string> check)
    {
        Assembly Api(string split,string legacy) { try { return Assembly.Load(split); } catch(FileNotFoundException) { return Assembly.Load(legacy); } }
        var dcc=Api("Siemens.Engineering.DCC","Siemens.Engineering");
        var drives=Api("Siemens.Engineering.Startdrive","Siemens.Engineering");
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
        var engineeringService=T(core,"Siemens.Engineering.IEngineeringService"); var engineeringObject=T(core,"Siemens.Engineering.IEngineeringObject");
        const string ns="Siemens.Engineering.MC.Drives.Dcc."; const string ex=ns+"DccExceptions.";
        var text=typeof(string); var boolean=typeof(bool); var u32=typeof(uint);
        var pin=T(dcc,ns+"DccPin"); var chartInterface=T(dcc,ns+"DccChartInterface"); var importOptions=T(dcc,ns+"DccImportOptions"); var statement=T(dcc,ns+"Statement");

        // ---- container / charts ----
        check(engineeringService.IsAssignableFrom(T(dcc,ns+"DriveControlChartContainer")),"DriveControlChartContainer is an IEngineeringService (DriveObject.GetService)");
        Property(dcc,ns+"DriveControlChartContainer","Charts","DriveControlChartComposition",false); Property(dcc,ns+"DriveControlChartContainer","DcbLibraries","DcbLibraryComposition",false);
        Method(dcc,ns+"DriveControlChartComposition","Create",new[]{text},"DriveControlChart"); Method(dcc,ns+"DriveControlChartComposition","Create",Type.EmptyTypes,"DriveControlChart"); Method(dcc,ns+"DriveControlChartComposition","Find",new[]{text},"DriveControlChart");
        Method(dcc,ns+"DriveControlChartComposition","Export",new[]{text},"Void"); Method(dcc,ns+"DriveControlChartComposition","Import",new[]{text,importOptions},"DccImportResultData"); Method(dcc,ns+"DriveControlChartComposition","GetChartSequence",Type.EmptyTypes,"IList`1");
        Property(dcc,ns+"DccImportResultData","RemappedParameterNumbers","IDictionary`2",false);
        check(T(dcc,ns+"DccImportOptions").IsEnum && System.Enum.IsDefined(importOptions,"None") && System.Enum.IsDefined(importOptions,"RenameOnConflict"),"DccImportOptions None / RenameOnConflict");
        check(T(dcc,ns+"DriveControlChart").IsSubclassOf(statement) && T(dcc,ns+"DccBlock").IsSubclassOf(statement),"DriveControlChart and DccBlock derive from Statement");
        Property(dcc,ns+"Statement","Name","String",true); Property(dcc,ns+"Statement","Comment","String",true); Property(dcc,ns+"Statement","Partition","DccChartPartition",true); Property(dcc,ns+"Statement","PositionX","Int32",true); Property(dcc,ns+"Statement","PositionY","Int32",true);
        Method(dcc,ns+"Statement","Delete",Type.EmptyTypes,"Void"); Method(dcc,ns+"Statement","MoveInRuntimeSequence",new[]{u32},"Void");
        Method(dcc,ns+"DccBlock","SetAsPredecessor",Type.EmptyTypes,"Void"); Method(dcc,ns+"DriveControlChart","SetAsPredecessor",Type.EmptyTypes,"Void");   // declared per class, not on Statement
        Property(dcc,ns+"DriveControlChart","Blocks","DccBlockComposition",false); Property(dcc,ns+"DriveControlChart","Subcharts","DriveControlChartComposition",false); Property(dcc,ns+"DriveControlChart","ChartInterfaces","DccChartInterfaceComposition",false); Property(dcc,ns+"DriveControlChart","Partitions","DccChartPartitionComposition",false);
        Property(dcc,ns+"DriveControlChart","HorizontalSheets","Int32",true); Property(dcc,ns+"DriveControlChart","VerticalSheets","Int32",true); Property(dcc,ns+"DriveControlChart","SheetWidth","Int32",false); Property(dcc,ns+"DriveControlChart","SheetHeight","Int32",false);
        Method(dcc,ns+"DriveControlChart","Export",new[]{text},"Void"); Method(dcc,ns+"DriveControlChart","GetRunSequence",Type.EmptyTypes,"IList`1"); Method(dcc,ns+"DriveControlChart","OptimizeRunSequence",Type.EmptyTypes,"Void"); Method(dcc,ns+"DriveControlChart","ShowDccEditor",Type.EmptyTypes,"Void");
        Method(dcc,ns+"DccChartPartitionComposition","Create",new[]{text},"DccChartPartition"); Method(dcc,ns+"DccChartPartitionComposition","Find",new[]{text},"DccChartPartition");
        Property(dcc,ns+"DccChartPartition","Name","String",true); Property(dcc,ns+"DccChartPartition","Comment","String",true); Method(dcc,ns+"DccChartPartition","Delete",Type.EmptyTypes,"Void");

        // ---- blocks / pins / parameters / connections / interfaces ----
        Method(dcc,ns+"DccBlockComposition","Create",new[]{text},"DccBlock"); Method(dcc,ns+"DccBlockComposition","Create",new[]{text,text},"DccBlock"); Method(dcc,ns+"DccBlockComposition","Create",new[]{text,text,text},"DccBlock"); Method(dcc,ns+"DccBlockComposition","Find",new[]{text},"DccBlock");
        Property(dcc,ns+"DccBlock","GenericInputsNumber","UInt16",true); Property(dcc,ns+"DccBlock","Pins","DccPinComposition",false);
        Method(dcc,ns+"DccPinComposition","Find",new[]{text},"DccPin");
        Property(dcc,ns+"DccPin","Name","String",false); Property(dcc,ns+"DccPin","Comment","String",true); Property(dcc,ns+"DccPin","Value","Object",true); Property(dcc,ns+"DccPin","Unit","String",true);
        Property(dcc,ns+"DccPin","IsInput","Boolean",false); Property(dcc,ns+"DccPin","Invisible","Boolean",true); Property(dcc,ns+"DccPin","ForTest","Boolean",true); Property(dcc,ns+"DccPin","IsPublished","Boolean",false);
        Property(dcc,ns+"DccPin","Parameter","DccParameter",false); Property(dcc,ns+"DccPin","Connections","DccConnectionComposition",false);
        Method(dcc,ns+"DccPin","Connect",new[]{pin},"DccConnection"); Method(dcc,ns+"DccPin","Connect",new[]{chartInterface},"DccConnection");
        Method(dcc,ns+"DccPin","Publish",new[]{boolean},"DccParameter"); Method(dcc,ns+"DccPin","Publish",new[]{boolean,u32},"DccParameter"); Method(dcc,ns+"DccPin","Publish",new[]{u32,u32,boolean},"DccParameter"); Method(dcc,ns+"DccPin","Unpublish",Type.EmptyTypes,"Void");
        Property(dcc,ns+"DccParameter","Number","Int32",true); Property(dcc,ns+"DccParameter","ArrayIndex","Int32",true); Property(dcc,ns+"DccParameter","ParameterText","String",true); Property(dcc,ns+"DccParameter","IsSink","Boolean",false); Property(dcc,ns+"DccParameter","IsSignal","Boolean",true);
        Property(dcc,ns+"DccConnection","Sink","IEngineeringObject",false); Property(dcc,ns+"DccConnection","Source","IEngineeringObject",false); Method(dcc,ns+"DccConnection","Delete",Type.EmptyTypes,"Void");
        Method(dcc,ns+"DccChartInterfaceComposition","Create",new[]{pin},"DccChartInterface");
        Property(dcc,ns+"DccChartInterface","Name","String",false); Property(dcc,ns+"DccChartInterface","Comment","String",true); Property(dcc,ns+"DccChartInterface","Value","Object",true); Property(dcc,ns+"DccChartInterface","Unit","String",true);
        Property(dcc,ns+"DccChartInterface","IsInput","Boolean",false); Property(dcc,ns+"DccChartInterface","Invisible","Boolean",true); Property(dcc,ns+"DccChartInterface","ForTest","Boolean",true); Property(dcc,ns+"DccChartInterface","Pin","DccPin",false); Property(dcc,ns+"DccChartInterface","Connections","DccConnectionComposition",false);
        Method(dcc,ns+"DccChartInterface","Delete",Type.EmptyTypes,"Void");
        check(T(dcc,ns+"IDccObject").IsInterface && T(dcc,ns+"IOVariable").IsInterface && T(dcc,ns+"IOVariable").GetInterfaces().Contains(T(dcc,ns+"IDccObject")),"IDccObject / IOVariable interfaces (pins and chart interfaces)");
        check(T(dcc,ns+"IDccObject").IsAssignableFrom(pin) && T(dcc,ns+"IOVariable").IsAssignableFrom(chartInterface) && T(dcc,ns+"IDccObject").IsAssignableFrom(statement),"DccPin / DccChartInterface / Statement implement the DCC interfaces");

        // ---- DCB libraries ----
        Method(dcc,ns+"DcbLibraryComposition","Find",new[]{text},"DcbLibrary");
        Property(dcc,ns+"DcbLibrary","LibraryName","String",false); Property(dcc,ns+"DcbLibrary","Version","Version",false); Property(dcc,ns+"DcbLibrary","BlockTypes","DcbBlockTypeComposition",false);
        Method(dcc,ns+"DcbBlockTypeComposition","Find",new[]{text},"DcbBlockType"); Property(dcc,ns+"DcbBlockType","Name","String",false); Property(dcc,ns+"DcbBlockType","Description","String",false);
        check(T(dcc,ns+"DcbBlockTypeLibraryType").IsSubclassOf(T(core,"Siemens.Engineering.Library.Types.LibraryType")),"DcbBlockTypeLibraryType derives from LibraryType (typeKind dccBlockType)");
        if(v20) check(dcc.GetType(ns+"DcbLibraryImporter",false)==null,"DcbLibraryImporter is absent on V20 (V21 addition)");
        else { check(engineeringService.IsAssignableFrom(T(dcc,ns+"DcbLibraryImporter")),"DcbLibraryImporter is an IEngineeringService (project library)"); Method(dcc,ns+"DcbLibraryImporter","ImportDcbLibrary",new[]{text},"Void"); }

        // ---- exceptions: every concrete class derives from DccException : EngineeringTargetInvocationException ----
        var dccException=T(dcc,ex+"DccException"); check(dccException.IsSubclassOf(T(core,"Siemens.Engineering.EngineeringTargetInvocationException")),"DccException derives from EngineeringTargetInvocationException");
        var exceptions=dcc.GetExportedTypes().Where(t=>t.Namespace==ns+"DccExceptions").ToArray();
        check(exceptions.Length==39 && exceptions.All(t=>t==dccException || t.IsSubclassOf(dccException)),"39 DCC exception classes, all derived from DccException ("+exceptions.Length+")");
        check(T(dcc,ex+"DccImportException").IsSubclassOf(dccException) && T(dcc,ex+"DccLicenseUnavailableException").IsSubclassOf(dccException) && T(dcc,ex+"DccExportException").IsSubclassOf(dccException),"DccImportException / DccLicenseUnavailableException / DccExportException families");
        var portal=server.GetType("TiaMcpServer.Siemens.Portal",true)!;
        var known=(Type[])portal.GetField("KnownDccExceptions",BindingFlags.NonPublic|BindingFlags.Static)!.GetValue(null)!;
        check(known.Length==39 && exceptions.All(t=>known.Contains(t)),"Portal.KnownDccExceptions names all 39 DCC exception classes");

        // ---- server side ----
        foreach(var tool in new[]{"ReadDccCharts","ManageDccChart","ManageDccBlock","ManageDccPin","ManageDccChartInterface","ManageDccChartPartition","ManageDcbLibraries","ReadDccObject"})
            check(portal.GetMethod(tool)!=null,"Portal."+tool+" exists");
        check(portal.GetMethod("ManageDccChart")!.GetParameters().Any(p=>p.Name=="confirmDelete") && portal.GetMethod("ManageDccChart")!.GetParameters().Any(p=>p.Name=="driveObjectIndex"),"ManageDccChart takes confirmDelete and driveObjectIndex");
    }
}
