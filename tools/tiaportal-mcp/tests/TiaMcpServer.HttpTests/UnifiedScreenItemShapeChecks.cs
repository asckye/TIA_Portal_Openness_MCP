using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Web.Script.Serialization;

// Native members used by the Unified screen item family (Portal.UnifiedScreenItems.cs), verified against the installed
// API, plus the engine's own reflective catalog run against that API so every concrete item type is accounted for.
// JSON results of the engine are read back through their ToJsonString() and System.Web's serializer, so the harness
// stays free of the engine's System.Text.Json version.
internal static class UnifiedScreenItemShapeChecks
{
    internal static void Run(Assembly server, Action<bool,string> check)
    {
        Assembly Api(string split,string legacy) { try { return Assembly.Load(split); } catch(System.IO.FileNotFoundException) { return Assembly.Load(legacy); } }
        var unified=Api("Siemens.Engineering.WinCCUnified","Siemens.Engineering");
        var core=Api("Siemens.Engineering.Base","Siemens.Engineering");
        bool v20=core.GetName().Version!.Major==20;
        Type T(Assembly a,string n)=>a.GetType(n,true)!;
        const string ui="Siemens.Engineering.HmiUnified.UI.";
        var serializer=new JavaScriptSerializer{MaxJsonLength=int.MaxValue};
        object Json(object node)=>serializer.DeserializeObject((string)node.GetType().GetMethods().First(m=>m.Name=="ToJsonString" && m.GetParameters().Length==1).Invoke(node,new object?[]{null})!)!;
        Dictionary<string,object> Obj(object o)=>(Dictionary<string,object>)o;
        // Composition and base class
        var composition=T(unified,ui+"Base.HmiScreenItemBaseComposition");
        var creates=composition.GetMethods(BindingFlags.Instance|BindingFlags.Public).Where(m=>m.Name=="Create" && m.IsGenericMethodDefinition).ToArray();
        check(creates.Any(m=>m.GetParameters().Length==1 && m.GetParameters()[0].ParameterType==typeof(string)),"HmiScreenItemBaseComposition.Create<T>(string name)");
        check(creates.Any(m=>m.GetParameters().Length==2 && m.GetParameters().All(p=>p.ParameterType==typeof(string))),"HmiScreenItemBaseComposition.Create<T>(string name, string containedType)");
        var itemBase=T(unified,ui+"Base.HmiScreenItemBase");
        check(itemBase.GetMethod("Delete",Type.EmptyTypes)?.ReturnType==typeof(void),"HmiScreenItemBase.Delete() -> Void");
        foreach(var name in new[]{"Name","Visible","Enabled","TabIndex"}) check(itemBase.GetProperty(name)!=null,"HmiScreenItemBase."+name);
        check(T(unified,ui+"Screens.HmiScreen").GetProperty("ScreenItems")?.PropertyType==composition,"HmiScreen.ScreenItems : HmiScreenItemBaseComposition");
        // Feature interfaces the schema reports
        Type[] loadable; try { loadable=unified.GetTypes(); } catch(ReflectionTypeLoadException ex) { loadable=ex.Types.Where(t=>t!=null).ToArray()!; }
        var features=loadable.Where(t=>t.IsInterface && t.Namespace==ui+"Features").Select(t=>t.Name).OrderBy(n=>n).ToArray();
        foreach(var f in new[]{"IHmiBoxFeature","IHmiAreaFeature","IHmiLineFeature","IHmiRotationFeature","IHmiOperabilityFeature","IHmiScreenWindowFeature"}) check(features.Contains(f),"UI.Features."+f);
        // Engine catalog against this API: every concrete item type must be a creatable HmiScreenItemBase subclass with Dynamizations and EventHandlers.
        var logic=server.GetType("TiaMcpServer.Siemens.UnifiedScreenItemLogic",true)!;
        var catalog=(object[])Json(logic.GetMethod("Catalog",BindingFlags.NonPublic|BindingFlags.Static)!.Invoke(null,new object[]{unified})!);
        var typeList=((IEnumerable)logic.GetMethod("ItemTypes",BindingFlags.NonPublic|BindingFlags.Static)!.Invoke(null,new object[]{unified})!).Cast<Type>().ToArray();
        check(typeList.Length==catalog.Length && typeList.Length>=(v20?37:43),"catalog lists "+typeList.Length+" concrete screen item types (V20 37 / V21 43)");
        check(typeList.All(t=>itemBase.IsAssignableFrom(t) && !t.IsAbstract && !t.Name.EndsWith("Base")),"every catalog type derives from HmiScreenItemBase, is concrete and not a *Base class");
        check(typeList.All(t=>t.GetProperties().Any(p=>p.Name=="Dynamizations")),"every catalog type exposes Dynamizations");
        check(typeList.Where(t=>!t.GetProperties().Any(p=>p.Name=="EventHandlers")).Select(t=>t.Name).SequenceEqual(new[]{"HmiLabel"}),"every catalog type except HmiLabel exposes EventHandlers (schema reports eventTypes=null there)");
        var hiding=typeList.Where(t=>t.GetProperties().Count(p=>p.Name=="EventHandlers")>1).Select(t=>t.Name).OrderBy(n=>n).ToArray();
        check(hiding.Length>0 && hiding.All(n=>new[]{"HmiSlider","HmiToggleSwitch","HmiCircleSegment","HmiEllipseSegment"}.Contains(n)),"EventHandlers is hidden by a narrower composition on "+string.Join("/",hiding)+" (engine resolves the most derived declaration)");
        foreach(var name in new[]{"HmiCircle","HmiRectangle","HmiText","HmiLine","HmiPolygon","HmiGraphicView","HmiButton","HmiIOField","HmiSymbolicIOField","HmiSlider","HmiGauge","HmiBar","HmiClock","HmiTextBox","HmiToggleSwitch","HmiAlarmControl","HmiTrendControl","HmiScreenWindow","HmiFaceplateContainer"})
            check(typeList.Any(t=>t.Name==name),"catalog contains "+name);
        var groups=catalog.Select(r=>(string)Obj(r)["group"]).Distinct().OrderBy(g=>g).ToArray();
        check(groups.SequenceEqual(new[]{"Controls","Screens","Shapes","Widgets"}),"catalog groups are Controls/Screens/Shapes/Widgets");
        // Resolution and schema on the real API
        var resolve=logic.GetMethod("ResolveItemType",BindingFlags.NonPublic|BindingFlags.Static)!;
        check(((Type)resolve.Invoke(null,new object[]{unified,"Circle"})!).FullName==ui+"Shapes.HmiCircle","ResolveItemType(Circle) -> UI.Shapes.HmiCircle");
        check(((Type)resolve.Invoke(null,new object[]{unified,"Widgets.HmiSlider"})!).Name=="HmiSlider","ResolveItemType(Widgets.HmiSlider)");
        var describe=logic.GetMethod("TypeDescription",BindingFlags.NonPublic|BindingFlags.Static)!;
        Dictionary<string,Dictionary<string,object>> Props(object description)=>((object[])Obj(description)["properties"]).Select(Obj).ToDictionary(p=>(string)p["name"],p=>p);
        var circle=Json(describe.Invoke(null,new object[]{T(unified,ui+"Shapes.HmiCircle"),2})!);
        var props=Props(circle);
        check((string)props["BackColor"]["kind"]=="color" && ((object[])props["BackFillPattern"]["enumValues"]).Length>0,"HmiCircle schema: BackColor is color, BackFillPattern lists enum values");
        check((string)props["Radius"]["kind"]=="scalar" && (bool)props["Radius"]["writable"],"HmiCircle schema: Radius scalar writable");
        var slider=Json(describe.Invoke(null,new object[]{T(unified,ui+"Widgets.HmiSlider"),1})!);
        check(((object[])Obj(slider)["eventTypes"]).Length>0 && Props(slider).Count(p=>p.Key=="EventHandlers")==1,"HmiSlider schema resolves the hidden EventHandlers once with its own event types");
        var button=Json(describe.Invoke(null,new object[]{T(unified,ui+"Widgets.HmiButton"),2})!);
        var bprops=Props(button);
        check((string)bprops["Text"]["kind"]=="multilingual" && (string)bprops["Font"]["kind"]=="part" && ((object[])bprops["Font"]["properties"]).Select(Obj).Any(p=>(string)p["name"]=="Size"),"HmiButton schema: Text multilingual, Font part expanded with Size");
        check(((object[])Obj(button)["eventTypes"]).Cast<string>().Contains("Tapped"),"HmiButton schema: event types include Tapped");
        var window=Json(describe.Invoke(null,new object[]{T(unified,ui+"Screens.HmiScreenWindow"),1})!);
        check(((object[])Obj(window)["features"]).Cast<string>().Contains("IHmiScreenWindowFeature"),"HmiScreenWindow schema: IHmiScreenWindowFeature");
        if(v20) Console.WriteLine("CAPABILITY V20 Unified screen item catalog: "+typeList.Length+" types from the merged Siemens.Engineering assembly.");
        // Tool surface
        var tools=server.GetType("TiaMcpServer.ModelContextProtocol.McpServer",true)!;
        check(Equals(tools.GetMethod("ManageUnifiedScreenItem")!.GetParameters().Single(p=>p.Name=="dryRun").DefaultValue,true),"ManageUnifiedScreenItem defaults to preview");
        check(Equals(tools.GetMethod("ManageUnifiedScreenItem")!.GetParameters().Single(p=>p.Name=="confirmDelete").DefaultValue,false),"ManageUnifiedScreenItem requires explicit confirmDelete");
        check(tools.GetMethod("DescribeUnifiedScreenItemType")!.GetParameters().All(p=>p.Name!="softwarePath"),"DescribeUnifiedScreenItemType needs no project");
    }
}
