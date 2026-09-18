using System;
using System.Linq;
using System.Reflection;

// Native members used by the Unified exchange family (Portal.UnifiedExchange.cs) and the remaining sub-batch 5 types
// (thresholds, substitute values, driver properties, audit classes), verified against the installed API.
internal static class UnifiedExchangeShapeChecks
{
    internal static void Run(Assembly server, Action<bool,string> check)
    {
        Assembly Api(string split,string legacy) { try { return Assembly.Load(split); } catch(System.IO.FileNotFoundException) { return Assembly.Load(legacy); } }
        var unified=Api("Siemens.Engineering.WinCCUnified","Siemens.Engineering");
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
        const string ns="Siemens.Engineering.HmiUnified.";
        var dir=typeof(System.IO.DirectoryInfo);
        var service=T(core,"Siemens.Engineering.IEngineeringService");
        // Tags: WinCC ML exchange at the composition level, root and per table
        Property(unified,ns+"HmiSoftware","Tags","HmiTagComposition");
        Property(unified,ns+"HmiSoftware","TagTables","HmiTagTableComposition");
        Property(unified,ns+"HmiSoftware","TagTableGroups","HmiTagTableGroupComposition");
        Property(unified,ns+"HmiTags.HmiTagTable","Tags","HmiTagComposition");
        Property(unified,ns+"HmiTags.HmiTagTableGroup","TagTables","HmiTagTableComposition");
        Property(unified,ns+"HmiTags.HmiTagTableGroup","Groups","HmiTagTableGroupComposition");
        Method(unified,ns+"HmiTags.HmiTagComposition","Export",new[]{dir});
        Method(unified,ns+"HmiTags.HmiTagComposition","Export",new[]{dir,typeof(string)});
        Method(unified,ns+"HmiTags.HmiTagComposition","Import",new[]{dir},"Boolean");
        Method(unified,ns+"HmiTags.HmiTagComposition","Import",new[]{dir,typeof(string)},"Boolean");
        Method(unified,ns+"HmiTags.HmiTagComposition","Find",new[]{typeof(string)},"HmiTag");
        // Thresholds / substitute value / ranges on a tag (reached through ManageUnifiedObjectParts and nested property edits)
        Property(unified,ns+"HmiTags.HmiTag","Thresholds","HmiThresholdComposition");
        Method(unified,ns+"HmiTags.HmiThresholdComposition","Create",Type.EmptyTypes,"HmiThreshold");
        Method(unified,ns+"HmiTags.HmiThresholdComposition","Find",new[]{typeof(string)},"HmiThreshold");
        Method(unified,ns+"HmiTags.HmiThreshold","Delete",Type.EmptyTypes,"Void");
        Property(unified,ns+"HmiTags.HmiThreshold","Mode","HmiThresholdMode",false);
        Property(unified,ns+"HmiTags.HmiThreshold","Value","Object",true);
        Property(unified,ns+"HmiTags.HmiThreshold","ValueType","HmiLimitValueType",true);
        Property(unified,ns+"HmiTags.HmiTag","SubstituteValue","HmiSubstituteValue",false);
        Property(unified,ns+"HmiTags.HmiSubstituteValue","Value","Object",true);
        Property(unified,ns+"HmiTags.HmiSubstituteValue","SubstituteValueUsage","HmiSubstituteValueUsage",true);
        Property(unified,ns+"HmiTags.HmiTag","InitialMinValue","LowerRange",false);
        Property(unified,ns+"HmiTags.HmiTag","InitialMaxValue","UpperRange",false);
        Property(unified,ns+"HmiTags.HmiSystemTag","DataType");
        // Script modules
        Property(unified,ns+"HmiSoftware","Scripts","HmiScriptModuleComposition");
        Method(unified,ns+"Scripts.HmiScriptModuleComposition","Export",new[]{dir});
        Method(unified,ns+"Scripts.HmiScriptModuleComposition","Import",new[]{dir},"Boolean");
        Method(unified,ns+"Scripts.HmiScriptModuleComposition","Import",new[]{dir,typeof(string)},"Boolean");
        Method(unified,ns+"Scripts.HmiScriptModule","Export",new[]{dir});
        Method(unified,ns+"Scripts.HmiScriptModule","Export",new[]{dir,typeof(string)});
        check(T(unified,ns+"Common.IChromDataExchangeExport").IsAssignableFrom(T(unified,ns+"Scripts.HmiScriptModule")),"HmiScriptModule implements IChromDataExchangeExport");
        Property(unified,ns+"Scripts.HmiScriptModule","Name","String");
        // OPC UA alarm service on a connection
        check(service.IsAssignableFrom(T(unified,ns+"HmiConnections.OpcUaAlarm")),"OpcUaAlarm is an engineering service (HmiConnection.GetService)");
        check(T(core,"Siemens.Engineering.IEngineeringServiceProvider").IsAssignableFrom(T(unified,ns+"HmiConnections.HmiConnection")),"HmiConnection is a service provider");
        Method(unified,ns+"HmiConnections.OpcUaAlarm","Import",new[]{typeof(string)},"Boolean");
        Method(unified,ns+"HmiConnections.OpcUaAlarm","GetNodeId",new[]{typeof(string)},"String");
        Property(unified,ns+"HmiConnections.OpcUaAlarm","DisplayNames");
        Method(unified,ns+"HmiConnections.HmiConnectionComposition","Find",new[]{typeof(string)},"HmiConnection");
        Property(unified,ns+"HmiConnections.HmiConnection","DriverProperties","DriverPropertyComposition");
        Property(unified,ns+"HmiConnections.DriverProperty","PropertyName","String",false);
        Property(unified,ns+"HmiConnections.DriverProperty","Value","String",true);
        Property(unified,ns+"HmiConnections.DriverProperty","Info","String",false);
        // Audit classes (read-only except Name), text/graphic/system text lists
        Property(unified,ns+"HmiSoftware","HmiAlarmAuditClass","HmiAlarmAuditClassComposition");
        foreach(var name in new[]{"CommentRequired","ConfirmationMode","IsGmpEnabled","RequiredFunctionRights"}) Property(unified,ns+"HmiAudit.HmiAuditClass",name,null,false);
        Property(unified,ns+"HmiAudit.HmiAuditClass","Name","String",true);
        Method(unified,ns+"TextGraphicList.HmiTextListComposition","Export",new[]{dir,typeof(string)});
        Method(unified,ns+"TextGraphicList.HmiTextListComposition","Import",new[]{dir,typeof(string)},"Boolean");
        if(unified.GetType(ns+"TextGraphicList.HmiGraphicListComposition")==null && v20) Console.WriteLine("CAPABILITY V20 has no HmiGraphicLists; graphicLists tools return NotSupported.");
        else Method(unified,ns+"TextGraphicList.HmiGraphicListComposition","Import",new[]{dir,typeof(string)},"Boolean");
        // Tool surface
        var tools=server.GetType("TiaMcpServer.ModelContextProtocol.McpServer",true)!;
        foreach(var name in new[]{"ExchangeUnifiedTags","ExchangeUnifiedScriptModules","ImportUnifiedOpcUaAlarms"})
            check(Equals(tools.GetMethod(name)!.GetParameters().Single(p=>p.Name=="dryRun").DefaultValue,true),name+" defaults to preview");
        check(Equals(tools.GetMethod("ImportUnifiedOpcUaAlarms")!.GetParameters().Single(p=>p.Name=="action").DefaultValue,"read"),"ImportUnifiedOpcUaAlarms defaults to read");
    }
}
