using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using TiaMcpServer.Siemens;
namespace TiaMcpServer.Tests
{
    internal static class ExtendedEngineeringTests
    {
        public enum EventKind { Changed, Loaded }
        public sealed class Handler {public EventKind EventType{get;set;} public string PropertyName{get;set;}="";}
        public sealed class Events:List<Handler> {public int Creates;public Handler Create(EventKind kind){Creates++;var h=new Handler{EventType=kind};Add(h);return h;}}
        public sealed class PropertyEvents:List<Handler> {public Handler Create(string property,EventKind kind){var h=new Handler{PropertyName=property,EventType=kind};Add(h);return h;}}
        internal static void Run(Action<bool,string> check)
        {
            bool Fails(Action action){try{action();return false;}catch{return true;}}
            var events=new Events();
            check(UnifiedEventOperations.Find(events,"Loaded","")==null&&events.Creates==0,"reading missing event never creates it");
            var shape=UnifiedEventOperations.Creation(events,"Loaded","");
            check(shape.Types.Length==1&&Equals(shape.Values[0],EventKind.Loaded)&&events.Creates==0,"event preview resolves native enum without creating");
            check(Fails(()=>UnifiedEventOperations.Creation(events,"999","")),"undefined event enum rejected");
            check(Fails(()=>UnifiedEventOperations.Creation(events,"loaded","")),"event enum names are exact");
            var handler=events.Create(EventKind.Loaded);
            check(ReferenceEquals(handler,UnifiedEventOperations.Find(events,"Loaded","")),"exact screen event resolved");
            events.Create(EventKind.Loaded);
            check(Fails(()=>UnifiedEventOperations.Find(events,"Loaded","")),"duplicate event rejected before mutation");
            var props=new PropertyEvents();var top=props.Create("Top",EventKind.Changed);props.Create("Left",EventKind.Changed);
            check(ReferenceEquals(top,UnifiedEventOperations.Find(props,"Changed","Top")),"property event key includes property name");
            var propertyShape=UnifiedEventOperations.Creation(props,"Changed","Top");
            check(propertyShape.Values.Length==2&&Equals(propertyShape.Values[0],"Top")&&Equals(propertyShape.Values[1],EventKind.Changed),"property creation preserves native argument order");
            check(NativeOutcome.Failed(JsonNode.Parse("{\"values\":{\"State\":\"Failure\"}}")),"native Failure cannot become successful API result");
            check(NativeOutcome.Failed(JsonNode.Parse("{\"values\":{\"State\":\"Success\",\"ErrorCount\":1}}")),"error count overrides Success state");
            check(NativeOutcome.Failed(JsonNode.Parse("{\"values\":{\"State\":\"Success\"},\"Messages\":[{\"values\":{\"State\":\"Error\"}}]}")),"nested native error is not silently ignored");
            check(!NativeOutcome.Failed(JsonNode.Parse("{\"values\":{\"State\":\"Success\",\"Enabled\":false}}")),"false data property is not a failure result");
            check(NativeOutcome.Failed(JsonValue.Create(false)),"native false return detected");
            check(!NativeOutcome.Failed(null),"void return does not fabricate a native failure");
            check(NativeOutcome.Failed(JsonNode.Parse("{\"Errors\":[\"invalid definition\"]}")),"native Errors list detected");
        }
    }
}
