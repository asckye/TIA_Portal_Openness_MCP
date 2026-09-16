using System;
using System.Linq;
using System.Text.Json.Nodes;
namespace TiaMcpServer.Siemens
{
    internal static class NativeOutcome
    {
        internal static bool Failed(JsonNode? node)
        {
            if(node is JsonValue primitive && primitive.TryGetValue<bool>(out var ok))return !ok;
            if(node is JsonArray array)return array.Any(x=>x is JsonObject && Failed(x));
            if(node is not JsonObject row)return false;
            var values=row["values"] as JsonObject ?? row;
            if(values["State"] is JsonValue state && state.TryGetValue<string>(out var name) && new[]{"Error","Failed","Failure","Aborted","Canceled","Cancelled"}.Contains(name,StringComparer.OrdinalIgnoreCase))return true;
            if(values["ErrorCount"] is JsonValue count && count.TryGetValue<int>(out int errors) && errors>0)return true;
            if(row["Errors"] is JsonArray list && list.Count>0)return true;
            return new[]{"Messages","Results"}.Any(key=>row[key]!=null&&Failed(row[key]));
        }
    }
}
