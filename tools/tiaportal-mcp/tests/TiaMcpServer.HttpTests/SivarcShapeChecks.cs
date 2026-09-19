using System;
using System.IO;
using System.Linq;
using System.Reflection;

// Native members used by the 2.7.38 phase 6 ⑥-① SiVArc option-package tools (Portal.Sivarc.cs), verified member by member against
// the installed V20 (Siemens.Engineering) or V21 (Siemens.Engineering.Sivarc) PublicAPI. The VM has no SiVArc licence, so this
// file is the only verification the family gets; every tool answers NotSupported when the services are absent at run time.
internal static class SivarcShapeChecks
{
    internal static void Run(Assembly server, Action<bool,string> check)
    {
        Assembly Api(string split,string legacy) { try { return Assembly.Load(split); } catch(FileNotFoundException) { return Assembly.Load(legacy); } }
        var sivarc=Api("Siemens.Engineering.Sivarc","Siemens.Engineering");
        var core=Api("Siemens.Engineering.Base","Siemens.Engineering");
        var step7=Api("Siemens.Engineering.Step7","Siemens.Engineering");
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
        var engineeringService=T(core,"Siemens.Engineering.IEngineeringService"); var engineeringObject=T(core,"Siemens.Engineering.IEngineeringObject");
        var serviceProvider=T(core,"Siemens.Engineering.IEngineeringServiceProvider");
        const string ns="Siemens.Engineering.SiVArc.";
        var text=typeof(string); var file=typeof(FileInfo); var masterCopy=T(core,"Siemens.Engineering.Library.MasterCopies.MasterCopy"); var libraryType=T(core,"Siemens.Engineering.Library.Types.LibraryType");
        var libraryTypeVersion=T(core,"Siemens.Engineering.Library.Types.LibraryTypeVersion"); var deviceItem=T(core,"Siemens.Engineering.HW.DeviceItem");
        var createOptions=T(sivarc,ns+"CreateOptions"); var conditionOperator=T(sivarc,ns+"ConditionOperator"); var generationOptions=T(sivarc,ns+"GenerationOptions");
        // V20 declares the rule references with SiVArc marker interfaces, V21 with IEngineeringObject (SetSivarcReference has both branches).
        string refType(string v20Interface)=>v20 ? v20Interface : "IEngineeringObject";

        // ---- enums ----
        Enum(sivarc,ns+"ConditionOperator","None","And","Equal","NotEqual","GreaterThan","GreaterThanOrEqual","LessThan","LessThanOrEqual");
        Enum(sivarc,ns+"CreateOptions","Replace","Rename");
        Enum(sivarc,ns+"GenerationOptions","None","AllTags","UsedHmiTags","FullGeneration","UserCreatedRules","EnergySuiteRules","AllRules","AdvancedTags");
        check(generationOptions.GetCustomAttribute<FlagsAttribute>()!=null,"GenerationOptions is a [Flags] enum (options combine with |)");
        Enum(sivarc,ns+"MessageType","Error","Warning","Information");

        // ---- project service ----
        check(engineeringService.IsAssignableFrom(T(sivarc,ns+"Sivarc")),"Sivarc is an IEngineeringService (project.GetService<Sivarc>())");
        foreach(var anchor in new[]{"ScreenRules","TagRules","AdvancedTagRules","AlarmRules","CopyRules","TextlistRules"}) Property(sivarc,ns+"Sivarc",anchor,anchor);
        var stringList=typeof(System.Collections.Generic.IEnumerable<string>);
        Method(sivarc,ns+"Sivarc","Generate",new[]{text,stringList,generationOptions},"SivarcGenerationResult");
        Method(sivarc,ns+"Sivarc","Generate",new[]{stringList,stringList,generationOptions},"SivarcGenerationResult");
        Method(sivarc,ns+"Sivarc","GetExpressionResolver",new[]{v20 ? T(step7,"Siemens.Engineering.SW.Blocks.CodeBlock") : engineeringObject,deviceItem,v20 ? T(sivarc,ns+"ISivarcLibraryItem") : engineeringObject},"ExpressionResolver");
        Method(sivarc,ns+"ExpressionResolver","Resolve",new[]{text});
        foreach(var p in new[]{"InstanceName","CallPath","Result"}) Property(sivarc,ns+"ExpressionResult",p,"String",false);
        Property(sivarc,ns+"SivarcGenerationResult","IsGenerationSuccessful","Boolean",false); Property(sivarc,ns+"SivarcGenerationResult","ErrorCount","Int32",false); Property(sivarc,ns+"SivarcGenerationResult","WarningCount","Int32",false); Property(sivarc,ns+"SivarcGenerationResult","Messages","SivarcFeedbackMessageComposition");
        foreach(var p in new[]{("Path","String"),("Description","String"),("MessageType","MessageType"),("DateTime","DateTime"),("ErrorCount","Int32"),("WarningCount","Int32"),("Messages","SivarcFeedbackMessageComposition")}) Property(sivarc,ns+"SivarcFeedbackMessage",p.Item1,p.Item2,false);

        // ---- six rule families: anchor -> folders / tables -> groups / rules ----
        var families=new[]{("Screen","ScreenRules"),("Tag","TagRules"),("AdvancedTag","AdvancedTagRules"),("Alarm","AlarmRules"),("Copy","CopyRules"),("Textlist","TextlistRules")};
        foreach(var (prefix,anchor) in families)
        {
            string rule=ns+prefix+"Rule", group=ns+prefix+"RuleGroup", table=ns+prefix+"RuleTable", folder=ns+prefix+"RuleFolder";
            Property(sivarc,ns+anchor,"Folders",prefix+"RuleFolderComposition"); Property(sivarc,ns+anchor,"Tables",prefix+"RuleTableComposition");
            Property(sivarc,folder,"Name","String",true); Property(sivarc,folder,"Folders",prefix+"RuleFolderComposition"); Property(sivarc,folder,"Tables",prefix+"RuleTableComposition"); Method(sivarc,folder,"Delete",Type.EmptyTypes,"Void");
            Method(sivarc,folder+"Composition","Create",new[]{text},prefix+"RuleFolder"); Method(sivarc,folder+"Composition","Find",new[]{text},prefix+"RuleFolder");
            Property(sivarc,table,"Name","String",true); Property(sivarc,table,"IsDefault","Boolean",false); Property(sivarc,table,"Groups",prefix+"RuleGroupComposition"); Property(sivarc,table,"Rules",prefix+"RuleComposition"); Method(sivarc,table,"Delete",Type.EmptyTypes,"Void");
            check(serviceProvider.IsAssignableFrom(T(sivarc,table)),table+" is a service provider (LibraryTypeInstanceInfo of instantiated tables)");
            Method(sivarc,table+"Composition","Create",new[]{text},prefix+"RuleTable"); Method(sivarc,table+"Composition","CreateFrom",new[]{T(sivarc,table+"TypeVersion")},prefix+"RuleTable"); Method(sivarc,table+"Composition","Find",new[]{text},prefix+"RuleTable");
            check(T(sivarc,table+"Type").IsSubclassOf(libraryType) && T(sivarc,table+"TypeVersion").IsSubclassOf(libraryTypeVersion),table+"Type / TypeVersion derive from LibraryType / LibraryTypeVersion");
            foreach(var t in new[]{rule,group})
            {
                Property(sivarc,t,"Name","String",true); Property(sivarc,t,"Comment","String",true); Property(sivarc,t,"Condition","String",true); Property(sivarc,t,"ConditionOperator","ConditionOperator",true); Property(sivarc,t,"Enabled","Boolean",true);
                Method(sivarc,t,"Delete",Type.EmptyTypes,"Void");
                Method(sivarc,t+"Composition","Create",new[]{text},t.Substring(ns.Length)); Method(sivarc,t+"Composition","Find",new[]{text},t.Substring(ns.Length));
                Method(sivarc,t+"Composition","CreateFrom",new[]{masterCopy},t.Substring(ns.Length)); Method(sivarc,t+"Composition","CreateFrom",new[]{masterCopy,createOptions},t.Substring(ns.Length));
            }
            Property(sivarc,group,"Groups",prefix+"RuleGroupComposition"); Property(sivarc,group,"Rules",prefix+"RuleComposition");
        }
        // family-specific members
        foreach(var t in new[]{ns+"ScreenRule",ns+"ScreenRuleGroup"})
        {
            Property(sivarc,t,"LayoutField","String",true); Property(sivarc,t,"LoopCount","String",true); Method(sivarc,t,"GetLayoutFields",Type.EmptyTypes);
            Property(sivarc,t,"ProgramBlock",refType("ISivarcProgramBlockSource"),true); Property(sivarc,t,"LibraryScreen",refType("ISivarcLibraryItem"),true); Property(sivarc,t,"ScreenObjectLibraryItem",refType("ISivarcLibraryItem"),true);
        }
        foreach(var t in new[]{ns+"TagRule",ns+"AdvancedTagRule"}) { Property(sivarc,t,"TagGroupHierarchy","String",true); Property(sivarc,t,"TagTable","String",true); }
        Property(sivarc,ns+"AdvancedTagRule","ProgramBlock",refType("ISivarcDataBlockSource"),true); Property(sivarc,ns+"AdvancedTagRule","TagLibraryItem",refType("ISivarcLibraryMasterCopy"),true);
        Property(sivarc,ns+"AlarmRule","ProgramBlock",refType("ISivarcProgramBlockSource"),true); Property(sivarc,ns+"AlarmRule","AlarmLibraryItem",refType("ISivarcLibraryMasterCopy"),true);
        Property(sivarc,ns+"CopyRule","FolderStructure","String",true); Property(sivarc,ns+"CopyRule","LibraryObject",refType("ISivarcLibraryItem"),true);
        Property(sivarc,ns+"TextlistRule","ProgramBlock",refType("ISivarcProgramBlockSource"),true); Property(sivarc,ns+"TextlistRule","TextlistLibraryItem",refType("ISivarcLibraryMasterCopy"),true);
        if(v20) foreach(var i in new[]{"ISivarcProgramBlockSource","ISivarcDataBlockSource","ISivarcLibraryItem","ISivarcLibraryMasterCopy"}) check(T(sivarc,ns+i).IsInterface,ns+i+" marker interface (V20 reference properties)");
        // device columns are dynamic attributes on the rule objects (official "Configuring PLC and HMI device")
        Method(core,"Siemens.Engineering.IEngineeringObject","GetAttribute",new[]{text},"Object");
        Method(core,"Siemens.Engineering.IEngineeringObject","SetAttributes",new[]{typeof(System.Collections.Generic.IEnumerable<System.Collections.Generic.KeyValuePair<string,object>>)},"Void");
        Property(core,"Siemens.Engineering.Library.Types.LibraryTypeInstanceInfo","LibraryTypeVersion","LibraryTypeVersion",false);
        Property(sivarc,ns+"ExpressionTableType","Name","String"); check(T(sivarc,ns+"ExpressionTableType").IsSubclassOf(libraryType),"ExpressionTableType derives from LibraryType");

        // ---- block data provider (CodeBlock.GetService<SivarcDataProvider>()) ----
        check(engineeringService.IsAssignableFrom(T(sivarc,ns+"SivarcDataProvider")),"SivarcDataProvider is an IEngineeringService (block service)");
        Property(sivarc,ns+"SivarcDataProvider","TagDefinitions","TagDefinitionComposition"); Property(sivarc,ns+"SivarcDataProvider","TextDefinitions","TextDefinitionComposition");
        Property(sivarc,ns+"TagDefinition","Name","String",true); Property(sivarc,ns+"TagDefinition","Value","String",true); Property(sivarc,ns+"TagDefinition","Comment","String",true); Method(sivarc,ns+"TagDefinition","Delete",Type.EmptyTypes,"Void");
        Method(sivarc,ns+"TagDefinitionComposition","Create",new[]{text},"TagDefinition"); Method(sivarc,ns+"TagDefinitionComposition","Find",new[]{text},"TagDefinition"); Property(sivarc,ns+"TagDefinitionComposition","Count","Int32");
        Property(sivarc,ns+"TextDefinition","Name","String",true); Property(sivarc,ns+"TextDefinition","Expression","String",true); Property(sivarc,ns+"TextDefinition","Comment","String",true); Property(sivarc,ns+"TextDefinition","Text","MultilingualText",false); Method(sivarc,ns+"TextDefinition","Delete",Type.EmptyTypes,"Void");
        Method(sivarc,ns+"TextDefinitionComposition","Create",new[]{text},"TextDefinition"); Method(sivarc,ns+"TextDefinitionComposition","Find",new[]{text},"TextDefinition"); Property(sivarc,ns+"TextDefinitionComposition","Count","Int32");
        if(v20)
        {
            Console.WriteLine("CAPABILITY SiVArc TagMemberSetting / TagMember / AcquisitionMode and LayoutData absent on V20 (tools answer NotSupported there)");
            check(sivarc.GetType(ns+"TagMemberSetting")==null && sivarc.GetType(ns+"LayoutData")==null,"V20 has no TagMemberSetting / LayoutData");
        }
        else
        {
            Property(sivarc,ns+"SivarcDataProvider","TagMemberSettings","TagMemberSetting");
            Property(sivarc,ns+"TagMemberSetting","UseCommonConfiguration","Boolean",true); Property(sivarc,ns+"TagMemberSetting","CommonParameters","TagMember",false); Property(sivarc,ns+"TagMemberSetting","BlockParameters","TagMemberComposition",false);
            Property(sivarc,ns+"TagMember","Name","String"); Property(sivarc,ns+"TagMember","AcquisitionCycle","String",true); Property(sivarc,ns+"TagMember","AcquisitionMode","AcquisitionMode",true); Property(sivarc,ns+"TagMember","Comment","String",true);
            Method(sivarc,ns+"TagMemberComposition","Find",new[]{text},"TagMember"); Property(sivarc,ns+"TagMemberComposition","Count","Int32");
            Enum(sivarc,ns+"AcquisitionMode","None","CyclicContinuous","CyclicInOperation","OnDemand");
            // ---- layout data (screen service, classic Screen and Unified HmiScreen) ----
            check(engineeringService.IsAssignableFrom(T(sivarc,ns+"LayoutData")),"LayoutData is an IEngineeringService (screen.GetService<LayoutData>())");
            Method(sivarc,ns+"LayoutData","Export",new[]{file},"Void"); Method(sivarc,ns+"LayoutData","Import",new[]{file},"LayoutDataImportResult");
            Property(sivarc,ns+"LayoutDataImportResult","State","LayoutImportResultState",false); Property(sivarc,ns+"LayoutDataImportResult","NumberOfLayouts","Int32",false);
            Enum(sivarc,ns+"LayoutImportResultState","None","Success","Warning");
            var wincc=Api("Siemens.Engineering.WinCC","Siemens.Engineering"); var unified=Api("Siemens.Engineering.WinCCUnified","Siemens.Engineering");
            check(serviceProvider.IsAssignableFrom(T(wincc,"Siemens.Engineering.Hmi.Screen.Screen")) && serviceProvider.IsAssignableFrom(T(unified,"Siemens.Engineering.HmiUnified.UI.Screens.HmiScreen")),"classic Screen and Unified HmiScreen are service providers");
        }

        // ---- definitions upgrader (PlcSoftware.GetService<SivarcDefinitionsUpgrader>()) ----
        check(engineeringService.IsAssignableFrom(T(sivarc,ns+"SivarcDefinitionsUpgrader")),"SivarcDefinitionsUpgrader is an IEngineeringService (PLC service)");
        Method(sivarc,ns+"SivarcDefinitionsUpgrader","Upgrade",Type.EmptyTypes,"UpgradeDefinitionsResult");
        Property(sivarc,ns+"UpgradeDefinitionsResult","WarningCount","Int32",false); Property(sivarc,ns+"UpgradeDefinitionsResult","Messages","SivarcFeedbackMessageComposition",false);

        // ---- server side ----
        var portal=server.GetType("TiaMcpServer.Siemens.Portal",true)!;
        foreach(var tool in new[]{"ReadSivarcRuleTree","ManageSivarcRuleContainer","ManageSivarcRule","ReadSivarcBlockDefinitions","ManageSivarcBlockDefinition","ResolveSivarcExpression","ManageSivarcScreenLayout","UpgradeSivarcDefinitions","GenerateSiVArc"})
            check(portal.GetMethod(tool)!=null,"Portal."+tool+" exists");
        check(portal.GetMethod("GenerateSiVArc")!.GetParameters().Any(p=>p.Name=="additionalHmiDeviceNamesJson"),"GenerateSiVArc takes additionalHmiDeviceNamesJson (multi-device overload)");
    }
}
