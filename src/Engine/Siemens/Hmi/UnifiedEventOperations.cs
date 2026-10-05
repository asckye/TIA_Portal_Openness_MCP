using System;
using System.Linq;
using System.Text.Json.Nodes;
namespace TiaMcpServer.Siemens
{
    internal static class UnifiedEventOperations
    {
        internal static object? Find(object handlers,string eventType,string propertyName)
        {
            var rows=EngineeringGroupOperations.Items(handlers).Where(x=>
                string.Equals(EngineeringGroupOperations.Get(x,"EventType").ToString(),eventType,StringComparison.Ordinal) &&
                (propertyName=="" || string.Equals(EngineeringGroupOperations.Get(x,"PropertyName").ToString(),propertyName,StringComparison.Ordinal))).Take(2).ToArray();
            if(rows.Length>1)throw new InvalidOperationException("Ambiguous event; no mutation performed.");return rows.SingleOrDefault();
        }
        internal static (Type[] Types,object[] Values) Creation(object handlers,string eventType,string propertyName)
        {
            int count=propertyName=="" ? 1 : 2;
            var method=handlers.GetType().GetMethods().SingleOrDefault(m=>m.Name=="Create"&&m.GetParameters().Length==count&&
                (count==1 || m.GetParameters()[0].ParameterType==typeof(string)) && (m.GetParameters().Last().ParameterType.IsEnum || m.GetParameters().Last().ParameterType==typeof(string)))
                ?? throw new NotSupportedException("Native event creation signature unavailable.");
            var types=method.GetParameters().Select(p=>p.ParameterType).ToArray();
            var value=EngineeringScalarProperties.ConvertValue(JsonValue.Create(eventType),types.Last())!;
            return (types,count==1 ? new[]{value} : new object[]{propertyName,value});
        }
    }
}
