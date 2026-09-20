using System;
using System.IO;
using System.Linq;
using System.Reflection;

// Native members used by the 2.7.42 phase 6 ⑥-③ Safety Validation Assistant tools (Portal.SafetyValidation.cs), verified member by
// member against the installed V21 PublicAPI (Siemens.Engineering.SafetyValidation). The namespace does not exist on V20, where the
// tools answer NotSupportedOnVersion; the V20 run only pins that absence.
internal static class SafetyValidationShapeChecks
{
    internal static void Run(Assembly server, Action<bool,string> check)
    {
        Assembly Api(string split,string legacy) { try { return Assembly.Load(split); } catch(FileNotFoundException) { return Assembly.Load(legacy); } }
        var core=Api("Siemens.Engineering.Base","Siemens.Engineering");
        bool v20=core.GetName().Version!.Major==20;
        var portal=server.GetType("TiaMcpServer.Siemens.Portal",true)!;
        foreach(var tool in new[]{"ReadSafetyActivationTests","ManageSafetyActivationTest","ManageSafetyActivationTestGroup","ManageSafetyFunction","ManageSafetyFunctionCondition"})
            check(portal.GetMethod(tool)!=null,"Portal."+tool+" exists");
        check(portal.GetMethod("ManageSafetyActivationTest")!.GetParameters().Single(p=>p.Name=="confirmDelete").HasDefaultValue && Equals(portal.GetMethod("ManageSafetyActivationTest")!.GetParameters().Single(p=>p.Name=="confirmDelete").DefaultValue,false),"ManageSafetyActivationTest delete needs confirmDelete");
        // core option enums shared with the family (Export / Import / DocumentInfo)
        void Enum(Assembly a,string t,params string[] names)
        {
            var type=a.GetType(t,true)!; var missing=names.Where(n=>!System.Enum.IsDefined(type,n)).ToArray();
            check(type.IsEnum && missing.Length==0,t+" defines "+string.Join("/",names)+(missing.Length==0?"":" (missing "+string.Join(",",missing)+")"));
        }
        Enum(core,"Siemens.Engineering.ExportOptions","None","WithDefaults","WithReadOnly");
        Enum(core,"Siemens.Engineering.ImportOptions","None","Override","SkipInactiveCultures","ActivateInactiveCultures");
        Enum(core,"Siemens.Engineering.DocumentInfoOptions","None","ExportSetting","InstalledProducts","CreatedTimeStamp","All");
        if(v20)
        {
            check(core.GetType("Siemens.Engineering.SafetyValidation.SafetyValidationAssistant",false)==null,"SafetyValidation namespace is absent on V20 (tools answer NotSupportedOnVersion)");
            bool loadable; try { Assembly.Load("Siemens.Engineering.SafetyValidation"); loadable=true; } catch(FileNotFoundException) { loadable=false; }
            check(!loadable,"Siemens.Engineering.SafetyValidation.dll is a V21 addition");
            return;
        }
        var sv=Assembly.Load("Siemens.Engineering.SafetyValidation");
        var hw=Api("Siemens.Engineering.Base","Siemens.Engineering"); var library=Api("Siemens.Engineering.Base","Siemens.Engineering");
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
        var engineeringService=T(core,"Siemens.Engineering.IEngineeringService"); var serviceProvider=T(core,"Siemens.Engineering.IEngineeringServiceProvider");
        const string ns="Siemens.Engineering.SafetyValidation.";
        var text=typeof(string); var file=typeof(FileInfo); var deviceItem=T(hw,"Siemens.Engineering.HW.DeviceItem"); var masterCopy=T(library,"Siemens.Engineering.Library.MasterCopies.MasterCopy");
        var exportOptions=T(core,"Siemens.Engineering.ExportOptions"); var importOptions=T(core,"Siemens.Engineering.ImportOptions"); var documentInfo=T(core,"Siemens.Engineering.DocumentInfoOptions");

        // ---- enums (names mirrored in SafetyValidationLogic) ----
        Enum(sv,ns+"ActivationTestImportOptions","None","Override","Rename","SkipInactiveCultures","ActivateInactiveCultures");
        Enum(sv,ns+"SignalUsage","OperatingMode","InputCondition","Response");
        Enum(sv,ns+"TestState","NotTested","Succeeded","Failed");
        Enum(sv,ns+"ConditionValue","FALSE","TRUE","NotRelevant");
        Enum(sv,ns+"TestValidationResultState","Okay","Error");

        // ---- assistant / device query ----
        check(engineeringService.IsAssignableFrom(T(sv,ns+"SafetyValidationAssistant")),"SafetyValidationAssistant is an IEngineeringService (Project.GetService)");
        check(serviceProvider.IsAssignableFrom(T(sv,ns+"SafetyValidationAssistant")),"SafetyValidationAssistant is a service provider (DeviceQuery)");
        check(serviceProvider.IsAssignableFrom(T(core,"Siemens.Engineering.ProjectBase")),"ProjectBase is a service provider");
        Property(sv,ns+"SafetyValidationAssistant","ActivationTests","ActivationTestComposition",false); Property(sv,ns+"SafetyValidationAssistant","ActivationTestGroups","ActivationTestUserGroupComposition",false);
        check(engineeringService.IsAssignableFrom(T(sv,ns+"DeviceQuery")),"DeviceQuery is an IEngineeringService"); Method(sv,ns+"DeviceQuery","EvaluationDevices",Type.EmptyTypes,"IList`1");

        // ---- activation tests / groups ----
        Property(sv,ns+"ActivationTest","Name","String",true); Property(sv,ns+"ActivationTest","Author","String",true); Property(sv,ns+"ActivationTest","EvaluationDeviceName","String",false);
        Property(sv,ns+"ActivationTest","OverallState","TestState",false); Property(sv,ns+"ActivationTest","SafetyFunctions","SafetyFunctionComposition",false);
        Method(sv,ns+"ActivationTest","AvailableDevices",Type.EmptyTypes,"IList`1"); Method(sv,ns+"ActivationTest","ChangeEvaluationDevice",new[]{deviceItem},"Void"); Method(sv,ns+"ActivationTest","Delete",Type.EmptyTypes,"Void");
        Method(sv,ns+"ActivationTest","Export",new[]{file,exportOptions},"Void"); Method(sv,ns+"ActivationTest","Export",new[]{file,exportOptions,documentInfo},"Void");
        check(serviceProvider.IsAssignableFrom(T(sv,ns+"ActivationTest")),"ActivationTest is a service provider (TestValidity / ActivationTestPrintout)");
        Method(sv,ns+"ActivationTestComposition","Create",new[]{text,deviceItem},"ActivationTest"); Method(sv,ns+"ActivationTestComposition","CreateFrom",new[]{masterCopy},"ActivationTest");
        Method(sv,ns+"ActivationTestComposition","CreateFrom",new[]{T(sv,ns+"ActivationTest")},"ActivationTest"); Method(sv,ns+"ActivationTestComposition","Find",new[]{text},"ActivationTest");
        Method(sv,ns+"ActivationTestComposition","Import",new[]{file,T(sv,ns+"ActivationTestImportOptions")},"IList`1");
        Property(sv,ns+"ActivationTestUserGroup","Name","String",true); Property(sv,ns+"ActivationTestUserGroup","ActivationTests","ActivationTestComposition",false); Property(sv,ns+"ActivationTestUserGroup","Groups","ActivationTestUserGroupComposition",false);
        Method(sv,ns+"ActivationTestUserGroup","Delete",Type.EmptyTypes,"Void");
        Method(sv,ns+"ActivationTestUserGroupComposition","Create",new[]{text},"ActivationTestUserGroup"); Method(sv,ns+"ActivationTestUserGroupComposition","CreateFrom",new[]{masterCopy},"ActivationTestUserGroup"); Method(sv,ns+"ActivationTestUserGroupComposition","Find",new[]{text},"ActivationTestUserGroup");
        check(engineeringService.IsAssignableFrom(T(sv,ns+"ActivationTestPrintout")),"ActivationTestPrintout is an IEngineeringService"); Method(sv,ns+"ActivationTestPrintout","Generate",new[]{file},"Void");
        check(engineeringService.IsAssignableFrom(T(sv,ns+"TestValidity")),"TestValidity is an IEngineeringService"); Method(sv,ns+"TestValidity","CheckValidity",Type.EmptyTypes,"TestValidationResult");
        Property(sv,ns+"TestValidationResult","State","TestValidationResultState",false); Property(sv,ns+"TestValidationResult","ErrorCount","UInt32",false); Property(sv,ns+"TestValidationResult","Messages","IEnumerable`1",false);

        // ---- safety functions / conditions / trace ----
        Property(sv,ns+"SafetyFunction","Name","String",false); Property(sv,ns+"SafetyFunction","TestName","String",true); Property(sv,ns+"SafetyFunction","Description","String",true);
        Property(sv,ns+"SafetyFunction","TestState","TestState",false); Property(sv,ns+"SafetyFunction","Conditions","ConditionComposition",false); Property(sv,ns+"SafetyFunction","TraceConfiguration","TraceConfiguration",false);
        Method(sv,ns+"SafetyFunction","ResetTestResult",Type.EmptyTypes,"Void"); Method(sv,ns+"SafetyFunction","Delete",Type.EmptyTypes,"Void");
        Method(sv,ns+"SafetyFunction","Export",new[]{file,exportOptions},"Void"); Method(sv,ns+"SafetyFunction","Export",new[]{file,exportOptions,documentInfo},"Void");
        check(serviceProvider.IsAssignableFrom(T(sv,ns+"SafetyFunction")),"SafetyFunction is a service provider (TestValidity)");
        Method(sv,ns+"SafetyFunctionComposition","Create",Type.EmptyTypes,"SafetyFunction"); Method(sv,ns+"SafetyFunctionComposition","CreateFrom",new[]{T(sv,ns+"SafetyFunction")},"SafetyFunction");
        Method(sv,ns+"SafetyFunctionComposition","Import",new[]{file,importOptions},"IList`1");
        Property(sv,ns+"Condition","Comment","String",true); Property(sv,ns+"Condition","DeviceName","String",true); Property(sv,ns+"Condition","SignalName","String",true); Property(sv,ns+"Condition","SignalUsage","SignalUsage",true);
        Property(sv,ns+"Condition","InitialInput","Object",true); Property(sv,ns+"Condition","ExecutedInput","Object",true); Property(sv,ns+"Condition","Response","Object",true);
        Method(sv,ns+"Condition","Delete",Type.EmptyTypes,"Void"); check(serviceProvider.IsAssignableFrom(T(sv,ns+"Condition")),"Condition is a service provider (TestValidity)");
        Method(sv,ns+"ConditionComposition","Create",new[]{deviceItem,T(sv,ns+"SignalUsage"),text},"Condition");
        Property(sv,ns+"TraceConfiguration","IsTraced","Boolean",true); check(serviceProvider.IsAssignableFrom(T(sv,ns+"TraceConfiguration")),"TraceConfiguration is a service provider (TestValidity)");
        check(T(core,"Siemens.Engineering.IEngineeringObject").GetMethod("SetAttribute",new[]{text,typeof(object)})!=null,"IEngineeringObject.SetAttribute (TraceConfiguration PretriggerTime / RecordingDuration / Signals per manual)");
        check(server.GetType("TiaMcpServer.Siemens.SafetyValidationLogic",true)!.GetMethod("ValidateConditionValues",BindingFlags.NonPublic|BindingFlags.Static)!=null,"SafetyValidationLogic.ValidateConditionValues (2.7.43: NotRelevant refused per signal usage before any write)");
    }
}
