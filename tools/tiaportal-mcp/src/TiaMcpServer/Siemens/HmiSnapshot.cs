using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;

namespace TiaMcpServer.Siemens
{
    internal static class HmiSnapshot
    {
        internal static JsonObject Capture(object root, int maxDepth = 6, int maxNodes = 2000, int maxString = 65536)
        {
            maxDepth=Math.Max(1,Math.Min(12,maxDepth)); maxNodes=Math.Max(1,Math.Min(10000,maxNodes));
            var rows=new JsonArray();var seen=new Dictionary<object,string>(ReferenceComparer.Instance);
            var timer=Stopwatch.StartNew();bool incomplete=false; int omitted=0;
            Walk(root,"$",0);
            return new JsonObject { ["kind"]="InspectionSnapshot", ["restorableBackup"]=false,
                ["coverage"]="Public readable properties/collections within limits; Parent and indexers excluded; no hidden engineering attributes or library internals promised",
                ["incomplete"]=incomplete, ["omittedCount"]=omitted, ["nodeCount"]=rows.Count,
                ["maxDepth"]=maxDepth, ["maxNodes"]=maxNodes, ["maxStringLength"]=maxString, ["nodes"]=rows };
            void Walk(object? value,string path,int depth)
            {
                if(rows.Count>=maxNodes || timer.ElapsedMilliseconds>30000) { incomplete=true;omitted++;return; }
                var row=new JsonObject { ["path"]=path, ["type"]=value?.GetType().FullName, ["status"]="Read" };rows.Add(row);
                if(value==null) { row["value"]=null; return; }
                var type=value.GetType();
                if(value is string str) { row["value"]=str.Length<=maxString?str:str.Substring(0,maxString);if(str.Length>maxString){row["status"]="Truncated";incomplete=true;} return; }
                if(type.IsPrimitive || type.IsEnum || value is decimal || value is DateTime || value is Guid || value is TimeSpan)
                { row["value"]=value.ToString();return; }
                if(seen.TryGetValue(value,out var previous)) {row["referencePath"]=previous;return;}
                seen[value]=path;
                if(depth>=maxDepth) {row["status"]="DepthLimit";incomplete=true;return;}
                if(value is IEnumerable enumerable)
                {
                    int index=0;
                    try { foreach(var child in enumerable) { if(rows.Count>=maxNodes || timer.ElapsedMilliseconds>30000){incomplete=true;omitted++;break;} Walk(child,path+"/"+index++,depth+1); } }
                    catch(Exception ex){row["status"]="ReadFailed";row["error"]=(ex.InnerException??ex).Message;incomplete=true;}
                    return;
                }
                foreach(var property in type.GetProperties(BindingFlags.Public|BindingFlags.Instance).OrderBy(x=>x.Name,StringComparer.Ordinal))
                {
                    if(property.Name=="Parent" || !property.CanRead || property.GetIndexParameters().Length!=0) {omitted++;continue;}
                    var childPath=path+"/"+Uri.EscapeDataString(property.Name);
                    if(rows.Count>=maxNodes || timer.ElapsedMilliseconds>30000){incomplete=true;omitted++;break;}
                    try {Walk(property.GetValue(value),childPath,depth+1);}
                    catch(Exception ex){rows.Add(new JsonObject{["path"]=childPath,["status"]="ReadFailed",["error"]=(ex.InnerException??ex).Message});incomplete=true;}
                }
            }
        }
        private sealed class ReferenceComparer:IEqualityComparer<object>
        {
            internal static readonly ReferenceComparer Instance=new ReferenceComparer();
            public new bool Equals(object? x,object? y)=>ReferenceEquals(x,y);
            public int GetHashCode(object value)=>RuntimeHelpers.GetHashCode(value);
        }
    }
}
