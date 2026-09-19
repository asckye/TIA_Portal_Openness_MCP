using System;
using System.IO;
using System.Linq;
using System.Reflection;

// Native members used by the 2.7.35 Step7 sub-batch 2 (Portal.Step7Leftovers.cs and the typed retrofits: external sources,
// system block / type groups, tag table constants, alarm text list XLSX, watch / force table entries, ProDiag CSV export,
// typed access rules, OPC UA communication group / namespace restrictions, alarm class and supervision result messages,
// STEP 7 library type subclasses, user group classes), verified member by member against the installed V20
// (Siemens.Engineering) or V21 (Siemens.Engineering.Base / Siemens.Engineering.Step7) PublicAPI.
internal static class Step7LeftoversShapeChecks
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
        const string sw="Siemens.Engineering.SW.", ext="Siemens.Engineering.SW.ExternalSources.", blocks="Siemens.Engineering.SW.Blocks.", types="Siemens.Engineering.SW.Types.", tags="Siemens.Engineering.SW.Tags.",
            alarm="Siemens.Engineering.SW.Alarm.", tables="Siemens.Engineering.SW.WatchAndForceTables.", hw="Siemens.Engineering.HW.", opc="Siemens.Engineering.SW.OpcUa.";
        var text=typeof(string); var file=typeof(FileInfo); var directory=typeof(DirectoryInfo);
        var masterCopy=T(core,"Siemens.Engineering.Library.MasterCopies.MasterCopy"); var masterCopyMode=T(core,"Siemens.Engineering.Library.MasterCopies.MasterCopyMode");

        // ---- external sources ----
        Property(step7,sw+"PlcSoftware","ExternalSourceGroup","PlcExternalSourceSystemGroup"); Property(step7,"Siemens.Engineering.SW.Units.PlcUnitBase","ExternalSourceGroup","PlcExternalSourceSystemGroup");
        check(T(step7,ext+"PlcExternalSourceSystemGroup").IsSubclassOf(T(step7,ext+"PlcExternalSourceGroup")) && T(step7,ext+"PlcExternalSourceUserGroup").IsSubclassOf(T(step7,ext+"PlcExternalSourceGroup")),"PlcExternalSourceSystemGroup / UserGroup derive from PlcExternalSourceGroup");
        Property(step7,ext+"PlcExternalSourceGroup","Name","String"); Property(step7,ext+"PlcExternalSourceGroup","ExternalSources","PlcExternalSourceComposition"); Property(step7,ext+"PlcExternalSourceGroup","Groups","PlcExternalSourceUserGroupComposition");
        Method(step7,ext+"PlcExternalSourceUserGroup","Delete",Type.EmptyTypes,"Void");
        if(v20) { Console.WriteLine("CAPABILITY PlcExternalSourceUserGroup.Name is read-only on V20 (ManagePlcExternalSources renameGroup answers NotSupported there)"); Property(step7,ext+"PlcExternalSourceUserGroup","Name","String",false); }
        else Property(step7,ext+"PlcExternalSourceUserGroup","Name","String",true);
        Method(step7,ext+"PlcExternalSourceUserGroupComposition","Create",new[]{text},"PlcExternalSourceUserGroup"); Method(step7,ext+"PlcExternalSourceUserGroupComposition","Find",new[]{text},"PlcExternalSourceUserGroup");
        Method(step7,ext+"PlcExternalSourceComposition","CreateFromFile",new[]{text,text},"PlcExternalSource"); Method(step7,ext+"PlcExternalSourceComposition","Find",new[]{text},"PlcExternalSource");
        Method(step7,ext+"PlcExternalSourceComposition","CreateFrom",new[]{masterCopy},"PlcExternalSource"); Method(step7,ext+"PlcExternalSourceComposition","CreateFrom",new[]{masterCopy,masterCopyMode},"PlcExternalSource");
        Property(step7,ext+"PlcExternalSource","Name","String",false); Method(step7,ext+"PlcExternalSource","Delete",Type.EmptyTypes,"Void");
        var option=T(step7,ext+"GenerateBlockOption");
        Method(step7,ext+"PlcExternalSource","GenerateBlocksFromSource",Type.EmptyTypes,"Void");
        foreach(var sig in new[]{new[]{option},new[]{T(step7,blocks+"PlcBlockUserGroup"),option},new[]{T(step7,types+"PlcTypeUserGroup"),option}})
        { var m=T(step7,ext+"PlcExternalSource").GetMethod("GenerateBlocksFromSource",sig); check(m!=null && m.ReturnType.IsGenericType && m.ReturnType.GetGenericArguments()[0]==T(core,"Siemens.Engineering.IEngineeringObject"),"PlcExternalSource.GenerateBlocksFromSource("+string.Join(",",sig.Select(x=>x.Name))+") -> IList<IEngineeringObject>"); }
        Enum(step7,ext+"GenerateBlockOption","None","KeepOnError"); Enum(step7,ext+"GenerateOptions","None","WithDependencies");
        Method(step7,ext+"PlcExternalSourceSystemGroup","GenerateSource",new[]{typeof(System.Collections.Generic.IEnumerable<>).MakeGenericType(T(step7,ext+"IGenerateSource")),file,T(step7,ext+"GenerateOptions")},"Void");

        // ---- system groups / user groups ----
        Property(step7,blocks+"PlcBlockSystemGroup","SystemBlockGroups","PlcSystemBlockGroupComposition"); Property(step7,blocks+"PlcSystemBlockGroup","Name","String"); Property(step7,blocks+"PlcSystemBlockGroup","Blocks","PlcBlockComposition"); Property(step7,blocks+"PlcSystemBlockGroup","Groups","PlcSystemBlockGroupComposition");
        Method(step7,blocks+"PlcSystemBlockGroupComposition","Find",new[]{text},"PlcSystemBlockGroup");
        Property(step7,types+"PlcTypeSystemGroup","SystemTypeGroups","PlcSystemTypeGroupComposition"); Property(step7,types+"PlcSystemTypeGroup","Name","String"); Property(step7,types+"PlcSystemTypeGroup","Types","PlcTypeComposition");
        foreach(var g in new[]{types+"PlcTypeUserGroup",tags+"PlcTagTableUserGroup",tables+"PlcWatchAndForceTableUserGroup"}) { Property(step7,g,"Name","String"); Method(step7,g,"Delete",Type.EmptyTypes,"Void"); }
        check(T(step7,types+"PlcTypeUserGroup").IsSubclassOf(T(step7,types+"PlcTypeGroup")) && T(step7,tags+"PlcTagTableUserGroup").IsSubclassOf(T(step7,tags+"PlcTagTableGroup")) && T(step7,tables+"PlcWatchAndForceTableUserGroup").IsSubclassOf(T(step7,tables+"PlcWatchAndForceTableGroup")),"user groups derive from their group base classes");
        Property(step7,tables+"PlcWatchAndForceTableGroup","WatchTables","PlcWatchTableComposition"); Property(step7,tables+"PlcWatchAndForceTableGroup","ForceTables","PlcForceTableComposition"); Property(step7,tables+"PlcWatchAndForceTableGroup","Groups","PlcWatchAndForceTableUserGroupComposition");

        // ---- constants ----
        Property(step7,tags+"PlcTagTable","UserConstants","PlcUserConstantComposition"); Property(step7,tags+"PlcTagTable","SystemConstants","PlcSystemConstantComposition"); Property(step7,tags+"PlcTagTable","IsDefault","Boolean");
        check(T(step7,tags+"PlcUserConstant").IsSubclassOf(T(step7,tags+"PlcConstant")) && T(step7,tags+"PlcSystemConstant").IsSubclassOf(T(step7,tags+"PlcConstant")),"PlcUserConstant / PlcSystemConstant derive from PlcConstant");
        Property(step7,tags+"PlcConstant","Name","String",false); Property(step7,tags+"PlcConstant","DataTypeName","String",false); Property(step7,tags+"PlcConstant","Value","String",false);

        // ---- alarm text lists XLSX ----
        Service(alarm+"PlcAlarmTextListProvider");
        Method(step7,alarm+"PlcAlarmTextListProvider","ExportToXlsx",new[]{file},"TextListXlsxResult");
        Method(step7,alarm+"PlcAlarmTextListProvider","ExportToXlsx",new[]{file,typeof(System.Collections.Generic.IEnumerable<string>),typeof(System.Collections.Generic.IEnumerable<>).MakeGenericType(T(core,"Siemens.Engineering.Language"))},"TextListXlsxResult");
        Method(step7,alarm+"PlcAlarmTextListProvider","ImportFromXlsx",new[]{file,T(core,"Siemens.Engineering.ImportOptions")},"TextListXlsxResult");
        Property(step7,alarm+"TextListXlsxResult","State","TextListXlsxResultState"); Property(step7,alarm+"TextListXlsxResult","LogFilePath","FileInfo");
        Enum(step7,alarm+"TextListXlsxResultState","OK","Warning","Error"); Enum(core,"Siemens.Engineering.ImportOptions","None","Override");
        Method(core,"Siemens.Engineering.LanguageComposition","Find",new[]{typeof(System.Globalization.CultureInfo)},"Language"); Property(core,"Siemens.Engineering.LanguageSettings","Languages","LanguageComposition");
        check(serviceProvider.IsAssignableFrom(T(step7,"Siemens.Engineering.SW.Units.PlcUnitBase")),"PlcUnitBase is a service provider (unit-level PlcAlarmTextListProvider)");

        // ---- alarm class / supervision result messages ----
        Service(alarm+"AlarmClassDataProvider"); Method(step7,alarm+"AlarmClassDataProvider","Export",new[]{file},"AlarmClassExportImportResult"); Method(step7,alarm+"AlarmClassDataProvider","Import",new[]{file},"AlarmClassExportImportResult");
        Property(step7,alarm+"AlarmClassExportImportResult","State","AlarmClassExportImportResultState"); Property(step7,alarm+"AlarmClassExportImportResult","ErrorCount","Int32"); Property(step7,alarm+"AlarmClassExportImportResult","WarningCount","Int32"); Property(step7,alarm+"AlarmClassExportImportResult","Messages","AlarmClassExportImportResultMessageComposition");
        Property(step7,alarm+"AlarmClassExportImportResultMessage","Message","String",false); Property(step7,alarm+"AlarmClassExportImportResultMessage","State","AlarmClassExportImportResultState",false);
        Enum(step7,alarm+"AlarmClassExportImportResultState","Success","Warning","Error");
        Property(step7,"Siemens.Engineering.SW.Supervision.SupervisionSettingsExportImportResult","Messages","SupervisionSettingsExportImportResultMessageComposition"); Property(step7,"Siemens.Engineering.SW.Supervision.SupervisionSettingsExportImportResult","ErrorCount","Int32"); Property(step7,"Siemens.Engineering.SW.Supervision.SupervisionSettingsExportImportResult","WarningCount","Int32");
        Property(step7,"Siemens.Engineering.SW.Supervision.SupervisionSettingsExportImportResultMessage","Message","String",false);

        // ---- watch / force table entries ----
        Property(step7,tables+"PlcWatchTable","Entries","PlcTableCommentEntryComposition"); Property(step7,tables+"PlcForceTable","Entries","PlcTableCommentEntryComposition"); Property(step7,tables+"PlcWatchTable","IsConsistent","Boolean");
        Method(step7,tables+"PlcTableCommentEntryComposition","Create",Type.EmptyTypes,"PlcTableCommentEntry"); Method(step7,tables+"PlcTableCommentEntry","Delete",Type.EmptyTypes,"Void");
        check(T(step7,tables+"PlcWatchTableEntry").IsSubclassOf(T(step7,tables+"PlcTableCommentEntry")) && T(step7,tables+"PlcForceTableEntry").IsSubclassOf(T(step7,tables+"PlcTableCommentEntry")),"PlcWatchTableEntry / PlcForceTableEntry derive from PlcTableCommentEntry");
        foreach(var p in new[]{"Name","Address","DisplayFormat","MonitorTrigger","ModifyTrigger","ModifyValue","ModifyIntention"}) Property(step7,tables+"PlcWatchTableEntry",p);
        foreach(var p in new[]{"Name","Address","DisplayFormat","MonitorTrigger","ForceValue","ForceIntention"}) Property(step7,tables+"PlcForceTableEntry",p);

        // ---- ProDiag ----
        Method(step7,blocks+"CodeBlock","ExportProDIAGInfo",new[]{directory},"Void"); check(T(step7,blocks+"FB").IsSubclassOf(T(step7,blocks+"CodeBlock")),"FB derives from CodeBlock");
        Enum(step7,blocks+"ProgrammingLanguage","ProDiag");

        // ---- typed access rules (Siemens.Engineering.HW namespace, but shipped in the Step7 assembly on V21) ----
        Assembly Hw(string n) => step7.GetType(n) != null ? step7 : core;
        var access=T(Hw(hw+"WatchAndForceTableAccess"),hw+"WatchAndForceTableAccess"); var watchTable=T(step7,tables+"PlcWatchTable"); var forceTable=T(step7,tables+"PlcForceTable");
        Property(Hw(hw+"WatchTableAccessRule"),hw+"WatchTableAccessRule","Access","WatchAndForceTableAccess",true); Property(Hw(hw+"WatchTableAccessRule"),hw+"WatchTableAccessRule","WatchTable","PlcWatchTable"); Method(Hw(hw+"WatchTableAccessRule"),hw+"WatchTableAccessRule","Delete",Type.EmptyTypes,"Void");
        Property(Hw(hw+"ForceTableAccessRule"),hw+"ForceTableAccessRule","Access","WatchAndForceTableAccess",true); Property(Hw(hw+"ForceTableAccessRule"),hw+"ForceTableAccessRule","ForceTable","PlcForceTable"); Method(Hw(hw+"ForceTableAccessRule"),hw+"ForceTableAccessRule","Delete",Type.EmptyTypes,"Void");
        Method(Hw(hw+"WatchTableAccessRuleComposition"),hw+"WatchTableAccessRuleComposition","Create",new[]{watchTable,access},"WatchTableAccessRule"); Method(Hw(hw+"WatchTableAccessRuleComposition"),hw+"WatchTableAccessRuleComposition","Find",new[]{watchTable},"WatchTableAccessRule");
        Method(Hw(hw+"ForceTableAccessRuleComposition"),hw+"ForceTableAccessRuleComposition","Create",new[]{forceTable,access},"ForceTableAccessRule"); Method(Hw(hw+"ForceTableAccessRuleComposition"),hw+"ForceTableAccessRuleComposition","Find",new[]{forceTable},"ForceTableAccessRule");

        // ---- OPC UA communication group / namespace restrictions ----
        Property(step7,opc+"OpcUaProvider","CommunicationGroup","OpcUaCommunicationGroup"); Property(step7,opc+"OpcUaCommunicationGroup","ServerInterfaceGroup","ServerInterfaceGroup");
        if(v20) { Console.WriteLine("CAPABILITY NamespaceAccessRestriction absent on V20 (restriction rows fall back to scalar reads)"); check(step7.GetType(opc+"AccessControl.NamespaceAccessRestriction")==null,"V20 has no NamespaceAccessRestriction"); }
        else
        {
            Property(step7,opc+"AccessControl.NamespaceAccessRestriction","NamespaceIndex","Int32",false); Property(step7,opc+"AccessControl.NamespaceAccessRestriction","NamespaceUri","String",false);
            foreach(var p in new[]{"ApplyRestrictionsToBrowse","SessionRequired","SigningRequired","EncryptionRequired"}) Property(step7,opc+"AccessControl.NamespaceAccessRestriction",p,"Boolean",true);
        }

        // ---- STEP 7 library type subclasses ----
        foreach(var t in new[]{blocks+"CodeBlockLibraryType",types+"PlcTypeLibraryType",types+"PlcDocumentLibraryType"}) { check(T(step7,t).IsSubclassOf(T(core,"Siemens.Engineering.Library.Types.LibraryType")),t+" derives from LibraryType"); Property(step7,t,"Name","String"); }
    }
}
