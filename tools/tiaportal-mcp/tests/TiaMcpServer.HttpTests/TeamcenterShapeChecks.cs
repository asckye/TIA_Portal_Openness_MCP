using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security;

// Native members used by the 2.7.42 phase 6 ⑥-③ Teamcenter Gateway tools (Portal.Teamcenter.cs), verified member by member against
// the installed V20 / V21 PublicAPI (Siemens.Engineering.TeamcenterGateway is a separate assembly on both versions; identical surface).
internal static class TeamcenterShapeChecks
{
    internal static void Run(Assembly server, Action<bool,string> check)
    {
        Assembly Api(string split,string legacy) { try { return Assembly.Load(split); } catch(FileNotFoundException) { return Assembly.Load(legacy); } }
        var core=Api("Siemens.Engineering.Base","Siemens.Engineering");
        var tc=Api("Siemens.Engineering.TeamcenterGateway","Siemens.Engineering");  // V20: inside the monolithic Siemens.Engineering.dll
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
        const string ns="Siemens.Engineering.TeamcenterGateway.";
        var text=typeof(string); var secure=typeof(SecureString);
        var info=T(tc,ns+"TcGatewayConnectionInfo"); var datasetType=T(tc,ns+"DatasetType"); var itemType=T(tc,ns+"ItemType"); var cache=T(tc,ns+"LocalCacheOption");
        var itemDelegate=T(tc,ns+"ItemDetailsDelegate"); var revisionDelegate=T(tc,ns+"RevisionDetailsDelegate"); var properties=typeof(IEnumerable<>).MakeGenericType(T(tc,ns+"TeamcenterProperty"));

        // ---- enums (names mirrored in TeamcenterLogic) ----
        Enum(tc,ns+"DatasetType","T4TiaProjectDataset","T4TiaLibraryDataset");
        Enum(tc,ns+"ItemType","Project","GlobalLibrary");
        Enum(tc,ns+"LocalCacheOption","Overwrite","DoNotOverwrite");
        Enum(tc,ns+"MappedCustomAttributeType","Char","Date","Double","Float","Integer","Boolean","Short","String");
        Enum(tc,ns+"ListOfValuesUsageType","Exhaustive","Suggestive","Range");

        // ---- connection ----
        check(engineeringService.IsAssignableFrom(T(tc,ns+"TeamcenterConnectionProvider")),"TeamcenterConnectionProvider is an IEngineeringService (TiaPortal.GetService)");
        check(serviceProvider.IsAssignableFrom(T(core,"Siemens.Engineering.TiaPortal")),"TiaPortal is a service provider");
        Method(tc,ns+"TeamcenterConnectionProvider","Connect",new[]{text,secure,text,text,text,text},"TcGatewayConnectionInfo");
        Method(tc,ns+"TeamcenterConnectionProvider","ConnectSSO",new[]{text,text,text,text},"TcGatewayConnectionInfo");
        Method(tc,ns+"TeamcenterConnectionProvider","Disconnect",new[]{info},"Void");
        Property(tc,ns+"TcGatewayConnectionInfo","Group","String",false); Property(tc,ns+"TcGatewayConnectionInfo","Role","String",false); Property(tc,ns+"TcGatewayConnectionInfo","SessionToken","String",false);
        check(T(tc,ns+"TcGatewayException").IsSubclassOf(T(core,"Siemens.Engineering.EngineeringTargetInvocationException")),"TcGatewayException is recoverable (EngineeringTargetInvocationException)");

        // ---- locks, search and download ----
        check(engineeringService.IsAssignableFrom(T(tc,ns+"TcGatewayLockProvider")),"TcGatewayLockProvider is an IEngineeringService");
        foreach(var name in new[]{"CheckoutDataset","CheckinDataset","CancelCheckoutDataset"}) Method(tc,ns+"TcGatewayLockProvider",name,new[]{info,text,text,datasetType,text},"Void");
        check(engineeringService.IsAssignableFrom(T(tc,ns+"TcGatewaySearchAndDownloadProvider")),"TcGatewaySearchAndDownloadProvider is an IEngineeringService");
        Method(tc,ns+"TcGatewaySearchAndDownloadProvider","Search",new[]{info,itemType,text,text,text,text},"IList`1");
        Method(tc,ns+"TcGatewaySearchAndDownloadProvider","Download",new[]{info,text,text,itemType,cache},"FileInfo");
        Property(tc,ns+"SearchResult","ItemId","String",false); Property(tc,ns+"SearchResult","RevisionId","IEnumerable`1",false);

        // ---- workflow ----
        check(engineeringService.IsAssignableFrom(T(tc,ns+"TcGatewayWorkflowProvider")),"TcGatewayWorkflowProvider is an IEngineeringService (Project / GlobalLibrary.GetService)");
        check(serviceProvider.IsAssignableFrom(T(core,"Siemens.Engineering.Library.GlobalLibrary")) && serviceProvider.IsAssignableFrom(T(core,"Siemens.Engineering.ProjectBase")),"GlobalLibrary and ProjectBase are service providers (workflow owner)");
        Method(tc,ns+"TcGatewayWorkflowProvider","GetTeamcenterCustomAttributes",new[]{info,text},"IList`1");
        Method(tc,ns+"TcGatewayWorkflowProvider","Save",new[]{info,cache},"ItemInfo"); Method(tc,ns+"TcGatewayWorkflowProvider","SaveWithProxyObject",new[]{info,cache},"ItemInfo");
        Method(tc,ns+"TcGatewayWorkflowProvider","SaveToItem",new[]{info,text,text,cache},"ItemInfo"); Method(tc,ns+"TcGatewayWorkflowProvider","SaveToItemWithProxyObject",new[]{info,text,text,cache},"ItemInfo");
        Method(tc,ns+"TcGatewayWorkflowProvider","SaveAsNewItem",new[]{info,itemDelegate,properties},"ItemInfo"); Method(tc,ns+"TcGatewayWorkflowProvider","SaveAsNewItemWithProxyObject",new[]{info,itemDelegate,properties},"ItemInfo");
        Method(tc,ns+"TcGatewayWorkflowProvider","SaveAsNewRevision",new[]{info,revisionDelegate,properties},"ItemInfo"); Method(tc,ns+"TcGatewayWorkflowProvider","SaveAsNewRevisionWithProxyObject",new[]{info,revisionDelegate,properties},"ItemInfo");
        Property(tc,ns+"ItemInfo","ItemId","String",false); Property(tc,ns+"ItemInfo","RevisionId","String",false); Property(tc,ns+"ItemInfo","ItemName","String",false); Property(tc,ns+"ItemInfo","ItemType","ItemType",false);
        foreach(var name in new[]{"ItemId","ItemName","RevisionId","TeamcenterItemType","Comment","TeamcenterFolder"}) Property(tc,ns+"ItemDetails",name,"String",true);
        Property(tc,ns+"ItemDetails","TeamcenterProject","String[]",true);
        Property(tc,ns+"RevisionDetails","RevisionId","String",true); Property(tc,ns+"RevisionDetails","Comment","String",true);
        check(itemDelegate.GetMethod("Invoke")!.GetParameters().Single().ParameterType==T(tc,ns+"ItemDetails") && revisionDelegate.GetMethod("Invoke")!.GetParameters().Single().ParameterType==T(tc,ns+"RevisionDetails"),"ItemDetailsDelegate / RevisionDetailsDelegate fill the details objects");
        Property(tc,ns+"TeamcenterProperty","Name","String",false); Property(tc,ns+"TeamcenterProperty","DataType","MappedCustomAttributeType",false); Property(tc,ns+"TeamcenterProperty","DefaultValue","String",false); Property(tc,ns+"TeamcenterProperty","IsRequired","Boolean",false);
        Property(tc,ns+"TeamcenterProperty","MappingLevel","Int32",false); Property(tc,ns+"TeamcenterProperty","MaxFieldLength","Int32",false); Property(tc,ns+"TeamcenterProperty","LowerBound","String",false); Property(tc,ns+"TeamcenterProperty","UpperBound","String",false); Property(tc,ns+"TeamcenterProperty","ListOfValueInfo","TcPropertyListOfValueInfo",false);
        Method(tc,ns+"TeamcenterProperty","SetValue",new[]{text,T(tc,ns+"ErrorCallback")},"Void");
        check(T(tc,ns+"ErrorCallback").GetMethod("Invoke")!.GetParameters().Single().ParameterType==text,"ErrorCallback(string errorMessage)");
        Property(tc,ns+"TcPropertyListOfValueInfo","LovUsageType","ListOfValuesUsageType",false); Property(tc,ns+"TcPropertyListOfValueInfo","LowerLimit","String",false); Property(tc,ns+"TcPropertyListOfValueInfo","UpperLimit","String",false); Property(tc,ns+"TcPropertyListOfValueInfo","Values","IEnumerable`1",false);

        // ---- server side ----
        var portal=server.GetType("TiaMcpServer.Siemens.Portal",true)!;
        foreach(var tool in new[]{"ManageTeamcenterConnection","ManageTeamcenterDataset","ManageTeamcenterWorkflow"}) check(portal.GetMethod(tool)!=null,"Portal."+tool+" exists");
        check(Equals(portal.GetMethod("ManageTeamcenterWorkflow")!.GetParameters().Single(p=>p.Name=="confirmSave").DefaultValue,false),"ManageTeamcenterWorkflow saves need confirmSave");
    }
}
