using System;
using System.Linq;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using TiaMcpServer.Siemens;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcpServer.Tests
{
    internal static class HmiInspectionTests
    {
        public sealed class Root { public List<Screen> Screens {get;}=new List<Screen>(); public List<Group> ScreenGroups{get;}=new List<Group>(); }
        public sealed class Group
        {
            public string Name{get;set;}=""; public List<Group> Groups{get;}=new List<Group>();public List<Screen> Screens{get;}=new List<Screen>();
            public Action? OnDelete;public void Delete()=>OnDelete?.Invoke();
        }
        public sealed class Screen { public string Name{get;set;}="";public List<Button> ScreenItems{get;}=new List<Button>(); }
        public sealed class Button { public string Name{get;set;}="";public Events EventHandlers{get;}=new Events();public List<Dyn> Dynamizations{get;}=new List<Dyn>(); }
        public sealed class Script { public string ScriptCode{get;set;}="";public string GlobalDefinitionAreaScriptCode{get;set;}="";public bool Async{get;set;} }
        public sealed class Handler
        {
            public EngineeringDefectTests.EventType EventType{get;set;} public Script Script{get;}=new Script();
            public Action? OnDelete;public void Delete()=>OnDelete?.Invoke();
        }
        public sealed class Events:List<Handler>
        {
            public int Creates;
            public Handler? Find(EngineeringDefectTests.EventType type)=>this.SingleOrDefault(x=>x.EventType==type);
            public Handler Create(EngineeringDefectTests.EventType type){Creates++;var h=new Handler{EventType=type};h.OnDelete=()=>Remove(h);Add(h);return h;}
        }
        public sealed class Dyn
        {
            public string PropertyName{get;set;}="";public string Tag{get;set;}="";public Action? OnDelete;public void Delete()=>OnDelete?.Invoke();
        }
        public sealed class Cycle { public Cycle Self=>this; public string Failure=>throw new InvalidOperationException("broken"); }
        private static bool Success(ResponseMessage result)=>result.Meta?["success"]?.GetValue<bool>()==true;
        internal static void Run(Action<bool,string> check)
        {
            Console.WriteLine("== Precise HMI read/delete/preview and bounded snapshot ==");
            var root=new Root();var group=new Group{Name="Group"};root.ScreenGroups.Add(group);group.OnDelete=()=>root.ScreenGroups.Remove(group);
            var screen=new Screen{Name="Main"};group.Screens.Add(screen);var button=new Button{Name="btn"};screen.ScreenItems.Add(button);
            HmiToolFixture.Session.FixtureRoot=root;
            var down=button.EventHandlers.Create(EngineeringDefectTests.EventType.Down);down.Script.ScriptCode="keep();";down.Script.Async=true;
            check(!Success(HmiToolFixture.HmiInspection.ReadUnifiedHmiButtonEvent("HMI","/Group/Main","btn","Tapped")) && button.EventHandlers.Count==1,"missing event read has no side effect");
            check(McpServer.CallTool("GetUnifiedHmiButtonEvent", new TiaMcp.Logic.V4.Inputs.ToolArguments(System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>("{\"softwarePath\":\"HMI\",\"screenPath\":\"/Group/Main\",\"buttonName\":\"btn\",\"eventType\":\"Tapped\"}"))).IsError == true, "missing event CallTool business failure");
            var tapped=button.EventHandlers.Create(EngineeringDefectTests.EventType.Tapped);
            var read=HmiToolFixture.HmiInspection.ReadUnifiedHmiButtonEvent("HMI","/Group/Main","btn","Down");
            check(Success(read) && read.Meta!["event"]!["ScriptCode"]!.ToString()=="keep();" && read.Meta["event"]!["Async"]!.GetValue<bool>(),"read exact script global and Async DTO");
            check(!Success(HmiToolFixture.HmiInspection.DeleteUnifiedHmiButtonEvent("HMI","/Group/Main","btn","Down")),"nonempty event deletion refused");
            var preview=HmiToolFixture.HmiInspection.DeleteUnifiedHmiButtonEvent("HMI","/Group/Main","btn","Tapped");
            check(Success(preview)&&button.EventHandlers.Count==2,"default event delete is preview");
            var token=preview.Meta!["token"]!.ToString();tapped.Script.ScriptCode="edited();";
            check(!Success(HmiToolFixture.HmiInspection.DeleteUnifiedHmiButtonEvent("HMI","/Group/Main","btn","Tapped",false,token,false))&&button.EventHandlers.Count==2,"stale token protects user edit");
            tapped.Script.ScriptCode="";
            check(Success(HmiToolFixture.HmiInspection.DeleteUnifiedHmiButtonEvent("HMI","/Group/Main","btn","Tapped",false,token))&&button.EventHandlers.Count==1&&down.Script.ScriptCode=="keep();"&&down.Script.Async,"exact delete preserves Down script and Async");
            check(!Success(HmiToolFixture.HmiInspection.ReadUnifiedHmiButtonEvent("HMI","/Group/Main","btn","1")),"numeric enum is invalid through tool boundary");
            var left=new Dyn{PropertyName="Left",Tag="position"};var text=new Dyn{PropertyName="AlternateText",Tag="caption"};
            button.Dynamizations.AddRange(new[]{left,text});left.OnDelete=()=>button.Dynamizations.Remove(left);
            var dynPreview=HmiToolFixture.HmiInspection.DeleteUnifiedHmiDynamization("HMI","/Group/Main","btn","Left");
            check(Success(dynPreview)&&button.Dynamizations.Count==2,"dynamization default preview");
            check(Success(HmiToolFixture.HmiInspection.DeleteUnifiedHmiDynamization("HMI","/Group/Main","btn","Left",false,dynPreview.Meta!["token"]!.ToString()))&&button.Dynamizations.Single()==text,"delete one property preserves others");
            check(!Success(HmiToolFixture.HmiInspection.DeleteEmptyUnifiedHmiScreenGroup("HMI","/Group",false))&&root.ScreenGroups.Count==1,"nonempty group rejected");
            group.Screens.Clear();
            check(Success(HmiToolFixture.HmiInspection.DeleteEmptyUnifiedHmiScreenGroup("HMI","/Group"))&&root.ScreenGroups.Count==1,"empty group preview");
            check(Success(HmiToolFixture.HmiInspection.DeleteEmptyUnifiedHmiScreenGroup("HMI","/Group",false))&&root.ScreenGroups.Count==0,"empty group deletion verified");
            check(!Success(HmiToolFixture.HmiInspection.DeleteEmptyUnifiedHmiScreenGroup("HMI","/",false)),"root group never deleted");
            root.Screens.Add(new Screen{Name="Main"});group.Screens.Add(screen);root.ScreenGroups.Add(group);
            check(!Success(HmiToolFixture.HmiInspection.ReadHmiScreenSnapshot("HMI","Main")),"ambiguous bare screen name rejected");
            check(Success(HmiToolFixture.HmiInspection.ReadHmiScreenSnapshot("HMI","/Group/Main")),"full nested screen path unambiguous");
            var paths=HmiToolFixture.HmiInspection.ListHmiScreenPaths("HMI",0,1);
            check(Success(paths)&&paths.Meta!["total"]!.GetValue<int>()==2&&paths.Meta["nextOffset"]!.GetValue<int>()==1,"screen paths paginate with total");
            var snapshot=HmiSnapshot.Capture(new Cycle());
            check(snapshot["incomplete"]!.GetValue<bool>()&&!snapshot["restorableBackup"]!.GetValue<bool>()&&snapshot.ToJsonString().Contains("ReadFailed"),"snapshot records failures and cycles safely");
            check(HmiSnapshot.Capture(root,1,2)["incomplete"]!.GetValue<bool>(),"snapshot limits expose incompleteness");
            HmiToolFixture.Session.FixtureRoot=null;
            check(!Success(HmiToolFixture.HmiInspection.ListHmiScreenPaths("HMI")),"missing project remains business failure");
        }
    }
}
