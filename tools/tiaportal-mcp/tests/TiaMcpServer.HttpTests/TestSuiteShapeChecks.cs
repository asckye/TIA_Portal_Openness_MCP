using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

// Native members used by the 2.7.42 phase 6 ⑥-③ Test Suite tools (Portal.TestSuite.cs), verified member by member against the
// installed V20 / V21 PublicAPI (Siemens.Engineering.TestSuite is a separate assembly on both versions; the surface is identical).
internal static class TestSuiteShapeChecks
{
    internal static void Run(Assembly server, Action<bool,string> check)
    {
        Assembly Api(string split,string legacy) { try { return Assembly.Load(split); } catch(FileNotFoundException) { return Assembly.Load(legacy); } }
        var core=Api("Siemens.Engineering.Base","Siemens.Engineering");
        var step7=Api("Siemens.Engineering.Step7","Siemens.Engineering");
        var ts=Api("Siemens.Engineering.TestSuite","Siemens.Engineering");          // V20: inside the monolithic Siemens.Engineering.dll
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
        var engineeringService=T(core,"Siemens.Engineering.IEngineeringService"); var serviceProvider=T(core,"Siemens.Engineering.IEngineeringServiceProvider"); var engineeringObject=T(core,"Siemens.Engineering.IEngineeringObject");
        const string ns="Siemens.Engineering.TestSuite."; const string sg=ns+"StyleGuide."; const string at=ns+"ApplicationTest."; const string st=ns+"SystemTest.";
        var text=typeof(string); var file=typeof(FileInfo); var directory=typeof(DirectoryInfo);
        var importOptions=T(core,"Siemens.Engineering.ImportOptions"); var masterCopy=T(core,"Siemens.Engineering.Library.MasterCopies.MasterCopy"); var plcSoftware=T(step7,"Siemens.Engineering.SW.PlcSoftware");

        // ---- enums (names mirrored in TestSuiteLogic) ----
        Enum(ts,sg+"RSLoadOptions","None","IgnorePropertyErrors","IgnoreMissingAttributes","SkipInvalidObjects");
        Enum(ts,sg+"UpdateOptions","Add","Override");
        Enum(ts,at+"TCLoadOptions","None","IgnoreInvalidObject"); Enum(ts,at+"TSLoadOptions","None","IgnoreInvalidObject"); Enum(ts,st+"TCLoadOptions","None","IgnoreInvalidObject");
        Enum(ts,at+"ExecutionMode","SystemManagedPLCSIMInstance","ExternallyManagedPLCSIMInstance");
        Enum(ts,st+"ServerInterfaces","UserDefined","StandardSIMATIC","SiOMECompanionSpecification");
        Enum(ts,ns+"TestResultsState","Success","Information","Warning","Error");

        // ---- service and system groups ----
        check(engineeringService.IsAssignableFrom(T(ts,ns+"TestSuiteService")),"TestSuiteService is an IEngineeringService (Project.GetService)");
        check(serviceProvider.IsAssignableFrom(T(core,"Siemens.Engineering.ProjectBase")),"ProjectBase is a service provider (TestSuiteService)");
        Property(ts,ns+"TestSuiteService","StyleGuideGroup","StyleGuideSystemGroup",false); Property(ts,ns+"TestSuiteService","ApplicationTestGroup","ApplicationTestSystemGroup",false); Property(ts,ns+"TestSuiteService","SystemTestGroup","SystemTestSystemGroup",false);
        Property(ts,sg+"StyleGuideSystemGroup","RuleSets","RuleSetComposition",false); check(serviceProvider.IsAssignableFrom(T(ts,sg+"StyleGuideSystemGroup")),"StyleGuideSystemGroup is a service provider (RuleSetExecutor)");
        Property(ts,at+"ApplicationTestSystemGroup","TestCases","TestCaseComposition",false); Property(ts,at+"ApplicationTestSystemGroup","ApplicationTestSets","ApplicationTestSetComposition",false);
        check(serviceProvider.IsAssignableFrom(T(ts,at+"ApplicationTestSystemGroup")),"ApplicationTestSystemGroup is a service provider (TestCaseExecutor)");
        Method(ts,at+"ApplicationTestSystemGroup","LoadFromFile",new[]{file,importOptions,T(ts,at+"TSLoadOptions")},"IList`1");
        Property(ts,st+"SystemTestSystemGroup","SystemTestCases","SystemTestCaseComposition",false); check(serviceProvider.IsAssignableFrom(T(ts,st+"SystemTestSystemGroup")),"SystemTestSystemGroup is a service provider (SystemTestCaseExecutor)");

        // ---- rule sets ----
        Property(ts,sg+"RuleSet","Name","String",true); Method(ts,sg+"RuleSet","Delete",Type.EmptyTypes,"Void"); Method(ts,sg+"RuleSet","ShowInEditor",Type.EmptyTypes,"Void"); Method(ts,sg+"RuleSet","SaveToFile",new[]{file},"Boolean");
        Method(ts,sg+"RuleSet","CopyScope",new[]{T(ts,sg+"RuleSet")},"Boolean"); Method(ts,sg+"RuleSet","SetScope",new[]{T(ts,sg+"UpdateOptions"),typeof(IEnumerable<>).MakeGenericType(engineeringObject)},"Boolean");
        Method(ts,sg+"RuleSetComposition","Find",new[]{text},"RuleSet"); Method(ts,sg+"RuleSetComposition","CreateFrom",new[]{masterCopy},"RuleSet"); Method(ts,sg+"RuleSetComposition","LoadFromFile",new[]{file,importOptions,T(ts,sg+"RSLoadOptions")},"IList`1");
        check(engineeringService.IsAssignableFrom(T(ts,sg+"RuleSetExecutor")),"RuleSetExecutor is an IEngineeringService");
        Method(ts,sg+"RuleSetExecutor","Run",new[]{T(ts,sg+"RuleSet")},"TestResults"); Method(ts,sg+"RuleSetExecutor","Run",new[]{typeof(IEnumerable<>).MakeGenericType(T(ts,sg+"RuleSet"))},"TestResults"); Method(ts,sg+"RuleSetExecutor","Run",new[]{T(ts,sg+"StyleGuideSystemGroup")},"TestResults");
        // official style-guide scope objects (IEngineeringObject): Project, DeviceGroups[x], CPU DeviceItem, the three system groups, UnitGroup
        foreach(var scope in new[]{"Siemens.Engineering.ProjectBase","Siemens.Engineering.HW.DeviceUserGroup","Siemens.Engineering.HW.DeviceItem"}) check(engineeringObject.IsAssignableFrom(T(core,scope)),scope+" is an IEngineeringObject (style-guide scope)");
        foreach(var scope in new[]{"Siemens.Engineering.SW.Blocks.PlcBlockSystemGroup","Siemens.Engineering.SW.Tags.PlcTagTableSystemGroup","Siemens.Engineering.SW.Types.PlcTypeSystemGroup","Siemens.Engineering.SW.Units.PlcUnitSystemGroup"}) check(engineeringObject.IsAssignableFrom(T(step7,scope)),scope+" is an IEngineeringObject (style-guide scope)");
        Method(core,"Siemens.Engineering.HW.DeviceUserGroupComposition","Find",new[]{text},"DeviceUserGroup");

        // ---- application test cases / test sets ----
        Property(ts,at+"TestCase","Name","String",true); Method(ts,at+"TestCase","Delete",Type.EmptyTypes,"Void"); Method(ts,at+"TestCase","ShowInEditor",Type.EmptyTypes,"Void"); Method(ts,at+"TestCase","SaveToFile",new[]{file},"Boolean");
        Method(ts,at+"TestCase","GetScope",Type.EmptyTypes,"String"); Method(ts,at+"TestCase","SetScope",new[]{plcSoftware},"Boolean"); Method(ts,at+"TestCase","SetScope",new[]{plcSoftware,text,T(ts,at+"ExecutionMode")},"Void");
        Method(ts,at+"TestCaseComposition","Find",new[]{text},"TestCase"); Method(ts,at+"TestCaseComposition","CreateFrom",new[]{masterCopy},"TestCase"); Method(ts,at+"TestCaseComposition","LoadFromFile",new[]{file,importOptions,T(ts,at+"TCLoadOptions")},"IList`1");
        Property(ts,at+"ApplicationTestSet","Name","String",true); Method(ts,at+"ApplicationTestSet","Delete",Type.EmptyTypes,"Void"); Method(ts,at+"ApplicationTestSet","ShowInEditor",Type.EmptyTypes,"Void");
        Method(ts,at+"ApplicationTestSetComposition","Find",new[]{text},"ApplicationTestSet");
        check(T(ts,at+"ApplicationTestSet").GetMethod("SaveToFile")==null && T(ts,at+"ApplicationTestSetComposition").GetMethod("CreateFrom")==null,"ApplicationTestSet has no SaveToFile / CreateFrom (test sets are imported via ApplicationTestSystemGroup.LoadFromFile)");
        check(engineeringService.IsAssignableFrom(T(ts,at+"TestCaseExecutor")),"TestCaseExecutor is an IEngineeringService");
        Method(ts,at+"TestCaseExecutor","Run",new[]{T(ts,at+"TestCase")},"TestResults"); Method(ts,at+"TestCaseExecutor","Run",new[]{typeof(IEnumerable<>).MakeGenericType(T(ts,at+"TestCase"))},"TestResults"); Method(ts,at+"TestCaseExecutor","Run",new[]{T(ts,at+"ApplicationTestSystemGroup")},"TestResults");
        Method(ts,at+"TestCaseExecutor","Run",new[]{T(ts,at+"ApplicationTestSet")},"TestResults"); Method(ts,at+"TestCaseExecutor","Run",new[]{typeof(IEnumerable<>).MakeGenericType(T(ts,at+"ApplicationTestSet"))},"TestResults");
        check(T(ts,at+"SupportSimulationNotEnabledException").IsSubclassOf(T(core,"Siemens.Engineering.EngineeringTargetInvocationException")),"SupportSimulationNotEnabledException is recoverable (EngineeringTargetInvocationException)");

        // ---- system test cases ----
        Property(ts,st+"SystemTestCase","Name","String",true); Property(ts,st+"SystemTestCase","OPCUAServerAddress","String",false); Property(ts,st+"SystemTestCase","OPCUAServerInterfaceType","ServerInterfaces",false); Property(ts,st+"SystemTestCase","OPCUAServerInterfaceFolderPath","DirectoryInfo",false);
        Method(ts,st+"SystemTestCase","Delete",Type.EmptyTypes,"Void"); Method(ts,st+"SystemTestCase","SaveToFile",new[]{file},"Boolean");
        Method(ts,st+"SystemTestCase","SetScope",new[]{text,T(ts,st+"ServerInterfaces")},"Void"); Method(ts,st+"SystemTestCase","SetScope",new[]{text,T(ts,st+"ServerInterfaces"),directory},"Void");
        check(T(ts,st+"SystemTestCase").GetMethod("ShowInEditor")==null,"SystemTestCase has no ShowInEditor (tool refuses it)");
        Method(ts,st+"SystemTestCaseComposition","Find",new[]{text},"SystemTestCase"); Method(ts,st+"SystemTestCaseComposition","CreateFrom",new[]{masterCopy},"SystemTestCase"); Method(ts,st+"SystemTestCaseComposition","LoadFromFile",new[]{file,importOptions,T(ts,st+"TCLoadOptions")},"IList`1");
        check(engineeringService.IsAssignableFrom(T(ts,st+"SystemTestCaseExecutor")),"SystemTestCaseExecutor is an IEngineeringService");
        Method(ts,st+"SystemTestCaseExecutor","Run",new[]{T(ts,st+"SystemTestCase")},"TestResults"); Method(ts,st+"SystemTestCaseExecutor","Run",new[]{typeof(IEnumerable<>).MakeGenericType(T(ts,st+"SystemTestCase"))},"TestResults"); Method(ts,st+"SystemTestCaseExecutor","Run",new[]{T(ts,st+"SystemTestSystemGroup")},"TestResults");
        check(T(ts,st+"OpcUaServerAddressNotValidException").IsSubclassOf(T(core,"Siemens.Engineering.EngineeringTargetInvocationException")),"OpcUaServerAddressNotValidException is recoverable");

        // ---- results ----
        Property(ts,ns+"TestResults","State","TestResultsState",false); Property(ts,ns+"TestResults","ErrorCount","Int32",false); Property(ts,ns+"TestResults","WarningCount","Int32",false); Property(ts,ns+"TestResults","Messages","TestResultsMessageComposition",false);
        Property(ts,ns+"TestResultsMessage","Path","String",false); Property(ts,ns+"TestResultsMessage","State","TestResultsState",false); Property(ts,ns+"TestResultsMessage","Description","String",false); Property(ts,ns+"TestResultsMessage","DateTime","DateTime",false);
        Property(ts,ns+"TestResultsMessage","ErrorCount","Int32",false); Property(ts,ns+"TestResultsMessage","WarningCount","Int32",false); Property(ts,ns+"TestResultsMessage","Messages","TestResultsMessageComposition",false);
        check(typeof(IEnumerable<>).MakeGenericType(T(ts,ns+"TestResultsMessage")).IsAssignableFrom(T(ts,ns+"TestResultsMessageComposition")),"TestResultsMessageComposition enumerates TestResultsMessage (recursive walk)");

        // ---- server side ----
        var portal=server.GetType("TiaMcpServer.Siemens.Portal",true)!;
        foreach(var tool in new[]{"ReadTestSuiteCases","ExchangeTestSuiteCase","RunTestSuiteCase","ManageTestSuiteCase"}) check(portal.GetMethod(tool)!=null,"Portal."+tool+" exists");
        check(portal.GetMethod("RunTestSuiteCase")!.GetParameters().Any(p=>p.Name=="namesJson") && portal.GetMethod("RunTestSuiteCase")!.GetParameters().Any(p=>p.Name=="runAll") && portal.GetMethod("ReadTestSuiteCases")!.GetParameters().Any(p=>p.Name=="kind"),"Test Suite tools take namesJson / runAll / kind (2.7.42 typed retrofit)");
        check(T(ts,at+"SupportSimulationNotEnabledException")!=null && T(core,"Siemens.Engineering.EngineeringTargetInvocationException")!=null,"empty-group Run failures are EngineeringTargetInvocationException (2.7.42 real project: application executor NRE, system executor 'No test case(s)') - runAll on an empty group is refused by the tool");
    }
}
